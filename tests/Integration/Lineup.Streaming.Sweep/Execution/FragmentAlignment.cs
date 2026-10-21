using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lineup.Streaming.Sweep;

/// <summary>One successful media response from a fresh observation window.</summary>
internal sealed record FragmentSample(string Session, string Representation, long Sequence, DateTimeOffset ReceivedUtc);

/// <summary>Representation metadata qualified by the currently owned startup and public manifest.</summary>
internal sealed record FragmentMetadata(string Session, string ManifestUrl, IReadOnlyDictionary<string, string> Representations);

/// <summary>Parses actual representation identifiers, never assumes that stream 0 is video.</summary>
internal static partial class FragmentAlignment
{
    /// <summary>Parses the current fragment URL shape, excluding query strings.</summary>
    public static FragmentSample? Parse(string url, DateTimeOffset receivedUtc)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var match = FragmentPattern().Match(uri.AbsolutePath);
        return match.Success && long.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var sequence)
            ? new FragmentSample(match.Groups[1].Value, match.Groups[2].Value, sequence, receivedUtc) : null;
    }

    /// <summary>Maps DASH representations to media types from the current manifest.</summary>
    public static IReadOnlyDictionary<string, string> Representations(string manifest)
    {
        var document = XDocument.Parse(manifest);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var adaptation in document.Descendants().Where(element => element.Name.LocalName == "AdaptationSet"))
        {
            foreach (var representation in adaptation.Elements().Where(element => element.Name.LocalName == "Representation"))
            {
                var id = representation.Attribute("id")?.Value;
                var type = representation.Attribute("mimeType")?.Value ?? adaptation.Attribute("contentType")?.Value ?? adaptation.Attribute("mimeType")?.Value;
                if (id is not null && type is not null)
                {
                    result[id] = type.Split('/')[0];
                }
            }
        }
        return result;
    }

    /// <summary>Reads public DASH metadata for the current owned session, independently of DASH or HLS playback.</summary>
    public static async Task<FragmentMetadata> ReadOwnedMetadataAsync(StartupExchange current, IReadOnlyList<OwnedSession> owners, Func<string, Task<string>> readManifest)
    {
        if (!current.Outcome.Succeeded || !Uri.TryCreate(current.Outcome.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidDataException("Synchronization has no current successful startup endpoint.");
        }
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        var owner = owners.SingleOrDefault(item => item.SessionId == current.SessionId && item.Origin == origin)
            ?? throw new InvalidDataException("Synchronization startup does not match current owned session evidence.");
        if (current.DashUrl is null || !Uri.TryCreate(endpoint, current.DashUrl, out var manifest))
        {
            throw new InvalidDataException("Current startup omitted its public DASH metadata URL.");
        }
        var prefix = owner.InventoryUrl.EndsWith("/media/sessions", StringComparison.Ordinal) ? "/media/" : "/api/stream/cmaf/";
        if (manifest.GetLeftPart(UriPartial.Authority) != owner.Origin || manifest.UserInfo.Length > 0 || manifest.Fragment.Length > 0
            || manifest.AbsolutePath != $"{prefix}{owner.SessionId}/manifest.mpd")
        {
            throw new InvalidDataException("DASH metadata refers to another origin, session or media route.");
        }
        try
        {
            return new(owner.SessionId, manifest.AbsoluteUri, Representations(await readManifest(manifest.AbsoluteUri)));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("Current owned DASH metadata could not map representations.", ex);
        }
    }

    /// <summary>Requires one active representation per media type and two distinct fragments in comparable windows.</summary>
    public static SynchronizationEvidence Evaluate(IReadOnlyList<FragmentSample> samples, IReadOnlyDictionary<string, string> representations, int maximumDelta)
    {
        var groups = samples.GroupBy(sample => (sample.Session, sample.Representation))
            .Where(group => representations.ContainsKey(group.Key.Representation)).ToArray();
        var video = groups.Where(group => representations[group.Key.Representation] == "video").ToArray();
        var audio = groups.Where(group => representations[group.Key.Representation] == "audio").ToArray();
        if (audio.Length == 0 || video.Length == 0)
        {
            return new("insufficient-samples", null, null, null, null, null, "No measurable active audio/video pair (video-only is not measurable).");
        }
        if (video.Length != 1 || audio.Length != 1 || video[0].Key.Session != audio[0].Key.Session)
        {
            return new("insufficient-samples", null, null, null, null, null, "Session or rendition changed; active representations are ambiguous.");
        }
        var v = video[0].ToArray();
        var a = audio[0].ToArray();
        var start = v.Min(sample => sample.ReceivedUtc) > a.Min(sample => sample.ReceivedUtc) ? v.Min(sample => sample.ReceivedUtc) : a.Min(sample => sample.ReceivedUtc);
        var end = v.Max(sample => sample.ReceivedUtc) < a.Max(sample => sample.ReceivedUtc) ? v.Max(sample => sample.ReceivedUtc) : a.Max(sample => sample.ReceivedUtc);
        var comparable = end >= start;
        var comparableVideo = v.Where(sample => sample.ReceivedUtc >= start && sample.ReceivedUtc <= end).ToArray();
        var comparableAudio = a.Where(sample => sample.ReceivedUtc >= start && sample.ReceivedUtc <= end).ToArray();
        var vh = comparableVideo.Select(sample => sample.Sequence).DefaultIfEmpty().Max();
        var ah = comparableAudio.Select(sample => sample.Sequence).DefaultIfEmpty().Max();
        var delta = Math.Abs(vh - ah);
        if (!comparable || comparableVideo.Select(sample => sample.Sequence).Distinct().Count() < 2 || comparableAudio.Select(sample => sample.Sequence).Distinct().Count() < 2)
        {
            return new("insufficient-samples", video[0].Key.Representation, audio[0].Key.Representation, vh, ah, null, "Two distinct successful fragments per active stream in overlapping windows are required.");
        }
        return new(delta <= maximumDelta ? "aligned" : "misaligned", video[0].Key.Representation, audio[0].Key.Representation, vh, ah, delta, "Fresh transport sequence heads; not audible lip-sync.");
    }

    [GeneratedRegex(@"/([^/]+)/chunk-(.+)-([0-9]+)\.m4s$", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentPattern();
}

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Lineup.Integration.Streaming.Sweep;

/// <summary>One DASH segment on the presentation timeline, including the offset applied to media timestamps.</summary>
internal sealed record ManifestTiming(double Start, double End, double Offset, double Precision);

/// <summary>Certifies only owned media timestamp integrity from retained browser resources, never perceptual lip-sync.</summary>
internal static class FragmentTimeline
{
    /// <summary>Reads actual successful response bodies and the explicitly fetched owned DASH manifest, including HLS observations.</summary>
    public static TimestampEvidence ReadTrace(string tracePath, FragmentMetadata metadata, IReadOnlySet<string> activeRepresentations, IReadOnlySet<(string Representation, long Sequence)>? observedFragments = null)
    {
        try
        {
            using var archive = ZipFile.OpenRead(tracePath);
            var network = archive.GetEntry("trace.network") ?? throw new InvalidDataException("Trace omitted its network journal.");
            using var reader = new StreamReader(network.Open());
            var prefix = new Uri(metadata.ManifestUrl, UriKind.Absolute);
            var directory = prefix.AbsolutePath[..(prefix.AbsolutePath.LastIndexOf('/') + 1)];
            var manifests = new List<string> { metadata.Manifest };
            var initialization = new Dictionary<string, FragmentInitialization>(StringComparer.Ordinal);
            var fragments = new Dictionary<(string Representation, long Sequence), FragmentTimingSample>();
            var media = new List<(string Representation, long Sequence, string Resource)>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                using var record = JsonDocument.Parse(line);
                var snapshot = record.RootElement.GetProperty("snapshot");
                var response = snapshot.GetProperty("response");
                if (response.GetProperty("status").GetInt32() != 200
                    || !Uri.TryCreate(snapshot.GetProperty("request").GetProperty("url").GetString(), UriKind.Absolute, out var url)
                    || url.GetLeftPart(UriPartial.Authority) != prefix.GetLeftPart(UriPartial.Authority)
                    || !url.AbsolutePath.StartsWith(directory, StringComparison.Ordinal))
                {
                    continue;
                }
                var name = url.AbsolutePath[directory.Length..];
                if (name.Contains('/', StringComparison.Ordinal) || url.UserInfo.Length > 0)
                {
                    continue;
                }
                var isManifest = name == "manifest.mpd";
                var sample = FragmentAlignment.Parse(url.AbsoluteUri, DateTimeOffset.UnixEpoch);
                var representation = name.StartsWith("init-", StringComparison.Ordinal) && name.EndsWith(".mp4", StringComparison.Ordinal) ? name[5..^4] : null;
                if (!isManifest && !(representation is not null && activeRepresentations.Contains(representation))
                    && !(sample is not null && activeRepresentations.Contains(sample.Representation)))
                {
                    continue;
                }
                if (sample is not null && observedFragments is not null && !observedFragments.Contains((sample.Representation, sample.Sequence)))
                {
                    continue;
                }
                if (!response.GetProperty("content").TryGetProperty("_sha1", out var resource) || resource.GetString() is not { } resourceName)
                {
                    if (isManifest)
                    {
                        // The separately fetched, ownership-qualified manifest is mandatory and still must match all measured media.
                        continue;
                    }
                    throw new InvalidDataException($"Trace omitted the completed response body for {name}.");
                }
                if (isManifest)
                {
                    manifests.Add(Encoding.UTF8.GetString(Resource(archive, resourceName)));
                }
                else if (representation is not null)
                {
                    var parsed = FragmentTiming.ReadInitialization(Resource(archive, resourceName));
                    if (initialization.TryGetValue(representation, out var previous) && previous != parsed)
                    {
                        throw new InvalidDataException("Initialization changed during the observation.");
                    }
                    initialization[representation] = parsed;
                }
                else if (sample is not null)
                {
                    media.Add((sample.Representation, sample.Sequence, resourceName));
                }
            }
            foreach (var item in media)
            {
                if (!initialization.TryGetValue(item.Representation, out var init))
                {
                    throw new InvalidDataException($"Missing initialization for active representation {item.Representation}.");
                }
                var timing = FragmentTiming.ReadFragment(Resource(archive, item.Resource), init);
                var key = (item.Representation, item.Sequence);
                if (fragments.TryGetValue(key, out var previous) && previous != timing)
                {
                    throw new InvalidDataException("A repeated fragment changed its timing.");
                }
                fragments[key] = timing;
            }
            return Evaluate(metadata.Session, activeRepresentations, initialization, fragments, ParseManifests(manifests));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or XmlException or JsonException or InvalidOperationException
            or FormatException or OverflowException or KeyNotFoundException)
        {
            return new(metadata.Session, "insufficient-samples", [], [$"Timestamp evidence could not be established: {error.Message}"]);
        }
    }

    /// <summary>Checks adjacent decode continuity and manifest agreement within one clock tick, without comparing sequence origins across tracks.</summary>
    public static TimestampEvidence Evaluate(
        string session,
        IReadOnlySet<string> activeRepresentations,
        IReadOnlyDictionary<string, FragmentInitialization> initialization,
        IReadOnlyDictionary<(string Representation, long Sequence), FragmentTimingSample> fragments,
        IReadOnlyDictionary<(string Representation, long Sequence), ManifestTiming> manifest)
    {
        var tracks = new List<TrackTimestampEvidence>();
        var errors = new List<string>();
        foreach (var representation in activeRepresentations.Order(StringComparer.Ordinal))
        {
            if (!initialization.TryGetValue(representation, out var init))
            {
                errors.Add($"Missing initialization for {representation}.");
                continue;
            }
            var samples = fragments.Where(pair => pair.Key.Representation == representation).OrderBy(pair => pair.Key.Sequence).ToArray();
            var findings = new List<string>();
            var matched = 0;
            var consecutive = 0;
            double maximumGap = 0;
            double maximumDifference = 0;
            double? first = null;
            double? last = null;
            for (var index = 0; index < samples.Length; index++)
            {
                var (key, timing) = samples[index];
                var gap = timing.MaximumDecodeGap / (double)init.Timescale;
                if (index > 0 && key.Sequence - samples[index - 1].Key.Sequence == 1)
                {
                    consecutive++;
                    gap = Math.Max(gap, Math.Abs(timing.DecodeStart - samples[index - 1].Value.DecodeEnd) / (double)init.Timescale);
                }
                maximumGap = Math.Max(maximumGap, gap);
                if (gap > 1.0 / init.Timescale + 1e-9)
                {
                    findings.Add($"Fragment {key.Sequence} has a decode gap or overlap of {gap * 1000:F3} ms.");
                }
                if (manifest.TryGetValue(key, out var expected))
                {
                    matched++;
                    var start = timing.PresentationStart + expected.Offset;
                    var end = timing.PresentationEnd + expected.Offset;
                    first = first is null ? start : Math.Min(first.Value, start);
                    last = last is null ? end : Math.Max(last.Value, end);
                    var difference = Math.Max(Math.Abs(start - expected.Start), Math.Abs(end - expected.End));
                    maximumDifference = Math.Max(maximumDifference, difference);
                    var precision = Math.Max(expected.Precision, Math.Max(1.0 / init.Timescale, init.EditPrecisionSeconds));
                    if (difference > precision + 1e-9)
                    {
                        findings.Add($"Fragment {key.Sequence} differs from its manifest by {difference * 1000:F3} ms.");
                    }
                }
            }
            if (samples.Length < 2 || consecutive == 0 || matched < 2)
            {
                errors.Add($"Representation {representation} needs two distinct fragments, an adjacent pair and two manifest matches.");
            }
            if (first is not null && last is not null)
            {
                tracks.Add(new(representation, samples.Length, consecutive, matched, first.Value, last.Value, maximumGap * 1000, maximumDifference * 1000, findings)
                {
                    MaximumFragmentDurationSeconds = samples.Max(pair => pair.Value.PresentationEnd - pair.Value.PresentationStart)
                });
            }
        }
        if (tracks.Count == 2 && tracks.Max(track => track.PresentationStartSeconds) >= tracks.Min(track => track.PresentationEndSeconds))
        {
            errors.Add("Active tracks have no overlapping presentation coverage; cross-track timing is inconclusive.");
        }
        var state = errors.Count > 0 || tracks.Count != 2 ? "insufficient-samples"
            : tracks.Any(track => track.Findings.Count > 0) ? "timeline-discontinuity" : "timeline-continuous";
        return new(session, state, tracks, errors);
    }

    /// <summary>Expands bounded DASH timelines using timescale, Period start, presentation-time offset and track-local start numbers.</summary>
    public static IReadOnlyDictionary<(string Representation, long Sequence), ManifestTiming> ParseManifests(IEnumerable<string> manifests)
    {
        var result = new Dictionary<(string Representation, long Sequence), ManifestTiming>();
        foreach (var content in manifests)
        {
            var document = XDocument.Parse(content);
            double? previousEnd = 0;
            foreach (var period in document.Descendants().Where(element => element.Name.LocalName == "Period"))
            {
                var start = period.Attribute("start") is { } startAttribute ? XmlConvert.ToTimeSpan(startAttribute.Value).TotalSeconds
                    : previousEnd ?? throw new InvalidDataException("Implicit Period start has no preceding duration.");
                double? duration = period.Attribute("duration") is { } durationAttribute ? XmlConvert.ToTimeSpan(durationAttribute.Value).TotalSeconds : null;
                previousEnd = duration is null ? null : start + duration.Value;
                foreach (var adaptation in period.Elements().Where(element => element.Name.LocalName == "AdaptationSet"))
                {
                    foreach (var representation in adaptation.Elements().Where(element => element.Name.LocalName == "Representation"))
                    {
                        var id = representation.Attribute("id")?.Value ?? throw new InvalidDataException("Manifest representation omitted its ID.");
                        var parent = adaptation.Elements().SingleOrDefault(element => element.Name.LocalName == "SegmentTemplate");
                        var template = representation.Elements().SingleOrDefault(element => element.Name.LocalName == "SegmentTemplate") ?? parent;
                        if (template is null)
                        {
                            continue;
                        }
                        string? Attribute(string name) => template.Attribute(name)?.Value ?? parent?.Attribute(name)?.Value;
                        var scale = Number(Attribute("timescale"), 1);
                        var number = Number(Attribute("startNumber"), 1);
                        var pto = Number(Attribute("presentationTimeOffset"), 0);
                        if (scale <= 0 || number < 0 || pto < 0)
                        {
                            throw new InvalidDataException("Manifest timescale or segment origin is invalid.");
                        }
                        var timeline = template.Elements().SingleOrDefault(element => element.Name.LocalName == "SegmentTimeline")
                            ?? parent?.Elements().SingleOrDefault(element => element.Name.LocalName == "SegmentTimeline");
                        if (timeline is null)
                        {
                            throw new InvalidDataException("Manifest omitted its explicit segment timeline.");
                        }
                        var entries = timeline.Elements().Where(element => element.Name.LocalName == "S").ToArray();
                        long time = 0;
                        var expanded = 0;
                        for (var index = 0; index < entries.Length; index++)
                        {
                            var entry = entries[index];
                            time = Number(entry.Attribute("t")?.Value, time);
                            var length = Number(entry.Attribute("d")?.Value, 0);
                            var repeat = Number(entry.Attribute("r")?.Value, 0);
                            if (time < 0 || length <= 0 || repeat < -1)
                            {
                                throw new InvalidDataException("Manifest segment timing is invalid.");
                            }
                            if (repeat == -1)
                            {
                                var next = index + 1 < entries.Length ? entries[index + 1].Attribute("t")?.Value : null;
                                var until = next is not null ? Number(next, 0) : duration is not null ? duration.Value * scale + pto
                                    : throw new InvalidDataException("Unbounded DASH timeline repeat cannot establish timestamp evidence.");
                                repeat = checked((long)Math.Ceiling((until - time) / length) - 1);
                            }
                            if (repeat < 0 || repeat >= 100_000 || expanded + repeat + 1 > 100_000)
                            {
                                throw new InvalidDataException("DASH timeline expansion exceeds its bounded inspection limit.");
                            }
                            for (long repetition = 0; repetition <= repeat; repetition++)
                            {
                                var end = checked(time + length);
                                var value = new ManifestTiming((time - pto) / (double)scale + start, (end - pto) / (double)scale + start, start - pto / (double)scale, 1.0 / scale);
                                var key = (id, number);
                                if (result.TryGetValue(key, out var previous) && previous != value)
                                {
                                    throw new InvalidDataException("Manifest snapshots disagree about a numbered segment or reuse it across Periods.");
                                }
                                result[key] = value;
                                time = end;
                                number = checked(number + 1);
                                expanded++;
                            }
                        }
                    }
                }
            }
        }
        return result;
    }

    private static long Number(string? value, long fallback) => value is null ? fallback : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static byte[] Resource(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry($"resources/{name}") ?? throw new InvalidDataException("Trace response resource is missing.");
        if (entry.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Trace response exceeds the bounded media inspection limit.");
        }
        using var stream = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        stream.CopyTo(output);
        return output.ToArray();
    }
}

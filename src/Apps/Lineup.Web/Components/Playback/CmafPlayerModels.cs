using System.Text.Json;

namespace Lineup.Web.Components.Playback;

/// <summary>Describes one active audio rendition reported by the browser player.</summary>
/// <param name="Label">The manifest rendition label.</param>
/// <param name="Codec">The browser codec identifier.</param>
/// <param name="Channels">The reported channel count.</param>
/// <param name="Id">The optional Shaka audio-stream identifier.</param>
/// <param name="Language">The optional manifest language.</param>
public sealed record CmafPlayerAudio(string? Label, string? Codec, int? Channels, string? Id = null, string? Language = null)
{
    /// <summary>Reads active-audio metadata from an asynchronous player event.</summary>
    /// <param name="details">The structured player event details.</param>
    /// <returns>The active audio metadata, or <see langword="null"/> when unavailable.</returns>
    public static CmafPlayerAudio? FromDetails(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var label = value.TryGetProperty("label", out var labelProperty) ? labelProperty.GetString() : null;
        var id = value.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var language = value.TryGetProperty("language", out var languageProperty) ? languageProperty.GetString() : null;
        var codec = value.TryGetProperty("codec", out var codecProperty) ? codecProperty.GetString() : null;
        int? channels = value.TryGetProperty("channels", out var channelsProperty) && channelsProperty.TryGetInt32(out var channelCount)
            ? channelCount
            : null;
        return new CmafPlayerAudio(label, codec, channels, id, language);
    }
}

/// <summary>Describes one active video rendition reported by the browser player.</summary>
/// <param name="Codec">The browser codec identifier.</param>
/// <param name="Width">The reported coded width.</param>
/// <param name="Height">The reported coded height.</param>
public sealed record CmafPlayerVideo(string? Codec, int? Width, int? Height)
{
    /// <summary>Reads active-video metadata from an asynchronous player event.</summary>
    /// <param name="details">The structured player event details.</param>
    /// <returns>The active video metadata, or <see langword="null"/> when unavailable.</returns>
    public static CmafPlayerVideo? FromDetails(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var codec = value.TryGetProperty("codec", out var codecProperty) ? codecProperty.GetString() : null;
        int? width = value.TryGetProperty("width", out var widthProperty) && widthProperty.TryGetInt32(out var widthValue) ? widthValue : null;
        int? height = value.TryGetProperty("height", out var heightProperty) && heightProperty.TryGetInt32(out var heightValue) ? heightValue : null;
        return new CmafPlayerVideo(codec, width, height);
    }
}

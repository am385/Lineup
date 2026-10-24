using System.Text.Json;
using Microsoft.Extensions.Localization;

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

/// <summary>Provides localized Lineup-authored text used by the CMAF JavaScript player.</summary>
/// <param name="Subtitles">The subtitles control label.</param>
/// <param name="Off">The disabled-subtitles label.</param>
/// <param name="VideoElementNotFound">The missing video element error.</param>
/// <param name="ShakaUnsupported">The unsupported Shaka Player error.</param>
/// <param name="PlayerContainerNotFound">The missing player container error.</param>
/// <param name="InitializationSuperseded">The superseded initialization error.</param>
/// <param name="SessionUnavailable">The unavailable stream session error.</param>
/// <param name="AudioFallback">The source-audio fallback notification.</param>
/// <param name="VideoFallbackRequired">The source-video restart notification.</param>
/// <param name="VideoFallback">The source-video fallback notification.</param>
/// <param name="PlaybackFailed">The live playback failure fallback.</param>
/// <param name="AudioTrackChanged">The audio-track change notification.</param>
/// <param name="PresentationLoadFailed">The live presentation load failure fallback.</param>
/// <param name="ProtocolFallback">The protocol fallback notification.</param>
/// <param name="BothManifestsLoadFailed">The dual-manifest load failure fallback.</param>
/// <param name="CompatibilityPlaybackFailed">The compatibility playback failure fallback.</param>
/// <param name="CompatibilityTrackLabel">The compatibility subtitle track label.</param>
/// <param name="CompatibilityPresentationLoadFailed">The compatibility presentation load failure fallback.</param>
/// <param name="SelectedSubtitles">The selected subtitles fallback label.</param>
public sealed record CmafPlayerText(
    string Subtitles,
    string Off,
    string VideoElementNotFound,
    string ShakaUnsupported,
    string PlayerContainerNotFound,
    string InitializationSuperseded,
    string SessionUnavailable,
    string AudioFallback,
    string VideoFallbackRequired,
    string VideoFallback,
    string PlaybackFailed,
    string AudioTrackChanged,
    string PresentationLoadFailed,
    string ProtocolFallback,
    string BothManifestsLoadFailed,
    string CompatibilityPlaybackFailed,
    string CompatibilityTrackLabel,
    string CompatibilityPresentationLoadFailed,
    string SelectedSubtitles)
{
    /// <summary>Creates localized text for live playback.</summary>
    public static CmafPlayerText Live(IStringLocalizer<UiStrings> text) =>
        new(
            text["Watch_Subtitles"],
            text["Watch_Off"],
            text["Watch_JsVideoElementNotFound"],
            text["Watch_JsShakaUnsupported"],
            text["Watch_JsPlayerContainerNotFound"],
            text["Watch_JsInitializationSuperseded"],
            text["Watch_JsSessionUnavailable"],
            text["Watch_JsAudioFallback"],
            text["Watch_JsVideoFallbackRequired"],
            text["Watch_JsVideoFallback"],
            text["Watch_JsPlaybackFailed"],
            text["Watch_JsAudioTrackChanged"],
            text["Watch_JsPresentationLoadFailed"],
            text["Watch_JsProtocolFallback"],
            text["Watch_JsBothManifestsLoadFailed"],
            string.Empty,
            string.Empty,
            string.Empty,
            text["Watch_SelectedSubtitles"]);

    /// <summary>Creates localized text for compatibility-test playback.</summary>
    public static CmafPlayerText Test(IStringLocalizer<UiStrings> text) =>
        new(
            string.Empty,
            string.Empty,
            text["WatchTest_JsVideoElementNotFound"],
            text["WatchTest_JsShakaUnsupported"],
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            text["WatchTest_JsCompatibilityPlaybackFailed"],
            text["WatchTest_JsCompatibilityTrackLabel"],
            text["WatchTest_JsPresentationLoadFailed"],
            string.Empty);
}

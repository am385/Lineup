namespace Lineup.WebPlayer.FFmpeg;

/// <summary>
/// Names transient workspaces owned by the Web Player runtime.
/// </summary>
public static class WebPlayerTransientWorkspaces
{
    /// <summary>Gets the workspace containing CMAF output sessions.</summary>
    public const string Cmaf = "web-player-cmaf";

    /// <summary>Gets the workspace containing extracted subtitle sidecars.</summary>
    public const string Subtitles = "web-player-subtitles";
}

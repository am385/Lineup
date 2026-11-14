namespace Lineup.Plugin.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Configures one XMLTV guide output.
/// </summary>
public sealed record XmltvGuideOutputConfiguration
{
    /// <summary>Gets the destination XMLTV file path.</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>Gets whether channels without guide data receive synthetic unavailable programs.</summary>
    public bool IncludeUnavailablePrograms { get; init; }
}

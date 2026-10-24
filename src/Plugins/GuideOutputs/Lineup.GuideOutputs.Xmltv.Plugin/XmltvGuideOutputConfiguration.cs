namespace Lineup.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Configures one XMLTV guide output.
/// </summary>
public sealed record XmltvGuideOutputConfiguration
{
    /// <summary>Gets the destination XMLTV file path.</summary>
    public string OutputPath { get; init; } = string.Empty;
}

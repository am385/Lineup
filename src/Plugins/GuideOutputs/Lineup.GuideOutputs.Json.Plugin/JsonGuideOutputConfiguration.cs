namespace Lineup.GuideOutputs.Json.Plugin;

/// <summary>Persisted JSON guide-output configuration.</summary>
internal sealed record JsonGuideOutputConfiguration
{
    /// <summary>Gets the destination JSON file path.</summary>
    public string OutputPath { get; init; } = string.Empty;
}

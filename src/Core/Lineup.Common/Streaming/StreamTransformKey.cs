namespace Lineup.Common.Streaming;

/// <summary>
/// Identifies a reusable transform applied to one shared acquisition.
/// </summary>
/// <param name="Stage">Stable transform stage name.</param>
/// <param name="SettingsKey">Canonical settings that affect transform output.</param>
public sealed record StreamTransformKey(string Stage, string SettingsKey);

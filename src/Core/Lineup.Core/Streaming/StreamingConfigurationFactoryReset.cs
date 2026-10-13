using System.Text.Json;

namespace Lineup.Core.Streaming;

/// <summary>
/// Replaces persisted streaming configuration with the current default configuration.
/// </summary>
public static class StreamingConfigurationFactoryReset
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Replaces the identified streaming configuration file with a current empty configuration.
    /// </summary>
    /// <param name="path">Streaming configuration path explicitly confirmed by the operator.</param>
    public static void Reset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StreamingConfiguration(), JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

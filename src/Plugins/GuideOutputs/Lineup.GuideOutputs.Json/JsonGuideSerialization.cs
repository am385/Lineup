using System.Text.Json;

namespace Lineup.GuideOutputs.Json;

/// <summary>Serializes and deserializes the public JSON guide document contract.</summary>
public static class JsonGuideSerialization
{
    /// <summary>Serializes one guide document using Lineup's stable JSON formatting.</summary>
    public static Task SerializeAsync(JsonGuideDocument document, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, document, CreateOptions(), cancellationToken);
    }

    /// <summary>Deserializes one guide document using Lineup's stable JSON formatting.</summary>
    public static ValueTask<JsonGuideDocument?> DeserializeAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return JsonSerializer.DeserializeAsync<JsonGuideDocument>(source, CreateOptions(), cancellationToken);
    }

    /// <summary>Creates serializer options compatible with published JSON guide documents.</summary>
    public static JsonSerializerOptions CreateOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
}

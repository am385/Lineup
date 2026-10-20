namespace Lineup.Streaming.Sweep;

internal static class JsonSequence
{
    public static async Task<IReadOnlyList<JsonNode>> ReadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return Parse(await File.ReadAllBytesAsync(path));
    }

    private static IReadOnlyList<JsonNode> Parse(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
        var nodes = new List<JsonNode>();
        while (reader.Read())
        {
            using var document = JsonDocument.ParseValue(ref reader);
            nodes.Add(JsonNode.Parse(document.RootElement.GetRawText())!);
        }
        return nodes;
    }
}

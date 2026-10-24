namespace Lineup.Streaming.Sweep;

internal static class JsonSequence
{
    public static async Task<IReadOnlyList<JsonNode>> ReadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var bytes = await File.ReadAllBytesAsync(path);
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var completeLength = lastNewline + 1;
        var nodes = Parse(bytes[..completeLength]);
        if (completeLength < bytes.Length)
        {
            var tail = ReadTail(bytes[completeLength..]);
            if (tail is not null)
            {
                nodes.Add(tail);
                await File.AppendAllTextAsync(path, "\n");
            }
            else
            {
                // Only a non-newline-terminated tail may be torn. Interior corruption already threw above.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(completeLength);
                stream.Flush(true);
            }
        }
        return nodes;
    }

    private static JsonNode? ReadTail(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        if (!reader.Read() || !JsonDocument.TryParseValue(ref reader, out var document))
        {
            return null;
        }
        using (document)
        {
            var node = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject
                ?? throw new JsonException("Journal records must be objects.");
            if (reader.Read())
            {
                throw new JsonException("Trailing journal data contains more than one record.");
            }
            return node;
        }
    }

    private static List<JsonNode> Parse(byte[] bytes)
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

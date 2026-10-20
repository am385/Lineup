namespace Lineup.Streaming.Sweep;

internal sealed class RunJournal
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly JsonSerializerOptions _compactJson;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunJournal(string root, JsonSerializerOptions json)
    {
        _path = Path.Combine(root, "events.jsonl");
        _json = json;
        _compactJson = new JsonSerializerOptions(json) { WriteIndented = false };
    }

    public Task WriteEventAsync(string type, object payload) => AppendAsync(new { utc = DateTimeOffset.UtcNow, type, payload });

    public Task WriteTerminalAsync(string matrix, string channel, ChannelAttemptResult result) =>
        AppendAsync(new { utc = DateTimeOffset.UtcNow, type = "channel-terminal", payload = new { matrix, channel, result } });

    public async Task<HashSet<string>> ReadTerminalKeysAsync()
    {
        if (!File.Exists(_path))
        {
            return [];
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in await JsonSequence.ReadAsync(_path))
        {
            if (node?["type"]?.GetValue<string>() == "channel-terminal")
            {
                result.Add($"{node["payload"]!["matrix"]!.GetValue<string>()}|{node["payload"]!["channel"]!.GetValue<string>()}");
            }
        }
        return result;
    }

    private async Task AppendAsync(object value)
    {
        await _gate.WaitAsync();
        try
        {
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(value, _compactJson));
            await writer.FlushAsync();
            stream.Flush(true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

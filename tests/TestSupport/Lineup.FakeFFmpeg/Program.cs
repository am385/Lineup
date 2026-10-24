using System.Text.Json;

if (args.Length == 0)
{
    using var bootstrap = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? throw new InvalidDataException("The worker probe requires a bootstrap descriptor."));
    var temporaryPath = Path.GetTempPath();
    var artifactDirectory = Path.Combine(temporaryPath, $"lineup-worker-probe-{Guid.NewGuid():N}", "lineup-web-player");
    string? writeError = null;
    try
    {
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "fragment.m4s"), "probe");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        writeError = exception.GetType().Name;
    }
    var report = new
    {
        TemporaryPath = temporaryPath,
        ArtifactDirectory = artifactDirectory,
        WriteError = writeError,
        Variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => (string?)item.Value),
        Arguments = Environment.GetCommandLineArgs()
    };
    var reportPath = Path.Combine(Environment.CurrentDirectory, "worker-environment.json");
    await File.WriteAllTextAsync(reportPath + ".tmp", JsonSerializer.Serialize(report));
    File.Move(reportPath + ".tmp", reportPath);
    if (bootstrap.RootElement.GetProperty("virtualDevice").GetProperty("name").GetString() != "exit")
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    return writeError is null ? 0 : 1;
}

var fail = string.Equals(Environment.GetEnvironmentVariable("LINEUP_FAKE_FFMPEG_FAIL"), "1", StringComparison.Ordinal);
if (fail)
{
    await Console.Error.WriteLineAsync("Synthetic media processor failure.");
    return 1;
}

var output = args.LastOrDefault();
if (output?.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) == true)
{
    var directory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory;
    await File.WriteAllTextAsync(
        output,
        """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
          <Period>
            <AdaptationSet contentType="video"><Representation id="0" codecs="avc1.64001f" /></AdaptationSet>
            <AdaptationSet contentType="audio"><Representation id="1" codecs="mp4a.40.2" /></AdaptationSet>
          </Period>
        </MPD>
        """);
    await File.WriteAllTextAsync(Path.Combine(directory, "master.m3u8"), "#EXTM3U\n#EXT-X-VERSION:7\n");
    await File.WriteAllBytesAsync(Path.Combine(directory, "chunk-0-00001.m4s"), [0]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "chunk-0-00002.m4s"), [0]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "chunk-1-00001.m4s"), [0]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "chunk-1-00002.m4s"), [0]);
}
else if (output?.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) == true)
{
    await File.WriteAllTextAsync(output, "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nIntegration caption\n");
}

await Console.OpenStandardInput().CopyToAsync(Stream.Null);
return 0;

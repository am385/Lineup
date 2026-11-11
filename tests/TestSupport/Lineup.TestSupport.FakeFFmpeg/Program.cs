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

if (args is ["diagnostics", var length, var code, var hold])
{
    var remaining = int.Parse(length, System.Globalization.CultureInfo.InvariantCulture);
    await Console.Error.WriteLineAsync("Earlier diagnostic");
    var chunk = new string('x', 1024);
    while (remaining > 0)
    {
        var count = Math.Min(remaining, chunk.Length);
        await Console.Error.WriteAsync(chunk.AsMemory(0, count));
        remaining -= count;
    }
    await Console.Error.WriteLineAsync("final-error");
    await Console.Error.FlushAsync();
    await Console.OpenStandardOutput().WriteAsync(new byte[] { 0x47 });
    await Console.OpenStandardOutput().FlushAsync();
    if (hold == "hold")
    {
        await Console.OpenStandardInput().CopyToAsync(Stream.Null);
    }
    return int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
}

if (args is ["probe-json", var metadata])
{
    await Console.Out.WriteAsync(metadata);
    return 0;
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
    var mode = Environment.GetEnvironmentVariable("LINEUP_FAKE_FFMPEG_STARTUP_MODE");
    var codecIndex = Array.IndexOf(args, "-c:a:0");
    var codec = codecIndex < 0 ? string.Empty : args[codecIndex + 1];
    var startLog = Environment.GetEnvironmentVariable("LINEUP_FAKE_FFMPEG_START_LOG");
    if (!string.IsNullOrEmpty(startLog))
    {
        await File.AppendAllTextAsync(startLog, $"{Environment.ProcessId}|{Path.GetDirectoryName(output)}|{codec}{Environment.NewLine}");
    }
    if (mode == "hold-readiness" && !string.IsNullOrEmpty(startLog))
    {
        while (!File.Exists(startLog + ".ready"))
        {
            await Task.Delay(10);
        }
    }
    if (mode is "unrelated" or "both-fail" || (codec == "copy" && mode is "copy-fails" or "fallback-holds"))
    {
        await File.WriteAllTextAsync(output, "stale failed-attempt manifest");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!, "chunk-0-stale.m4s"), "partial media");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!, "captions-999.vtt"), "stale captions");
        await Console.Error.WriteLineAsync(mode == "unrelated" ? "Synthetic unrelated failure." : "codec ac3 is not currently supported in container");
        return 1;
    }
    if (mode == "fallback-holds")
    {
        await Console.OpenStandardInput().CopyToAsync(Stream.Null);
        return 0;
    }
    if (mode == "copy-fails" && (File.Exists(output) ||
        File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "chunk-0-stale.m4s")) ||
        File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "captions-999.vtt"))))
    {
        await Console.Error.WriteLineAsync("Stale output was not removed before fallback.");
        return 1;
    }
}
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
    await File.WriteAllBytesAsync(Path.Combine(directory, "init-0.mp4"), [0]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "init-1.mp4"), [0]);
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

namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task<IReadOnlyList<ChannelDefinition>> ReadEnabledChannelsAsync(string baseUrl)
    {
        var status = await GetStatusAsync(baseUrl);
        return ReadProjectedChannels(status)
            .OrderBy(channel => ParseChannelNumber(channel.Number).Major)
            .ThenBy(channel => ParseChannelNumber(channel.Number).Minor)
            .ThenBy(channel => channel.Number, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Uses the same single enabled Web Player projection as Watch, not physical inventory.</summary>
    internal static IReadOnlyList<ChannelDefinition> ReadProjectedChannels(JsonNode status)
    {
        var players = status["configuration"]?["streaming"]?["virtualDevices"]?.AsArray()
            .Where(item => item?["enabled"]?.GetValue<bool>() == true
                && item["adapter"]?["kind"]?.GetValue<string>() == "web-player").ToArray() ?? [];
        if (players.Length != 1)
        {
            throw new InvalidDataException("Preflight requires exactly one enabled Web Player.");
        }
        var id = players[0]!["virtualDeviceId"]!.GetValue<string>();
        var projection = status["channels"]?["virtualProjections"]?.AsArray().SingleOrDefault(item => item?["virtualDeviceId"]?.GetValue<string>() == id)
            ?? throw new InvalidDataException("Web Player projection is unavailable.");
        if (projection["conflicts"]?.AsArray().Count > 0)
        {
            throw new InvalidDataException("Web Player projection contains conflicting channel numbers.");
        }
        return (projection["channels"]?.AsArray() ?? throw new InvalidDataException("Projected channels are unavailable."))
            .Select(item =>
            {
                var sources = item!["sources"]!.AsArray().Select(source => new SourceCandidate(
                    source!["physicalDeviceId"]!.GetValue<string>(), source["providerKey"]!.GetValue<string>())).ToArray();
                if (sources.Length == 0)
                {
                    throw new InvalidDataException("Projected channel has no physical source candidates.");
                }
                return new ChannelDefinition(item["number"]!.GetValue<string>(), item["name"]!.GetValue<string>(), sources[0].ProviderKey, sources[0].PhysicalDeviceId)
                {
                    VirtualDeviceId = id,
                    Sources = sources
                };
            }).ToArray();
    }

    private async Task<JsonNode> WaitForActiveStreamAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(baseUrl);
            var items = status["streams"]?["items"]?.AsArray();
            var currentSession = _attempt?.Starts.LastOrDefault()?.SessionId;
            var owned = _attempt?.Ownership.Where(owner => currentSession is null || owner.SessionId == currentSession)
                .Select(owner => owner.SessionId).ToHashSet(StringComparer.Ordinal);
            var matching = items?.FirstOrDefault(item => owned is null || owned.Contains(item?["sessionId"]?.GetValue<string>() ?? string.Empty)
                || owned.Contains(item?["workerMediaSessionId"]?.GetValue<string>() ?? string.Empty));
            if (matching is not null)
            {
                return matching.DeepClone();
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("No active stream appeared in the status API.");
    }

    private async Task<JsonNode> WaitForReadyWorkerStreamAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(baseUrl);
            var item = status["streams"]?["items"]?.AsArray().FirstOrDefault(stream =>
                string.Equals(stream?["workerStartupStage"]?.GetValue<string>(), "ready", StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                return item.DeepClone();
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("No ready dedicated worker presentation appeared in the status API.");
    }

    private static string ReadCodec(JsonArray tracks, string type)
    {
        return tracks.FirstOrDefault(track =>
                string.Equals(track?["type"]?.GetValue<string>(), type, StringComparison.OrdinalIgnoreCase))?["codec"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The active stream did not report a {type} codec.");
    }

    private async Task WaitForCleanupAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(baseUrl);
            if (status["streams"]?["count"]?.GetValue<int>() == 0)
            {
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"An active stream remained after cleanup at {baseUrl}.");
    }

    private static bool HasAudio(JsonNode activeStream) =>
        activeStream["virtualOutput"]?["tracks"]?.AsArray().Any(track =>
            string.Equals(track?["type"]?.GetValue<string>(), "audio", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasSourceCaptions(JsonNode activeStream) =>
        activeStream["physicalInput"]?["tracks"]?.AsArray().Any(track =>
            string.Equals(track?["type"]?.GetValue<string>(), "subtitle", StringComparison.OrdinalIgnoreCase)) == true
        || activeStream["tracks"]?.AsArray().Any(track => track?["closedCaptions"]?.GetValue<bool>() == true) == true;

    private async Task WaitReadyAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await GetStatusAsync(baseUrl);
                if (status["application"]?["startupStatus"]?.GetValue<string>() == "ready")
                {
                    return;
                }
            }
            catch
            {
            }
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"{baseUrl} did not become ready.");
    }

    private async Task WaitWorkerHealthyAsync(string baseUrl, Guid virtualId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(baseUrl);
            var worker = status["virtualOutputWorkers"]?.AsArray().FirstOrDefault(item =>
                string.Equals(item?["virtualDeviceId"]?.GetValue<string>(), virtualId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (string.Equals(worker?["state"]?.GetValue<string>(), "Healthy", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"Virtual output {virtualId:D} did not become healthy.");
    }

    private async Task WaitForBrokerStreamCountAsync(MatrixDefinition matrix, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(matrix.SourceUrl);
            if (ReadBrokerStreamCount(status, matrix) == expected)
            {
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"{matrix.Id} did not report {expected} active broker streams.");
    }

    private static int ReadBrokerStreamCount(JsonNode status, MatrixDefinition matrix)
    {
        var virtualId = GuidFor($"{matrix.Id}:source-virtual").ToString("D");
        var worker = status["virtualOutputWorkers"]?.AsArray().FirstOrDefault(item =>
            string.Equals(item?["virtualDeviceId"]?.GetValue<string>(), virtualId, StringComparison.OrdinalIgnoreCase));
        return worker?["activeBrokerStreams"]?.GetValue<int>() ?? -1;
    }

    private async Task<JsonNode> GetStatusAsync(string baseUrl)
    {
        var text = await _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/v1/status");
        return JsonNode.Parse(text) ?? throw new JsonException("Status API returned null.");
    }

    private async Task EnsureTunerIdleAsync(TimeSpan timeout)
    {
        if (_options.ExistingInstance)
        {
            return;
        }
        var discovery = JsonNode.Parse(await _http.GetStringAsync($"http://{GetTunerAddress()}/discover.json"))
            ?? throw new InvalidDataException("Tuner discovery was null.");
        var capacity = discovery["TunerCount"]?.GetValue<int>() ?? throw new InvalidDataException("Tuner discovery omitted capacity.");
        if (capacity < 1)
        {
            throw new InvalidDataException("Tuner capacity must be positive.");
        }
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var html = await _http.GetStringAsync($"http://{GetTunerAddress()}/tuners.html");
                if (CountOccurrences(html, "not in use") == capacity)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Tuner idle probe failed: {ex.Message}");
            }
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"The {capacity} discovered physical tuners did not return to idle.");
    }

    private string GetTunerAddress()
    {
        return string.IsNullOrWhiteSpace(_tunerAddress)
            ? throw new InvalidOperationException("Set LINEUP_SWEEP_TUNER_ADDRESS before running a live channel sweep.")
            : _tunerAddress;
    }

    private async Task<BrowserLease> OpenBrowserAsync(string profilePath)
    {
        Directory.CreateDirectory(profilePath);
        var configuredExecutable = _options.BrowserExecutable ?? Environment.GetEnvironmentVariable("LINEUP_SWEEP_BROWSER_EXECUTABLE")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredExecutable) && !File.Exists(configuredExecutable))
        {
            throw new FileNotFoundException("The configured sweep browser executable does not exist.", configuredExecutable);
        }

        var executable = configuredExecutable;
        if (string.IsNullOrWhiteSpace(executable) && OperatingSystem.IsWindows())
        {
            executable = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            }.FirstOrDefault(File.Exists);
        }
        var playwright = await Playwright.CreateAsync();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(
            profilePath,
            new()
            {
                ExecutablePath = executable,
                Headless = true,
                IgnoreHTTPSErrors = false,
                Args =
                [
                    "--autoplay-policy=no-user-gesture-required",
                    "--disable-background-timer-throttling",
                    "--disable-renderer-backgrounding",
                    "--disable-backgrounding-occluded-windows"
                ],
                ViewportSize = new() { Width = 1600, Height = 1000 }
            });
        return new BrowserLease(playwright, context);
    }

    private async Task SaveComposeLogsAsync(MatrixDefinition matrix, string project, string outputRoot)
    {
        if (_options.ExistingInstance)
        {
            throw new InvalidOperationException("Existing-instance mode does not operate Docker Compose.");
        }
        var result = await RunProcessAsync(
            "docker",
            $"compose -p {project} -f \"{Path.Combine(MatrixRoot(matrix), "compose.yml")}\" logs --no-color --timestamps",
            MatrixRoot(matrix));
        Directory.CreateDirectory(outputRoot);
        await SweepStore.WriteAtomicAsync(Path.Combine(outputRoot, "compose.log"), result.Output + result.Error);
    }

    private async Task StopMatrixAsync(MatrixDefinition matrix, string project, string outputRoot)
    {
        if (_options.ExistingInstance)
        {
            throw new InvalidOperationException("Existing-instance mode forbids deployment teardown.");
        }
        try
        {
            await RunProcessAsync(
                "docker",
                $"compose -p {project} -f \"{Path.Combine(MatrixRoot(matrix), "compose.yml")}\" down -v --remove-orphans",
                MatrixRoot(matrix));
        }
        finally
        {
            try
            {
                await EnsureTunerIdleAsync(TimeSpan.FromSeconds(45));
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(outputRoot);
                await File.WriteAllTextAsync(Path.Combine(outputRoot, "cleanup-error.txt"), ex.ToString());
                throw;
            }
        }
    }

    private async Task BuildReportsAsync(string runRoot)
    {
        var journalPath = Path.Combine(runRoot, "events.jsonl");
        var terminals = (await JsonSequence.ReadAsync(journalPath))
            .Where(node => node?["type"]?.GetValue<string>() == "channel-terminal")
            .Select(node => node!["payload"]!["result"]!.Deserialize<ChannelAttemptResult>(_json)!)
            .ToArray();
        await File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(terminals, _json));
        var csv = new StringBuilder(
            "Matrix,Channel,Name,Attempts,AV,Caption,Category,StartupOutcome,HttpStatus,Code,Stage,"
            + "StartupDurationMilliseconds,FailurePhase,DurationSeconds,Failure\n");
        var orderedTerminals = terminals
            .OrderBy(item => Array.FindIndex(_matrices, matrix => matrix.Id == item.Matrix))
            .ThenBy(item => ParseChannelNumber(item.Channel).Major)
            .ThenBy(item => ParseChannelNumber(item.Channel).Minor);
        foreach (var result in orderedTerminals)
        {
            csv.AppendLine(string.Join(",",
                Csv(result.Matrix),
                Csv(result.Channel),
                Csv(result.Name),
                GetAttemptsExecuted(result),
                result.AvPassed,
                Csv(result.CaptionResult),
                Csv(GetResultCategory(result)),
                Csv(result.Startup?.Outcome ?? string.Empty),
                result.Startup?.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(result.Startup?.Code ?? string.Empty),
                Csv(result.Startup?.Stage ?? string.Empty),
                result.Startup?.DurationMilliseconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(result.FailurePhase ?? string.Empty),
                result.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                Csv(result.Failure ?? string.Empty)));
        }
        await File.WriteAllTextAsync(Path.Combine(runRoot, "results.csv"), csv.ToString());
        var summary = new StringBuilder("# Lineup Docker channel sweep\n\n");
        summary.AppendLine($"Completed: {DateTimeOffset.UtcNow:O}\n");
        summary.AppendLine("| Matrix | Channels | AV passed | Caption failures | Failed |");
        summary.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var matrix in _matrices)
        {
            var rows = terminals.Where(result => result.Matrix == matrix.Id).ToArray();
            summary.AppendLine($"| {matrix.Id} | {rows.Length} | {rows.Count(row => row.AvPassed)} | {rows.Count(row => row.CaptionResult == "source-caption-not-exposed")} | {rows.Count(row => !row.AvPassed)} |");
        }
        summary.AppendLine();
        var failures = terminals.Where(result => !result.AvPassed || result.CaptionResult == "source-caption-not-exposed").ToArray();
        summary.AppendLine($"Total terminal channel results: {terminals.Length}. Failures or caption mismatches: {failures.Length}.");
        summary.AppendLine("\n## Startup outcomes\n");
        summary.AppendLine("| Category | Outcome | HTTP | Code | Stage | Count |");
        summary.AppendLine("|---|---|---:|---|---|---:|");
        foreach (var outcomeGroup in terminals
            .GroupBy(result => new
            {
                Category = GetResultCategory(result),
                Outcome = result.Startup?.Outcome ?? "not-observed",
                HttpStatus = result.Startup?.HttpStatus,
                Code = result.Startup?.Code,
                Stage = result.Startup?.Stage
            })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Category, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Outcome, StringComparer.Ordinal))
        {
            var status = outcomeGroup.Key.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            summary.AppendLine(
                $"| {outcomeGroup.Key.Category} | {outcomeGroup.Key.Outcome} | {status} | "
                + $"{outcomeGroup.Key.Code ?? string.Empty} | {outcomeGroup.Key.Stage ?? string.Empty} | {outcomeGroup.Count()} |");
        }
        if (failures.Length > 0)
        {
            summary.AppendLine("\n## Failures\n");
            foreach (var failure in failures)
            {
                var startupOutcome = failure.Startup?.Outcome ?? "not-observed";
                var startupStatus = failure.Startup?.HttpStatus is int status ? $" HTTP {status}" : string.Empty;
                var startupCode = string.IsNullOrWhiteSpace(failure.Startup?.Code) ? string.Empty : $" ({failure.Startup.Code})";
                summary.AppendLine(
                    $"- `{failure.Matrix}` channel `{failure.Channel}` ({failure.Name}), attempts {GetAttemptsExecuted(failure)}, "
                    + $"category `{GetResultCategory(failure)}`, startup `{startupOutcome}`{startupStatus}{startupCode}: "
                    + $"{failure.Failure ?? failure.CaptionResult}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(runRoot, "summary.md"), summary.ToString());
    }

    private string GetOrCreateMainRunRoot()
    {
        var marker = Path.Combine(_runsRoot, "current.txt");
        if (File.Exists(marker))
        {
            var existing = File.ReadAllText(marker).Trim();
            if (Directory.Exists(existing) && !File.Exists(Path.Combine(existing, "completed.marker")))
            {
                return existing;
            }
        }
        var runRoot = CreateRunRoot($"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        File.WriteAllText(marker, runRoot);
        return runRoot;
    }

    private string CreateRunRoot(string name)
    {
        var path = Path.Combine(_runsRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string MatrixRoot(MatrixDefinition matrix) => Path.Combine(_root, "matrices", matrix.Id);
    private static string TerminalKey(string matrix, string channel) => $"{matrix}|{channel}";
    private static Guid GuidFor(string value) => new(MD5.HashData(Encoding.UTF8.GetBytes(value)));
    private static int CountOccurrences(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.OrdinalIgnoreCase).Length) / value.Length;
    private static (int Major, int Minor) ParseChannelNumber(string value)
    {
        var parts = value.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        _ = int.TryParse(parts.ElementAtOrDefault(0), out var major);
        _ = int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        return (major, minor);
    }
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string SourceInternalBaseUrl(MatrixDefinition matrix) =>
        matrix.Mode == HostingMode.Management ? "http://source:8080" : "http://source:8600";
    private static object CompareChannels(IReadOnlyList<ChannelDefinition> canonical, IReadOnlyList<ChannelDefinition> actual)
    {
        var expected = canonical.Select(channel => channel.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = actual.Select(channel => channel.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new { missing = expected.Except(present).ToArray(), added = present.Except(expected).ToArray() };
    }
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, bool throwOnError = true)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed ({result.ExitCode}).\n{result.Output}\n{result.Error}");
        }
        return result;
    }
}

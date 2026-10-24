namespace Lineup.Streaming.Sweep;

internal static class SweepApplication
{
    public static async Task RunAsync(string[] args)
    {
        var parsed = SweepCommand.Parse(args);
        args = parsed.Arguments;
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "verify-outcome-reporting";
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var projectRoot = Path.Combine(repositoryRoot, "tests", "Integration", "Lineup.Streaming.Sweep");
        var root = parsed.OutputRoot ?? Environment.GetEnvironmentVariable("LINEUP_SWEEP_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(repositoryRoot, "out", "integration", "streaming-sweep");
        }
        if (command == "report-only" && parsed.OutputRoot is null)
        {
            root = Path.GetDirectoryName(parsed.InputRun ?? throw new ArgumentException("report-only requires --input-run <sweep-run.json>."))!;
        }
        root = Path.GetFullPath(root);
        var repositoryOutputRoot = Path.Combine(repositoryRoot, "out");
        if (IsPathWithin(root, repositoryRoot) && !IsPathWithin(root, repositoryOutputRoot))
        {
            throw new InvalidOperationException(
                $"LINEUP_SWEEP_OUTPUT_ROOT must be outside the repository or beneath '{repositoryOutputRoot}'.");
        }
        if (command == "resume" && parsed.InputRun is not null)
        {
            var resumeRoot = Path.GetDirectoryName(parsed.InputRun)!;
            if (IsPathWithin(resumeRoot, repositoryRoot) && !IsPathWithin(resumeRoot, repositoryOutputRoot))
            {
                throw new ArgumentException("Resume artifacts must be outside the repository or beneath out.");
            }
        }
        if (parsed.Options.BrowserProfile is { } profilePath && IsPathWithin(profilePath, repositoryRoot) && !IsPathWithin(profilePath, repositoryOutputRoot))
        {
            throw new ArgumentException("Browser profiles must be outside the repository or beneath out.");
        }
        if (command == "report-only")
        {
            if (JsonSerializer.Serialize(parsed.Options) != JsonSerializer.Serialize(new SweepOptions()))
            {
                throw new ArgumentException("report-only accepts only --input-run and --output, not execution options.");
            }
            var input = parsed.InputRun ?? throw new ArgumentException("report-only requires --input-run <sweep-run.json>.");
            var run = await SweepStore.RegenerateReportsAsync(input, root);
            Environment.ExitCode = SweepPolicy.ExitCode(run);
            return;
        }
        var harness = new SweepHarness(root, repositoryRoot, projectRoot);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = parsed.Options;
            if (command == "resume")
            {
                var input = parsed.InputRun ?? throw new ArgumentException("resume requires --input-run <sweep-run.json>.");
                var previous = await SweepStore.LoadAsync(input);
                if (JsonSerializer.Serialize(options) != JsonSerializer.Serialize(new SweepOptions()))
                {
                    throw new ArgumentException("Resume uses persisted effective options; do not supply execution options.");
                }
                harness.Configure(previous.Options, cancellation.Token);
                await harness.ResumeAsync(input);
                return;
            }
            harness.Configure(options, cancellation.Token);
            if (command is "existing" or "sync-only")
            {
                await harness.ExecuteAsync(runId: $"existing-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                return;
            }
            if (command == "generate")
            {
                await harness.GenerateAsync();
                return;
            }
            if (command == "smoke")
            {
                await harness.GenerateAsync();
                harness.Configure(options with { MaximumChannels = 1, Matrices = ["local-management"] }, cancellation.Token);
                await harness.ExecuteAsync(runId: $"smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                return;
            }
            if (command == "execute")
            {
                await harness.GenerateAsync();
                await harness.ExecuteAsync();
                return;
            }
            if (command == "execute-fresh")
            {
                await harness.GenerateAsync();
                await harness.ExecuteAsync(runId: $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                return;
            }
            if (command == "execute-channel")
            {
                var channel = args.Skip(1).FirstOrDefault();
                ArgumentException.ThrowIfNullOrWhiteSpace(channel);
                await harness.GenerateAsync();
                await harness.ExecuteAsync(
                    new HashSet<string>([channel], StringComparer.OrdinalIgnoreCase),
                    $"channel-{channel.Replace('.', '_')}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                return;
            }
            if (command == "execute-local-channel")
            {
                var channel = args.Skip(1).FirstOrDefault();
                ArgumentException.ThrowIfNullOrWhiteSpace(channel);
                await harness.GenerateAsync();
                await harness.ExecuteAsync(
                    new HashSet<string>([channel], StringComparer.OrdinalIgnoreCase),
                    $"local-channel-{channel.Replace('.', '_')}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    new HashSet<string>(["local-management"], StringComparer.OrdinalIgnoreCase));
                return;
            }
            if (command == "execute-channels")
            {
                var channels = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (channels.Count == 0)
                {
                    throw new ArgumentException("At least one channel is required.", nameof(args));
                }
                await harness.GenerateAsync();
                await harness.ExecuteAsync(channels, $"channels-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                return;
            }
            if (command == "execute-remote-channels")
            {
                var channels = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (channels.Count == 0)
                {
                    throw new ArgumentException("At least one channel is required.", nameof(args));
                }
                await harness.GenerateAsync();
                await harness.ExecuteAsync(
                    channels,
                    $"remote-channels-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    new HashSet<string>(["remote-management", "remote-managed-process", "remote-external-process"], StringComparer.OrdinalIgnoreCase));
                return;
            }
            if (command == "verify-outcome-reporting")
            {
                await harness.VerifyOutcomeReportingAsync();
                return;
            }
            if (command == "recheck-post-fix-failures")
            {
                await harness.RecheckPostFixFailuresAsync(parsed.InputRun ?? throw new ArgumentException("Failure retry requires --input-run <sweep-run.json>."));
                return;
            }
            if (command == "verify-issue1")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue1Async();
                return;
            }
            if (command == "verify-issue2")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue2Async();
                return;
            }
            if (command == "verify-issue3")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue3Async();
                return;
            }
            if (command == "verify-issue4")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue4Async();
                return;
            }
            if (command == "verify-issue5")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue5Async();
                return;
            }
            if (command == "verify-issue6")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue6Async(args.Skip(1).FirstOrDefault());
                return;
            }
            if (command == "verify-issue7")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue7Async(args.Skip(1).FirstOrDefault());
                return;
            }
            if (command == "verify-issue8")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue8Async(args.Skip(1).FirstOrDefault());
                return;
            }
            if (command == "verify-issue9")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue9Async(args.Skip(1).FirstOrDefault());
                return;
            }
            if (command == "verify-issue10")
            {
                await harness.GenerateAsync();
                await harness.VerifyIssue10Async(args.Skip(1).FirstOrDefault());
                return;
            }
            throw new ArgumentException($"Unknown command '{command}'.");
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    static string FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate Lineup.slnx from '{startPath}'.");
    }

    static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(parentPath), Path.GetFullPath(candidatePath));
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
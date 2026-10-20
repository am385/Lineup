namespace Lineup.Streaming.Sweep;

internal static class SweepApplication
{
    public static async Task RunAsync(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var projectRoot = Path.Combine(repositoryRoot, "tests", "Integration", "Lineup.Streaming.Sweep");
        var root = Environment.GetEnvironmentVariable("LINEUP_SWEEP_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(repositoryRoot, "out", "integration", "streaming-sweep");
        }
        root = Path.GetFullPath(root);
        var repositoryOutputRoot = Path.Combine(repositoryRoot, "out");
        if (IsPathWithin(root, repositoryRoot) && !IsPathWithin(root, repositoryOutputRoot))
        {
            throw new InvalidOperationException(
                $"LINEUP_SWEEP_OUTPUT_ROOT must be outside the repository or beneath '{repositoryOutputRoot}'.");
        }
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "verify-outcome-reporting";
        var harness = new SweepHarness(root, repositoryRoot, projectRoot);
        if (command == "generate")
        {
            await harness.GenerateAsync();
            return;
        }
        if (command == "smoke")
        {
            await harness.GenerateAsync();
            await harness.RunSmokeAsync();
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
            await harness.GenerateAsync();
            await harness.RecheckPostFixFailuresAsync();
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
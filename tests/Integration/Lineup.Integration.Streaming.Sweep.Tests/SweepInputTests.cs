using System.Diagnostics;
using System.Text.Json.Nodes;
using Lineup.Integration.Streaming.Sweep;
using Xunit;

namespace Lineup.Integration.Streaming.Sweep.Tests;

/// <summary>Exercises the real command entry point with isolated environment inputs, without Docker, browsers or hardware.</summary>
public sealed class SweepInputTests
{
    /// <summary>Every execution alias rejects cached tuner A before attempting dependencies for tuner B.</summary>
    [Theory]
    [InlineData("generate")]
    [InlineData("execute")]
    [InlineData("execute-fresh")]
    [InlineData("smoke")]
    [InlineData("execute-local-channel", "7.1")]
    [InlineData("execute-remote-channels", "7.1")]
    public async Task CachedTunerMismatch_RejectsBeforeSideEffects(string command, string? channel = null)
    {
        // Arrange
        using var fixture = new InputFixture();
        await fixture.GenerateAsync();
        var before = fixture.Snapshot();

        // Act
        var result = await fixture.RunAsync("tuner-b.invalid", channel is null ? [command] : [command, channel]);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sweep input", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tuner-a.invalid", result.Error);
        Assert.Contains("tuner-b.invalid", result.Error);
        Assert.Contains("--output", result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    /// <summary>Validation inspects all cached matrices before rewriting any earlier template or compose file.</summary>
    [Theory]
    [InlineData("address")]
    [InlineData("missing-address")]
    [InlineData("missing-source")]
    [InlineData("consumer-target")]
    [InlineData("malformed")]
    [InlineData("split-source")]
    [InlineData("split-consumer")]
    [InlineData("consumer-identity")]
    [InlineData("missing-consumer")]
    public async Task InconsistentTemplate_RejectsWithoutRegeneration(string corruption)
    {
        // Arrange
        using var fixture = new InputFixture();
        await fixture.GenerateAsync();
        var source = Path.Combine(fixture.Root, "matrices", "remote-external-process", "templates", "source", "settings.json");
        var consumer = Path.Combine(fixture.Root, "matrices", "remote-external-process", "templates", "consumer", "settings.json");
        var path = corruption is "consumer-target" or "split-consumer" or "consumer-identity" or "missing-consumer" ? consumer : source;
        if (corruption is "missing-source" or "missing-consumer")
        {
            File.Delete(path);
        }
        else if (corruption == "malformed")
        {
            await File.WriteAllTextAsync(path, "{", TestContext.Current.CancellationToken);
        }
        else
        {
            var settings = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
            var configuration = settings["Streaming"]!["PhysicalDevices"]![0]!["Adapter"]!["Configuration"]!;
            if (corruption is "split-source" or "split-consumer")
            {
                path = Path.Combine(Path.GetDirectoryName(path)!, "streaming.json");
                configuration[corruption == "split-consumer" ? "BaseUrl" : "Address"] = "tuner-b.invalid";
                settings = settings["Streaming"]!.DeepClone();
            }
            else if (corruption == "consumer-identity")
            {
                configuration["VirtualDeviceId"] = Guid.NewGuid().ToString();
            }
            else if (corruption == "missing-address")
            {
                configuration.AsObject().Remove("Address");
            }
            else
            {
                configuration[corruption == "consumer-target" ? "BaseUrl" : "Address"] = "tuner-b.invalid";
            }
            await File.WriteAllTextAsync(path, settings.ToJsonString(), TestContext.Current.CancellationToken);
        }
        var before = fixture.Snapshot();

        // Act
        var result = await fixture.RunAsync("tuner-a.invalid", ["execute-fresh"]);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sweep input", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    /// <summary>Resume and retry validate both actual templates and recorded tuner provenance without changing prior evidence.</summary>
    [Theory]
    [InlineData("resume", "template")]
    [InlineData("resume", "checkpoint")]
    [InlineData("resume", "missing-provenance")]
    [InlineData("recheck-post-fix-failures", "checkpoint")]
    [InlineData("recheck-post-fix-failures", "missing-provenance")]
    public async Task ResumeOrRecheck_RejectsUnprovenTuner(string command, string mismatch)
    {
        // Arrange
        using var fixture = new InputFixture();
        await fixture.GenerateAsync();
        var run = new SweepRun
        {
            Id = "resume-test",
            Options = new SweepOptions
            {
                TimestampEvidenceVersion = 1,
                BrowserExecutable = Path.Combine(fixture.Root, "browser-must-not-start")
            }
        };
        if (mismatch != "missing-provenance")
        {
            run.Environment["tunerAddress"] = mismatch == "checkpoint" ? "tuner-b.invalid" : "tuner-a.invalid";
        }
        var runRoot = Path.Combine(fixture.Root, "runs", run.Id);
        await SweepStore.SaveAsync(runRoot, run);
        var input = Path.Combine(runRoot, "sweep-run.json");
        var before = await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken);

        // Act
        var result = await fixture.RunAsync(mismatch == "template" ? "tuner-b.invalid" : "tuner-a.invalid", [command, "--input-run", input]);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sweep input", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mismatch == "missing-provenance" ? "<missing>" : "tuner-b.invalid", result.Error);
        Assert.Equal(before, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(runRoot, "browser-preflight")));
    }

    /// <summary>Matching inputs preserve generated access tokens and reuse templates without silent regeneration.</summary>
    [Theory]
    [InlineData("tuner-a.invalid")]
    [InlineData(" TUNER-A.INVALID ")]
    public async Task SameTuner_ReusesExistingTemplates(string tuner)
    {
        // Arrange
        using var fixture = new InputFixture();
        await fixture.GenerateAsync();
        var before = fixture.Snapshot();

        // Act
        var result = await fixture.RunAsync(tuner, ["generate"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, fixture.Snapshot());
    }

    /// <summary>Generation cannot rewrite templates or compose state while another sweep owns the output root.</summary>
    [Fact]
    public async Task LockedOutputRoot_RejectsGenerationWithoutMutation()
    {
        // Arrange
        using var fixture = new InputFixture();
        await fixture.GenerateAsync();
        var before = fixture.Snapshot();
        await using var lease = new FileStream(Path.Combine(fixture.Root, "sweep.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = await fixture.RunAsync("tuner-a.invalid", ["generate"]);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sweep.lock", result.Error);
        await lease.DisposeAsync();
        Assert.Equal(before, fixture.Snapshot());
    }

    /// <summary>A deliberate new output root can generate settings for a different tuner.</summary>
    [Fact]
    public async Task NewOutputRoot_GeneratesRequestedTuner()
    {
        // Arrange
        using var fixture = new InputFixture();

        // Act
        var result = await fixture.RunAsync("tuner-b.invalid", ["generate"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        var source = Path.Combine(fixture.Root, "matrices", "local-management", "templates", "source", "settings.json");
        Assert.Contains("tuner-b.invalid", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    /// <summary>Cancellation is observed before generation creates artifacts or requires a tuner environment.</summary>
    [Fact]
    public async Task CanceledGeneration_DoesNotWriteArtifacts()
    {
        // Arrange
        using var fixture = new InputFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var harness = new SweepHarness(fixture.Root, fixture.Root, fixture.Root);
        harness.Configure(new SweepOptions(), cancellation.Token);

        // Act
        var error = await Record.ExceptionAsync(harness.GenerateAsync);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Empty(fixture.Snapshot());
    }

    /// <summary>Existing-instance execution reaches normal dependency preflight without a tuner variable or generated templates.</summary>
    [Fact]
    public async Task ExistingInstance_DoesNotRequireGeneratedTunerInputs()
    {
        // Arrange
        using var fixture = new InputFixture();

        // Act
        var result = await fixture.RunAsync(string.Empty, ["existing", "--base-url", "http://existing.invalid"]);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The configured sweep browser executable does not exist.", result.Error);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "matrices")));
        Assert.DoesNotContain("LINEUP_SWEEP_TUNER_ADDRESS", result.Error);
    }

    private sealed class InputFixture : IDisposable
    {
        /// <summary>Gets the isolated, owned output root.</summary>
        public string Root { get; } = Directory.CreateTempSubdirectory("lineup-sweep-input-").FullName;

        /// <summary>Generates the real six-matrix templates without starting dependencies.</summary>
        public async Task GenerateAsync()
        {
            var result = await RunAsync("tuner-a.invalid", ["generate"]);
            Assert.True(result.ExitCode == 0, result.Error);
        }

        /// <summary>Captures artifact contents to detect mutation or silent regeneration.</summary>
        public SortedDictionary<string, string> Snapshot() => new(Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllText), StringComparer.Ordinal);

        /// <summary>Runs a bounded, owned child with isolated environment inputs and an unavailable browser guard.</summary>
        public async Task<ProcessResult> RunAsync(string tuner, string[] arguments)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var tests = typeof(SweepInputTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[]
            {
                "exec", "--runtimeconfig", Path.ChangeExtension(tests, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(tests, ".deps.json"), typeof(SweepHarness).Assembly.Location
            }.Concat(arguments).Concat(["--output", Root]))
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["LINEUP_SWEEP_TUNER_ADDRESS"] = tuner;
            start.Environment["LINEUP_SWEEP_BROWSER_EXECUTABLE"] = Path.Combine(Root, "browser-must-not-start");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned offline sweep command.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                return new ProcessResult(process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

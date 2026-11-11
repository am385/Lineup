using System.Diagnostics;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Plugin.WebPlayer.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

/// <summary>Verifies portable media processing remains independent of application orchestration.</summary>
public sealed class PortableMediaTests
{
    /// <summary>Verifies media and worker assemblies reference no trusted application or database implementation.</summary>
    [Fact]
    public void Assemblies_DoNotReferenceTrustedImplementations()
    {
        // Arrange
        var assemblies = new[]
        {
            typeof(MediaStreamProbe).Assembly,
            typeof(ActiveStreamPlanFactory).Assembly,
            typeof(Lineup.Plugin.WebPlayer.VirtualOutputHost.DedicatedCmafSessionPlan).Assembly
        };
        var trustedAssemblies = new[]
        {
            "Lineup.Core", "Lineup.Core.Hosting", "Lineup.Core.VirtualOutputs.Runtime"
        };
        var databasePackages = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "SQLitePCLRaw" };

        // Act
        var violations = assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => trustedAssemblies.Contains(name, StringComparer.Ordinal) ||
                databasePackages.Any(prefix => name.Equals(prefix, StringComparison.Ordinal) || name.StartsWith($"{prefix}.", StringComparison.Ordinal)))
            .Select(name => $"{assembly.GetName().Name} -> {name}")).ToArray();

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>Verifies stream-level probing parses media without requiring graph, settings, or acquisition services and leaves input ownership with its caller.</summary>
    [Fact]
    public async Task ProbeAsync_CallerOwnedStream_ReturnsMetadataWithoutDisposingInput()
    {
        // Arrange
        using var input = new MemoryStream();
        var probe = new MediaStreamProbe(NullLogger.Instance, new ProbeProcessFactory());

        // Act
        var result = await probe.ProbeAsync("portable-source", input, TestContext.Current.CancellationToken);

        // Assert
        var audio = Assert.Single(result.Tracks);
        Assert.Equal("ac4", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.True(input.CanRead);
    }

    private sealed class ProbeProcessFactory : IMediaProcessFactory
    {
        /// <inheritdoc />
        public Process Start(MediaProcessRequest request)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("probe-json");
            start.ArgumentList.Add("""{"streams":[{"index":1,"codec_type":"audio","codec_name":"ac4","channels":6,"sample_rate":"48000"}]}""");
            return Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned portable media probe.");
        }
    }
}

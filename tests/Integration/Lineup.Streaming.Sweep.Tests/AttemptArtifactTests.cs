using System.Text.Json;
using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Verifies diagnostic readers follow the browser attempt's immutable artifact identity.</summary>
public sealed class AttemptArtifactTests
{
    /// <summary>Retry ordinals and stale GUID directories cannot override the current writer's evidence.</summary>
    [Theory]
    [InlineData("104.1", 1)]
    [InlineData("7.10", 3)]
    public void ReadActiveStatus_UsesCurrentEvidenceIdentity(string channel, int ordinal)
    {
        // Arrange
        using var directory = new ArtifactDirectory();
        var result = Result(channel, ordinal);
        directory.Write(channel, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), """{"codec":"wrong-numeric"}""");
        directory.Write(channel, Guid.NewGuid().ToString("N"), """{"codec":"wrong-stale"}""");
        directory.Write(channel, result.EvidenceId!, """{"codec":"ac3"}""");

        // Act
        var status = SweepHarness.ReadActiveStatus(directory.Root, channel, result);

        // Assert
        Assert.Equal("ac3", status?["codec"]?.GetValue<string>());
    }

    /// <summary>Missing current diagnostics cannot silently fall back to a previous ordinal directory.</summary>
    [Fact]
    public void ReadActiveStatus_MissingCurrentEvidence_DoesNotUseNumericFallback()
    {
        // Arrange
        using var directory = new ArtifactDirectory();
        var result = Result("104.1", 1);
        directory.Write("104.1", "1", """{"codec":"stale"}""");

        // Act
        var status = SweepHarness.ReadActiveStatus(directory.Root, "104.1", result);

        // Assert
        Assert.Null(status);
    }

    /// <summary>Malformed current evidence is surfaced instead of being mistaken for missing diagnostics.</summary>
    [Fact]
    public void ReadActiveStatus_MalformedCurrentEvidence_Throws()
    {
        // Arrange
        using var directory = new ArtifactDirectory();
        var result = Result("104.1", 1);
        directory.Write("104.1", result.EvidenceId!, "{");

        // Act
        var error = Record.Exception(() => SweepHarness.ReadActiveStatus(directory.Root, "104.1", result));

        // Assert
        Assert.IsAssignableFrom<JsonException>(error);
    }

    /// <summary>Legacy results without an evidence identity fail explicitly instead of inventing provenance.</summary>
    [Fact]
    public void ReadActiveStatus_MissingIdentity_Throws()
    {
        // Arrange
        using var directory = new ArtifactDirectory();
        var result = Result("104.1", 1) with { EvidenceId = null };
        directory.Write("104.1", "1", "{}");

        // Act
        var error = Record.Exception(() => SweepHarness.ReadActiveStatus(directory.Root, "104.1", result));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>Evidence identities must be a single safe path segment, never an escape from the attempt directory.</summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\escape")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(" ")]
    public void ReadActiveStatus_InvalidIdentity_Throws(string identity)
    {
        // Arrange
        using var directory = new ArtifactDirectory();
        var result = Result("104.1", 1) with { EvidenceId = identity };

        // Act
        var error = Record.Exception(() => SweepHarness.ReadActiveStatus(directory.Root, "104.1", result));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    /// <summary>The returned evidence identity survives canonical serialization separately from retry ordinal.</summary>
    [Fact]
    public void Result_RoundTrip_PreservesEvidenceIdentity()
    {
        // Arrange
        var result = Result("104.1", 3);

        // Act
        var restored = JsonSerializer.Deserialize<ChannelAttemptResult>(JsonSerializer.Serialize(result, SweepStore.Json), SweepStore.Json);

        // Assert
        Assert.Equal(result.EvidenceId, restored?.EvidenceId);
        Assert.Equal(3, restored?.Attempt);
    }

    private static ChannelAttemptResult Result(string channel, int ordinal) =>
        new("matrix", channel, "Channel", ordinal, true, "not-applicable", 1, null, null, null, ordinal, null, "pass")
        {
            EvidenceId = Guid.NewGuid().ToString("N")
        };

    private sealed class ArtifactDirectory : IDisposable
    {
        /// <summary>Gets this fixture's exclusively owned temporary artifact root.</summary>
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lineup-sweep-tests", Guid.NewGuid().ToString("N"));

        /// <summary>Creates the owned temporary directory.</summary>
        public ArtifactDirectory() => Directory.CreateDirectory(Root);

        /// <summary>Writes a diagnostic snapshot in an explicit evidence directory.</summary>
        public void Write(string channel, string identity, string json)
        {
            var root = SweepHarness.GetAttemptArtifactRoot(Root, channel, identity);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "active-status.json"), json);
        }

        /// <summary>Removes only this fixture's temporary directory.</summary>
        public void Dispose() => Directory.Delete(Root, true);
    }
}

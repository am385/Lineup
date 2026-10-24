using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Verifies a browser audio pass requires an explicitly positive decode counter or analyser peak.</summary>
public sealed class AudioDecodeEvidenceTests
{
    /// <summary>Absent, zero, and negative metrics are not positive audio evidence.</summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, -1, false)]
    [InlineData(0L, null, false)]
    [InlineData(-1L, null, false)]
    [InlineData(0L, 0, false)]
    [InlineData(0L, -1, false)]
    [InlineData(-1L, 0, false)]
    [InlineData(-1L, -1, false)]
    [InlineData(1L, null, true)]
    [InlineData(1L, 0, true)]
    [InlineData(1L, -1, true)]
    [InlineData(null, 1, true)]
    [InlineData(0L, 1, true)]
    [InlineData(-1L, 1, true)]
    [InlineData(1L, 1, true)]
    public void HasAudioDecodeEvidence_RequiresPositiveEvidence(long? bytes, int? peak, bool expected)
    {
        // Arrange
        var metrics = new PlayerMetrics(0, 30, 4, 1920, 1080, null, bytes, 600, 0, 0, 0, [], peak, 4, null, []);

        // Act
        var actual = SweepPolicy.HasAudioDecodeEvidence(metrics);

        // Assert
        Assert.Equal(expected, actual);
    }
}

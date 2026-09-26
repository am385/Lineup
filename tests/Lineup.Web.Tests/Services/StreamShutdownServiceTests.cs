using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies process-wide stream shutdown coordination.
/// </summary>
public class StreamShutdownServiceTests
{
    /// <summary>
    /// Verifies active streams drain before shared tuner sources stop.
    /// </summary>
    [Fact]
    public async Task StopAsync_ActiveStreams_DrainsBeforeStoppingTunerSources()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var tunerStreams = Substitute.For<ITunerStreamMultiplexer>();
        activeStreams.StopAllAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        tunerStreams.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new StreamShutdownService(activeStreams, tunerStreams, NullLogger<StreamShutdownService>.Instance);

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Received.InOrder(() =>
        {
            activeStreams.StopAllAsync(Arg.Any<CancellationToken>());
            tunerStreams.StopAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// Verifies a stream drain timeout still forces shared tuner shutdown.
    /// </summary>
    [Fact]
    public async Task StopAsync_StreamDrainTimesOut_StopsTunerSources()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var tunerStreams = Substitute.For<ITunerStreamMultiplexer>();
        activeStreams.StopAllAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));
        activeStreams.GetActiveStreams().Returns([]);
        tunerStreams.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new StreamShutdownService(
            activeStreams,
            tunerStreams,
            NullLogger<StreamShutdownService>.Instance,
            TimeSpan.FromMilliseconds(20));

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        await tunerStreams.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies an unexpected stream-stop failure does not bypass shared tuner shutdown.
    /// </summary>
    [Fact]
    public async Task StopAsync_StreamDrainFails_StopsTunerSources()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var tunerStreams = Substitute.For<ITunerStreamMultiplexer>();
        activeStreams.StopAllAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("stop failed"));
        tunerStreams.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new StreamShutdownService(activeStreams, tunerStreams, NullLogger<StreamShutdownService>.Instance);

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        await tunerStreams.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies a tuner-stop timeout does not block application shutdown indefinitely.
    /// </summary>
    [Fact]
    public async Task StopAsync_TunerShutdownTimesOut_Returns()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var tunerStreams = Substitute.For<ITunerStreamMultiplexer>();
        activeStreams.StopAllAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        tunerStreams.StopAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));
        var service = new StreamShutdownService(
            activeStreams,
            tunerStreams,
            NullLogger<StreamShutdownService>.Instance,
            TimeSpan.FromMilliseconds(20));

        // Act
        var exception = await Record.ExceptionAsync(() => service.StopAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
    }
}

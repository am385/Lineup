using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies the shared diagnostic reader's cancellation, ownership, and error behavior.</summary>
public sealed class MediaProcessDiagnosticsTests
{
    /// <summary>Verifies retained failure events preserve correlation and upstream exit status even when the downstream processor was terminated.</summary>
    [Fact]
    public void LogFailure_UpstreamProcessorFailure_RetainsBoundedCorrelatedDetails()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sourceFailure = new MediaProcessException(234, new string('x', 65_536) + "corrupt-packet");
        var failure = new MediaInputException("The upstream stream failed.", sourceFailure);

        // Act
        MediaProcessDiagnostics.LogFailure(logger, failure, "source", "media-session", "broker-request", "stream-session", -1, "downstream was terminated");

        // Assert
        Assert.Equal("source", logger.Values["StartupStage"]);
        Assert.Equal("media-session", logger.Values["MediaSessionId"]);
        Assert.Equal("broker-request", logger.Values["BrokerRequestId"]);
        Assert.Equal("stream-session", logger.Values["StreamSessionId"]);
        Assert.Equal(-1, logger.Values["ProcessExitCode"]);
        Assert.Equal(234, logger.Values["UpstreamProcessExitCode"]);
        var sourceDiagnostics = Assert.IsType<string>(logger.Values["UpstreamDiagnosticTail"]);
        Assert.True(sourceDiagnostics.Length <= 16_384);
        Assert.EndsWith("corrupt-packet", sourceDiagnostics, StringComparison.Ordinal);
        Assert.Equal("downstream was terminated", logger.Values["DiagnosticTail"]);
    }

    /// <summary>Verifies live CMAF diagnostics remain bounded even when a single log line or accumulated queue exceeds the retention limit.</summary>
    [Fact]
    public void AppendLine_HighVolume_BoundsCharactersAndLines()
    {
        // Arrange
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        MediaProcessDiagnostics.AppendLine(lines, new string('x', 65_536));

        // Act
        for (var index = 0; index < 1000; index++)
        {
            MediaProcessDiagnostics.AppendLine(lines, new string('y', 1000) + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Assert
        Assert.InRange(lines.Count, 1, 50);
        Assert.True(string.Join("\n", lines).Length <= 16_384);
        Assert.EndsWith("999", lines.Last(), StringComparison.Ordinal);
    }

    /// <summary>Verifies canceling an open reader interrupts drainage without disposing the process-owned reader.</summary>
    [Fact]
    public async Task ReadTailAsync_OpenReader_CancelsAfterDrainingBoundedChunks()
    {
        // Arrange
        using var reader = new OpenReader();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = MediaProcessDiagnostics.ReadTailAsync(reader, cancellation.Token);
        await reader.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        await cancellation.CancelAsync();
        var exception = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(exception).CancellationToken);
        Assert.Equal(1_048_576, reader.CharactersRead);
        Assert.InRange(reader.MaximumBufferLength, 1, 16_384);
        Assert.False(reader.IsDisposed);
    }

    /// <summary>Verifies an already-canceled caller is observed even by synchronous text readers.</summary>
    [Fact]
    public async Task ReadTailAsync_PreCanceledCaller_DoesNotRead()
    {
        // Arrange
        using var reader = new StringReader("final-error");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() => MediaProcessDiagnostics.ReadTailAsync(reader, cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(exception).CancellationToken);
        Assert.Equal('f', reader.Peek());
    }

    /// <summary>Verifies stream errors propagate without substituting a successful empty diagnostic tail.</summary>
    [Fact]
    public async Task ReadTailAsync_ReadFailure_Propagates()
    {
        // Arrange
        var failure = new IOException("Diagnostic pipe failed.");
        using var reader = new FailingReader(failure);

        // Act
        var exception = await Record.ExceptionAsync(() => MediaProcessDiagnostics.ReadTailAsync(reader, TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(failure, exception);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        /// <summary>Gets retained structured values from the failure event.</summary>
        public IReadOnlyDictionary<string, object?> Values { get; private set; } = new Dictionary<string, object?>();

        /// <summary>Executes begin scope.</summary>
        /// <param name="state">The state value.</param>
        /// <typeparam name="TState">Type of the state value.</typeparam>
        /// <returns>The operation's result.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>Determines whether enabled.</summary>
        /// <param name="logLevel">The log level value.</param>
        /// <returns>The operation's result.</returns>
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        /// <summary>Executes log.</summary>
        /// <param name="logLevel">The log level value.</param>
        /// <param name="eventId">The event ID value.</param>
        /// <param name="state">The state value.</param>
        /// <param name="exception">The exception value.</param>
        /// <param name="formatter">The formatter value.</param>
        /// <typeparam name="TState">Type of the state value.</typeparam>
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }

    private sealed class OpenReader : TextReader
    {
        /// <summary>Gets the signal raised after all synthetic input has been consumed.</summary>
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the total consumed characters.</summary>
        public int CharactersRead { get; private set; }
        /// <summary>Gets the largest requested read buffer.</summary>
        public int MaximumBufferLength { get; private set; }
        /// <summary>Gets whether the borrowed reader has been disposed.</summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            MaximumBufferLength = Math.Max(MaximumBufferLength, buffer.Length);
            if (CharactersRead < 1_048_576)
            {
                var count = Math.Min(1_048_576 - CharactersRead, buffer.Length);
                buffer.Span[..count].Fill('x');
                CharactersRead += count;
                return count;
            }
            Drained.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReader(IOException failure) : TextReader
    {
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);
    }
}

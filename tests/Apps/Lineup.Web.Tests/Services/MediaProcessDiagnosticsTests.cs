using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies the shared diagnostic reader's cancellation, ownership, and error behavior.</summary>
public sealed class MediaProcessDiagnosticsTests
{
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

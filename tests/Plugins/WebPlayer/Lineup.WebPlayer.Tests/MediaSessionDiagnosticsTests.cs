using System.Text;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies dedicated media and caption diagnostics are drained with bounded retention and useful final errors.</summary>
public sealed class MediaSessionDiagnosticsTests
{
    /// <summary>Verifies short errors and empty output remain readable with normalized line endings.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("Final codec failure.", "Final codec failure.")]
    [InlineData("first\nlast", "first\nlast")]
    [InlineData("first\r\nlast\r\n", "first\nlast")]
    [InlineData("\r\n\r\n", "")]
    public async Task ReadDiagnosticTailAsync_ShortOutput_PreservesDiagnostics(string input, string expected)
    {
        // Arrange
        using var reader = new StringReader(input);

        // Act
        var diagnostics = await MediaSession.ReadDiagnosticTailAsync(reader);

        // Assert
        Assert.Equal(expected, diagnostics);
    }

    /// <summary>Verifies continuous FFmpeg lines retain only the newest fifty useful diagnostics for every common line separator.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ReadDiagnosticTailAsync_HighVolume_RetainsLatestFiftyLines(string separator)
    {
        // Arrange
        var lines = Enumerable.Range(0, 10_000).Select(index => $"diagnostic-{index:D5}").ToArray();
        using var reader = new StringReader(string.Join(separator, lines));

        // Act
        var diagnostics = await MediaSession.ReadDiagnosticTailAsync(reader);

        // Assert
        Assert.Equal(string.Join("\n", lines.TakeLast(50)), diagnostics);
        Assert.Equal(50, diagnostics.Split('\n').Length);
        Assert.True(diagnostics.Length <= 16_384);
    }

    /// <summary>Verifies an individual line cannot exceed the character limit, including at exact buffer and wrap boundaries.</summary>
    [Theory]
    [InlineData(16_383)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    [InlineData(65_543)]
    public async Task ReadDiagnosticTailAsync_UnterminatedLine_RetainsBoundedSuffix(int length)
    {
        // Arrange
        var text = new string('x', length - 11) + "final-error";
        using var reader = new StringReader(text);

        // Act
        var diagnostics = await MediaSession.ReadDiagnosticTailAsync(reader);

        // Assert
        Assert.Equal(text[^Math.Min(text.Length, 16_384)..], diagnostics);
        Assert.True(diagnostics.Length <= 16_384);
        Assert.EndsWith("final-error", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>Verifies a huge line after earlier history remains bounded while preserving its final failure text.</summary>
    [Fact]
    public async Task ReadDiagnosticTailAsync_LongTrailingLine_DiscardsEarlierHistory()
    {
        // Arrange
        var text = string.Join("\n", Enumerable.Repeat("Earlier diagnostic", 1000)) + "\n" + new string('x', 1_000_000) + "final-error";
        using var reader = new StringReader(text);

        // Act
        var diagnostics = await MediaSession.ReadDiagnosticTailAsync(reader);

        // Assert
        Assert.Equal(text[^16_384..], diagnostics);
        Assert.DoesNotContain("Earlier diagnostic", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>Verifies an open diagnostic stream is continuously consumed through bounded reads rather than buffered by whole-text APIs.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1024)]
    [InlineData(20_000)]
    public async Task ReadDiagnosticTailAsync_OpenStream_DrainsIncrementallyBeforeEof(int chunkSize)
    {
        // Arrange
        var lines = Enumerable.Range(0, 10_000).Select(index => $"diagnostic-{index:D5}").ToArray();
        using var reader = new OpenDiagnosticReader(string.Join("\r\n", lines), chunkSize);
        var pending = MediaSession.ReadDiagnosticTailAsync(reader);

        // Act
        try
        {
            await reader.WaitForDrainAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(pending.IsCompleted);
            Assert.False(reader.WholeTextReadRequested);
            Assert.InRange(reader.MaximumReadBufferLength, 1, 16_384);
        }
        finally
        {
            reader.Complete();
            await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(string.Join("\n", lines.TakeLast(50)), await pending);
    }

    /// <summary>Verifies diagnostic read errors propagate rather than becoming a success-shaped empty or partial tail.</summary>
    [Fact]
    public async Task ReadDiagnosticTailAsync_ReadFailure_PropagatesError()
    {
        // Arrange
        var failure = new IOException("The redirected diagnostic stream failed.");
        using var reader = new FailingDiagnosticReader(failure);

        // Act
        var exception = await Record.ExceptionAsync(() => MediaSession.ReadDiagnosticTailAsync(reader));

        // Assert
        Assert.Same(failure, exception);
    }

    /// <summary>Verifies the process remains the owner of its redirected reader after diagnostic draining completes.</summary>
    [Fact]
    public async Task ReadDiagnosticTailAsync_EndOfStream_DoesNotDisposeReader()
    {
        // Arrange
        using var reader = new StringReader("final-error");

        // Act
        var diagnostics = await MediaSession.ReadDiagnosticTailAsync(reader);

        // Assert
        Assert.Equal("final-error", diagnostics);
        Assert.Equal(-1, reader.Peek());
    }

    private sealed class OpenDiagnosticReader : StreamReader
    {
        private readonly OpenDiagnosticStream _stream;

        public OpenDiagnosticReader(string text, int chunkSize) : this(new OpenDiagnosticStream(text, chunkSize))
        {
        }

        private OpenDiagnosticReader(OpenDiagnosticStream stream) : base(stream)
        {
            _stream = stream;
        }

        public bool WholeTextReadRequested { get; private set; }
        public int MaximumReadBufferLength { get; private set; }

        public Task WaitForDrainAsync(CancellationToken cancellationToken) => _stream.WaitForDrainAsync(cancellationToken);

        public void Complete() => _stream.Complete();

        public override Task<string> ReadToEndAsync()
        {
            WholeTextReadRequested = true;
            return base.ReadToEndAsync();
        }

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            WholeTextReadRequested = true;
            return base.ReadToEndAsync(cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadBufferLength = Math.Max(MaximumReadBufferLength, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class OpenDiagnosticStream(string text, int chunkSize) : Stream
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes(text);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public Task WaitForDrainAsync(CancellationToken cancellationToken) => _drained.Task.WaitAsync(cancellationToken);

        public void Complete() => _end.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _data.Length)
            {
                var count = Math.Min(Math.Min(buffer.Length, chunkSize), _data.Length - _offset);
                _data.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            _drained.TrySetResult();
            await _end.Task.WaitAsync(cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
            }
            base.Dispose(disposing);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingDiagnosticReader(IOException failure) : TextReader
    {
        public override Task<string> ReadToEndAsync() => Task.FromException<string>(failure);

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => Task.FromException<string>(failure);

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);

        public override Task<int> ReadAsync(char[] buffer, int index, int count) => Task.FromException<int>(failure);
    }
}

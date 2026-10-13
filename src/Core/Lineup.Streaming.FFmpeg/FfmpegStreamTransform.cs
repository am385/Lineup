using System.Diagnostics;

namespace Lineup.Streaming.FFmpeg;

/// <summary>
/// Runs one reusable FFmpeg standard-input to standard-output transform.
/// </summary>
public sealed class FfmpegStreamTransform : IStreamTransform
{
    private readonly IReadOnlyList<string> _arguments;
    private readonly IMediaProcessFactory _processFactory;
    private readonly ILogger _logger;
    private readonly string _sourceName;

    /// <summary>Initializes an FFmpeg stream transform.</summary>
    public FfmpegStreamTransform(IReadOnlyList<string> arguments, IMediaProcessFactory processFactory, ILogger logger, string sourceName)
    {
        _arguments = arguments;
        _processFactory = processFactory;
        _logger = logger;
        _sourceName = sourceName;
    }

    /// <inheritdoc />
    public ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = _processFactory.Start(new MediaProcessRequest
        {
            FileName = "ffmpeg",
            Arguments = _arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        });
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpTask = MediaStreamPump.PumpAsync(input, process, _logger, _sourceName, lifetime.Token);
        var errorTask = process.StandardError.ReadToEndAsync(lifetime.Token);
        return ValueTask.FromResult<IStreamSourceLease>(new ProcessOutputLease(process, pumpTask, errorTask, lifetime));
    }

    private sealed class ProcessOutputLease : IStreamSourceLease
    {
        private Process? _process;
        private readonly Task _pumpTask;
        private readonly Task<string> _errorTask;
        private readonly CancellationTokenSource _lifetime;
        private readonly Stream _output;

        public ProcessOutputLease(Process process, Task pumpTask, Task<string> errorTask, CancellationTokenSource lifetime)
        {
            _process = process;
            _pumpTask = pumpTask;
            _errorTask = errorTask;
            _lifetime = lifetime;
            _output = new ValidatedOutputStream(process.StandardOutput.BaseStream, process, errorTask);
        }

        public Stream Stream => _process is null ? Stream.Null : _output;

        public string? ContentType => "video/mp2t";

        public async ValueTask DisposeAsync()
        {
            var process = Interlocked.Exchange(ref _process, null);
            if (process is null)
            {
                return;
            }
            await _lifetime.CancelAsync();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await ObserveAsync(_pumpTask);
            await ObserveAsync(_errorTask);
            process.Dispose();
            _lifetime.Dispose();
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private sealed class ValidatedOutputStream(Stream output, Process process, Task<string> errorTask) : Stream
        {
            private readonly object _gate = new();
            private Task? _validation;

            public override bool CanRead => output.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = output.Read(buffer, offset, count);
                if (read == 0)
                {
                    EnsureSuccessAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await output.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    await EnsureSuccessAsync(cancellationToken);
                }
                return read;
            }

            private Task EnsureSuccessAsync(CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    return _validation ??= ValidateAsync(cancellationToken);
                }
            }

            private async Task ValidateAsync(CancellationToken cancellationToken)
            {
                await process.WaitForExitAsync(cancellationToken);
                var error = await errorTask.WaitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new MediaProcessException($"FFmpeg exited with code {process.ExitCode}: {error.Trim()}");
                }
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

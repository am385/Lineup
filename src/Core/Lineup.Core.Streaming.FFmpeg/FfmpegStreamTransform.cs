using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Owns the readable output and process lifetime of one portable media transform.</summary>
public interface IMediaStreamLease : IAsyncDisposable
{
    /// <summary>Gets the readable media output.</summary>
    Stream Stream { get; }

    /// <summary>Gets the output content type when known.</summary>
    string? ContentType { get; }

    /// <summary>Gets the encoder outcome of the launched transform, or null when the producer does not report one.</summary>
    VideoEncodingSnapshot? VideoEncoding => null;

    /// <summary>Gets the owned process resource identities, without transferring process lifetime ownership.</summary>
    IReadOnlyList<string> ResourceIds => [];
}

/// <summary>
/// Runs one reusable FFmpeg standard-input to standard-output transform.
/// </summary>
public sealed class FfmpegStreamTransform
{
    private readonly IReadOnlyList<string> _arguments;
    private readonly IMediaProcessFactory _processFactory;
    private readonly ILogger _logger;
    private readonly string _sourceName;
    private readonly HardwareEncodingSettings? _hardware;
    private readonly IReadOnlyList<string>? _softwareArguments;

    /// <summary>Initializes an FFmpeg stream transform.</summary>
    public FfmpegStreamTransform(
        IReadOnlyList<string> arguments,
        IMediaProcessFactory processFactory,
        ILogger logger,
        string sourceName,
        HardwareEncodingSettings? hardware = null,
        IReadOnlyList<string>? softwareArguments = null
    )
    {
        _arguments = arguments;
        _processFactory = processFactory;
        _logger = logger;
        _sourceName = sourceName;
        _hardware = hardware;
        _softwareArguments = softwareArguments;
    }

    /// <summary>Starts the media process and returns ownership of its output and cleanup without taking ownership of the input stream.</summary>
    public async ValueTask<IMediaStreamLease> StartAsync(Stream input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = _arguments;
        var usingHardware = false;
        var requiresHardware = _hardware is { Encoder: not HardwareVideoEncoder.Software } && _arguments.Contains(HardwareVideoEncodingPlanner.GetEncoderName(_hardware.Encoder));
        if (requiresHardware)
        {
            usingHardware = await HardwareEncodingRuntime.VerifyAsync(_hardware!, _processFactory, _logger, cancellationToken);
            if (!usingHardware)
            {
                arguments = _softwareArguments ?? throw new InvalidOperationException("The software fallback plan is missing.");
            }
        }
        while (true)
        {
            var process = _processFactory.Start(new MediaProcessRequest
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            });
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumpTask = MediaStreamPump.PumpAsync(input, process, _logger, _sourceName, lifetime.Token);
            var errorTask = MediaProcessDiagnostics.ReadTailAsync(process.StandardError, lifetime.Token);
            var encoding = new VideoEncodingSnapshot(HardwareVideoEncodingPlanner.GetVideoEncoder(arguments), _hardware?.Encoder, requiresHardware && !usingHardware);
            var lease = new ProcessOutputLease(process, pumpTask, errorTask, lifetime, encoding);
            if (!usingHardware)
            {
                return lease;
            }
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(20));
                await lease.PrimeAsync(startup.Token);
                return lease;
            }
            catch (MediaProcessException exception) when (_hardware is { AllowSoftwareFallback: true } hardware && HardwareEncodingRuntime.IsInitializationFailure(exception.Diagnostics))
            {
                await lease.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogWarning(exception, "Requested hardware encoder {Encoder} failed before publication. Owned media was released; retrying with software.", hardware.Encoder);
                arguments = _softwareArguments ?? throw new InvalidOperationException("The software fallback plan is missing.");
                usingHardware = false;
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class ProcessOutputLease : IMediaStreamLease
    {
        private Process? _process;
        private readonly Task _pumpTask;
        private readonly Task<string> _errorTask;
        private readonly CancellationTokenSource _lifetime;
        private readonly ValidatedOutputStream _output;

        /// <summary>Initializes the process output lease.</summary>
        /// <param name="process">The process value.</param>
        /// <param name="pumpTask">The pump task value.</param>
        /// <param name="errorTask">The error task value.</param>
        /// <param name="lifetime">The lifetime value.</param>
        /// <param name="videoEncoding">The outcome of the launched transform.</param>
        public ProcessOutputLease(Process process, Task pumpTask, Task<string> errorTask, CancellationTokenSource lifetime, VideoEncodingSnapshot videoEncoding)
        {
            _process = process;
            _pumpTask = pumpTask;
            _errorTask = errorTask;
            _lifetime = lifetime;
            _output = new ValidatedOutputStream(process.StandardOutput.BaseStream, process, errorTask);
            VideoEncoding = videoEncoding;
        }

        /// <inheritdoc />
        public VideoEncodingSnapshot VideoEncoding { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> ResourceIds => _process is { } process ? OwnedMediaProcess.GetResourceIds(process) : [];

        /// <summary>Gets the stream.</summary>
        public Stream Stream => _process is null ? Stream.Null : _output;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => "video/mp2t";

        /// <summary>Waits for the first output bytes without publishing them or losing the transport-stream prefix.</summary>
        public async Task PrimeAsync(CancellationToken cancellationToken) => await _output.PrimeAsync(cancellationToken);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var process = Interlocked.Exchange(ref _process, null);
            if (process is null)
            {
                return;
            }

            List<Exception>? failures = null;
            try
            {
                await _lifetime.CancelAsync();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await MediaProcessAdmission.WaitForReleaseAsync(process);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            await ObserveCleanupAsync(_pumpTask, failures ??= []);
            await ObserveCleanupAsync(_errorTask, failures);
            try
            {
                process.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            try
            {
                _lifetime.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }

        private static async Task ObserveCleanupAsync(Task task, List<Exception> failures)
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
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        private sealed class ValidatedOutputStream(Stream output, Process process, Task<string> errorTask) : Stream
        {
            private readonly object _gate = new();
            private Task? _validation;
            private ReadOnlyMemory<byte> _prefix;

            /// <summary>Retains the first media bytes for the eventual stream consumer.</summary>
            public async Task PrimeAsync(CancellationToken cancellationToken)
            {
                var prefix = new byte[188];
                var count = await ReadAsync(prefix, cancellationToken);
                if (count == 0)
                {
                    throw new HardwareEncodingException("The hardware encoder exited without producing any media. Check the source, encoder, and driver diagnostics.");
                }
                _prefix = prefix.AsMemory(0, count);
            }

            /// <inheritdoc />
            public override bool CanRead => output.CanRead;
            /// <inheritdoc />
            public override bool CanSeek => false;
            /// <inheritdoc />
            public override bool CanWrite => false;
            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();
            /// <inheritdoc />
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_prefix.IsEmpty && count > 0)
                {
                    return ReadPrefix(buffer.AsMemory(offset, count));
                }
                var read = output.Read(buffer, offset, count);
                if (read == 0)
                {
                    EnsureSuccessAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                return read;
            }

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_prefix.IsEmpty && !buffer.IsEmpty)
                {
                    return ReadPrefix(buffer);
                }
                var read = await output.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    await EnsureSuccessAsync(cancellationToken);
                }
                return read;
            }

            private int ReadPrefix(Memory<byte> buffer)
            {
                var count = Math.Min(buffer.Length, _prefix.Length);
                _prefix[..count].CopyTo(buffer);
                _prefix = _prefix[count..];
                return count;
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
                    throw new MediaProcessException(process.ExitCode, error);
                }
            }

            /// <inheritdoc />
            public override void Flush() => throw new NotSupportedException();
            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();
            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

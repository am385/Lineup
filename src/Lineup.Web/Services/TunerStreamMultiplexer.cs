using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Lineup.Web.Services;

/// <summary>
/// Shares one tuner input connection among all consumers of the same source URI.
/// </summary>
public interface ITunerStreamMultiplexer
{
    /// <summary>
    /// Subscribes to a shared tuner input.
    /// </summary>
    /// <param name="sourceUri">The HDHomeRun stream URI.</param>
    /// <param name="cancellationToken">Stops the subscription while it is being created.</param>
    /// <returns>A stream that receives a copy of the shared tuner input.</returns>
    ValueTask<Stream> SubscribeAsync(Uri sourceUri, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects new subscriptions, cancels active tuner sources, and waits for them to stop.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for source cleanup.</param>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exposes the optional shared progressive source used by playback consumers.
/// </summary>
public interface IEffectiveTunerStreamMultiplexer
{
    /// <summary>
    /// Subscribes to the effective raw or shared-normalized source for one playback consumer.
    /// </summary>
    ValueTask<Stream> SubscribeEffectiveAsync(Uri sourceUri, MediaProbeResult source, AppSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Returns metadata describing the effective source delivered to playback consumers.
    /// </summary>
    MediaProbeResult GetEffectiveSource(MediaProbeResult source, AppSettings settings);
}

/// <summary>
/// Fans raw MPEG-TS input out through bounded per-consumer buffers.
/// </summary>
public sealed class TunerStreamMultiplexer : ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer
{
    private const int BufferSize = 64 * 1024;
    private const int SubscriberBufferCount = 512;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TunerStreamMultiplexer> _logger;
    private readonly ConcurrentDictionary<string, SharedTunerSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<NormalizedSourceKey, SharedNormalizedSource> _normalizedSources = [];
    private readonly Lock _lifecycleGate = new();
    private bool _isStopping;

    /// <summary>
    /// Initializes the tuner stream multiplexer.
    /// </summary>
    /// <param name="httpClientFactory">Creates the streaming HTTP client used for tuner input.</param>
    /// <param name="logger">Logs shared source lifecycle and subscriber counts.</param>
    public TunerStreamMultiplexer(IHttpClientFactory httpClientFactory, ILogger<TunerStreamMultiplexer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<Stream> SubscribeAsync(Uri sourceUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = sourceUri.AbsoluteUri;
        while (true)
        {
            lock (_lifecycleGate)
            {
                if (_isStopping)
                {
                    throw new ObjectDisposedException(nameof(TunerStreamMultiplexer), "The tuner stream multiplexer is shutting down.");
                }

                var source = _sources.GetOrAdd(
                    key,
                    _ => new SharedTunerSource(
                        sourceUri,
                        _httpClientFactory,
                        _logger,
                        completedSource => _sources.TryRemove(new KeyValuePair<string, SharedTunerSource>(key, completedSource))));
                if (source.TrySubscribe(out var subscription))
                {
                    return ValueTask.FromResult(subscription);
                }

                _sources.TryRemove(new KeyValuePair<string, SharedTunerSource>(key, source));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> SubscribeEffectiveAsync(Uri sourceUri, MediaProbeResult source, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!SourceDeinterlacePlanner.ShouldNormalize(source, settings.SourceDeinterlaceMode))
        {
            return SubscribeAsync(sourceUri, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizedSourceKey.Create(sourceUri, settings);
        while (true)
        {
            lock (_lifecycleGate)
            {
                if (_isStopping)
                {
                    throw new ObjectDisposedException(nameof(TunerStreamMultiplexer), "The tuner stream multiplexer is shutting down.");
                }

                var normalized = _normalizedSources.GetOrAdd(
                    key,
                    _ => new SharedNormalizedSource(
                        sourceUri,
                        source,
                        settings,
                        this,
                        _logger,
                        completedSource => _normalizedSources.TryRemove(new KeyValuePair<NormalizedSourceKey, SharedNormalizedSource>(key, completedSource))));
                if (normalized.TrySubscribe(out var subscription))
                {
                    return ValueTask.FromResult(subscription);
                }

                _normalizedSources.TryRemove(new KeyValuePair<NormalizedSourceKey, SharedNormalizedSource>(key, normalized));
            }
        }
    }

    /// <inheritdoc />
    public MediaProbeResult GetEffectiveSource(MediaProbeResult source, AppSettings settings) =>
        SourceDeinterlacePlanner.CreateEffectiveSource(source, settings);

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        SharedTunerSource[] sources;
        SharedNormalizedSource[] normalizedSources;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            sources = _sources.Values.ToArray();
            normalizedSources = _normalizedSources.Values.ToArray();
        }

        foreach (var source in normalizedSources)
        {
            source.Stop();
        }

        foreach (var source in sources)
        {
            source.Stop();
        }

        await Task.WhenAll(normalizedSources.Select(source => source.Completion).Concat(sources.Select(source => source.Completion)))
            .WaitAsync(cancellationToken);
    }

    private sealed record NormalizedSourceKey(string SourceUri, DeinterlaceMode Mode, WebVideoPreset Preset, int Quality, int MaximumBitRateMbps)
    {
        /// <summary>Creates a stable key for one settings snapshot.</summary>
        public static NormalizedSourceKey Create(Uri sourceUri, AppSettings settings) =>
            new(sourceUri.AbsoluteUri, settings.SourceDeinterlaceMode, settings.WebVideoPreset, settings.WebVideoQuality, settings.MaximumVideoBitRateMbps);
    }

    private sealed class SharedNormalizedSource
    {
        private readonly Uri _sourceUri;
        private readonly IReadOnlyList<string> _arguments;
        private readonly ITunerStreamMultiplexer _rawSources;
        private readonly ILogger _logger;
        private readonly Action<SharedNormalizedSource> _onCompleted;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Channel<byte[]>> _subscribers = [];
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _runTask;
        private bool _completed;

        /// <summary>Gets the source processing task.</summary>
        public Task Completion => _runTask ?? Task.CompletedTask;

        /// <summary>Initializes one shared normalized source.</summary>
        public SharedNormalizedSource(Uri sourceUri, MediaProbeResult source, AppSettings settings, ITunerStreamMultiplexer rawSources, ILogger logger, Action<SharedNormalizedSource> onCompleted)
        {
            _sourceUri = sourceUri;
            _arguments = SourceDeinterlacePlanner.CreateArguments(source, settings);
            _rawSources = rawSources;
            _logger = logger;
            _onCompleted = onCompleted;
        }

        /// <summary>Attempts to subscribe to the normalized output.</summary>
        public bool TrySubscribe(out Stream subscription)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberBufferCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            lock (_gate)
            {
                if (_completed)
                {
                    subscription = Stream.Null;
                    return false;
                }

                _subscribers.Add(id, channel);
                _runTask ??= RunAsync();
                _logger.LogInformation("Normalized tuner source {SourceUri} now has {SubscriberCount} subscriber(s)", _sourceUri, _subscribers.Count);
            }

            subscription = new SubscriptionStream(channel.Reader, () => RemoveSubscriber(id));
            return true;
        }

        /// <summary>Stops the normalized source and completes every subscriber.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete();
                }
                _subscribers.Clear();
                _lifetime.Cancel();
            }
        }

        private async Task RunAsync()
        {
            Exception? failure = null;
            Process? process = null;
            Task? inputTask = null;
            Task<string>? errorTask = null;
            try
            {
                await using var input = await _rawSources.SubscribeAsync(_sourceUri, _lifetime.Token);
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in _arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                process = Process.Start(startInfo) ?? throw new MpegTsTranscodeException("Failed to start shared source deinterlacing.");
                _logger.LogInformation("Starting shared H.264 deinterlacing for tuner source {SourceUri}", _sourceUri);
                inputTask = TunerInputPump.PumpAsync(input, _sourceUri, process, _logger, _lifetime.Token);
                errorTask = process.StandardError.ReadToEndAsync(_lifetime.Token);
                while (!_lifetime.IsCancellationRequested)
                {
                    var buffer = new byte[BufferSize];
                    var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, _lifetime.Token);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    if (bytesRead != buffer.Length)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }
                    Broadcast(buffer);
                }

                await process.WaitForExitAsync(_lifetime.Token);
                var errors = await errorTask;
                if (process.ExitCode != 0)
                {
                    throw new MpegTsTranscodeException($"Shared source FFmpeg exited with code {process.ExitCode}: {errors.Trim()}");
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.LogWarning(ex, "Normalized tuner source {SourceUri} stopped unexpectedly", _sourceUri);
            }
            finally
            {
                if (process is not null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    process.Dispose();
                }
                await ObserveTaskAsync(inputTask);
                await ObserveTaskAsync(errorTask);
                CompleteSubscribers(failure);
                _onCompleted(this);
                _lifetime.Dispose();
            }
        }

        private void Broadcast(byte[] buffer)
        {
            List<Guid>? slowSubscribers = null;
            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    if (!subscriber.Value.Writer.TryWrite(buffer))
                    {
                        slowSubscribers ??= [];
                        slowSubscribers.Add(subscriber.Key);
                    }
                }
                if (slowSubscribers is null)
                {
                    return;
                }
                foreach (var id in slowSubscribers)
                {
                    if (_subscribers.Remove(id, out var channel))
                    {
                        _logger.LogWarning(
                            "Disconnecting a slow subscriber from normalized tuner source {SourceUri} after its {BufferSizeMb} MB buffer filled",
                            _sourceUri,
                            SubscriberBufferCount * BufferSize / 1024 / 1024);
                        channel.Writer.TryComplete(new IOException("The client could not keep up with the normalized tuner stream."));
                    }
                }
                if (_subscribers.Count == 0 && !_completed)
                {
                    _completed = true;
                    _lifetime.Cancel();
                }
            }
        }

        private void RemoveSubscriber(Guid id)
        {
            lock (_gate)
            {
                if (_subscribers.Remove(id, out var channel))
                {
                    channel.Writer.TryComplete();
                }
                _logger.LogInformation("Normalized tuner source {SourceUri} now has {SubscriberCount} subscriber(s)", _sourceUri, _subscribers.Count);
                if (_subscribers.Count == 0 && !_completed)
                {
                    _completed = true;
                    _lifetime.Cancel();
                }
            }
        }

        private void CompleteSubscribers(Exception? failure)
        {
            lock (_gate)
            {
                _completed = true;
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete(failure);
                }
                _subscribers.Clear();
            }
        }

        private static async Task ObserveTaskAsync(Task? task)
        {
            if (task is null || task.IsCompletedSuccessfully)
            {
                return;
            }
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
    }

    private sealed class SharedTunerSource
    {
        private readonly Uri _sourceUri;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly Action<SharedTunerSource> _onCompleted;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Channel<byte[]>> _subscribers = [];
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _runTask;
        private bool _completed;

        /// <summary>
        /// Gets the source processing task.
        /// </summary>
        public Task Completion => _runTask ?? Task.CompletedTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedTunerSource"/> class.
        /// </summary>
        public SharedTunerSource(Uri sourceUri, IHttpClientFactory httpClientFactory, ILogger logger, Action<SharedTunerSource> onCompleted)
        {
            _sourceUri = sourceUri;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _onCompleted = onCompleted;
        }

        /// <summary>
        /// Performs the try subscribe operation.
        /// </summary>
        public bool TrySubscribe(out Stream subscription)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberBufferCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            lock (_gate)
            {
                if (_completed)
                {
                    subscription = Stream.Null;
                    return false;
                }

                _subscribers.Add(id, channel);
                _runTask ??= RunAsync();
                _logger.LogInformation("Tuner source {SourceUri} now has {SubscriberCount} subscriber(s)", _sourceUri, _subscribers.Count);
            }

            subscription = new SubscriptionStream(channel.Reader, () => RemoveSubscriber(id));
            return true;
        }

        /// <summary>
        /// Stops the source and completes every subscriber.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete();
                }

                _subscribers.Clear();
                _lifetime.Cancel();
            }
        }

        private async Task RunAsync()
        {
            Exception? failure = null;
            try
            {
                var httpClient = _httpClientFactory.CreateClient("StreamProxy");
                using var response = await httpClient.GetAsync(_sourceUri, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var tunerError = response.Headers.TryGetValues("X-HDHomeRun-Error", out var values)
                        ? values.FirstOrDefault()
                        : null;
                    throw new IOException(tunerError ?? $"The tuner returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                await using var input = await response.Content.ReadAsStreamAsync(_lifetime.Token);
                while (!_lifetime.IsCancellationRequested)
                {
                    var buffer = new byte[BufferSize];
                    var bytesRead = await input.ReadAsync(buffer, _lifetime.Token);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (bytesRead != buffer.Length)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }

                    Broadcast(buffer);
                }
            }

            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.LogWarning(ex, "Shared tuner source {SourceUri} stopped unexpectedly", _sourceUri);
            }
            finally
            {
                CompleteSubscribers(failure);
                _onCompleted(this);
                _lifetime.Dispose();
            }
        }

        private void Broadcast(byte[] buffer)
        {
            List<Guid>? slowSubscribers = null;
            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    if (!subscriber.Value.Writer.TryWrite(buffer))
                    {
                        slowSubscribers ??= [];
                        slowSubscribers.Add(subscriber.Key);
                    }
                }

                if (slowSubscribers is not null)
                {
                    foreach (var id in slowSubscribers)
                    {
                        if (_subscribers.Remove(id, out var channel))
                        {
                            _logger.LogWarning("Disconnecting a slow subscriber from tuner source {SourceUri} after its {BufferSizeMb} MB buffer filled", _sourceUri, SubscriberBufferCount * BufferSize / 1024 / 1024);
                            channel.Writer.TryComplete(new IOException("The client could not keep up with the shared tuner stream."));
                        }
                    }

                    if (_subscribers.Count == 0 && !_completed)
                    {
                        _completed = true;
                        _lifetime.Cancel();
                    }
                }
            }
        }

        private void RemoveSubscriber(Guid id)
        {
            lock (_gate)
            {
                if (_subscribers.Remove(id, out var channel))
                {
                    channel.Writer.TryComplete();
                }

                _logger.LogInformation("Tuner source {SourceUri} now has {SubscriberCount} subscriber(s)", _sourceUri, _subscribers.Count);
                if (_subscribers.Count == 0 && !_completed)
                {
                    _completed = true;
                    _lifetime.Cancel();
                }
            }
        }

        private void CompleteSubscribers(Exception? failure)
        {
            lock (_gate)
            {
                _completed = true;
                foreach (var channel in _subscribers.Values)
                {
                    channel.Writer.TryComplete(failure);
                }

                _subscribers.Clear();
            }
        }
    }

    private sealed class SubscriptionStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private readonly Action _unsubscribe;
        private byte[]? _currentBuffer;
        private int _currentOffset;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionStream"/> class.
        /// </summary>
        public SubscriptionStream(ChannelReader<byte[]> reader, Action unsubscribe)
        {
            _reader = reader;
            _unsubscribe = unsubscribe;
        }

        /// <summary>
        /// Gets can read.
        /// </summary>
        public override bool CanRead => !_disposed;
        /// <summary>
        /// Gets can seek.
        /// </summary>
        public override bool CanSeek => false;
        /// <summary>
        /// Gets can write.
        /// </summary>
        public override bool CanWrite => false;
        /// <summary>
        /// Gets length.
        /// </summary>
        public override long Length => throw new NotSupportedException();
        /// <summary>
        /// Gets or sets position.
        /// </summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Performs the read operation.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Performs the read operation.
        /// </summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_currentBuffer is null || _currentOffset >= _currentBuffer.Length)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                if (!_reader.TryRead(out _currentBuffer))
                {
                    continue;
                }

                _currentOffset = 0;
            }

            var bytesToCopy = Math.Min(buffer.Length, _currentBuffer.Length - _currentOffset);
            _currentBuffer.AsMemory(_currentOffset, bytesToCopy).CopyTo(buffer);
            _currentOffset += bytesToCopy;
            return bytesToCopy;
        }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _unsubscribe();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Performs the flush operation.
        /// </summary>
        public override void Flush() => throw new NotSupportedException();
        /// <summary>
        /// Performs the seek operation.
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>
        /// Performs the set length operation.
        /// </summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>
        /// Performs the write operation.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

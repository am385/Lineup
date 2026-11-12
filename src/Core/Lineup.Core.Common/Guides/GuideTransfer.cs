using System.Text.Json;

namespace Lineup.Core.Common.Guides;

/// <summary>Defines the shared management, worker, and remote-ingestion guide envelope.</summary>
public static class GuideTransfer
{
    /// <summary>Gets the maximum UTF-8 JSON response size, including any IPC envelope.</summary>
    public const int MaximumBytes = 128 * 1024 * 1024;

    /// <summary>Gets the maximum exported or remotely ingested channel count.</summary>
    public const int MaximumChannels = 2_000;

    /// <summary>Gets the maximum exported or remotely ingested program count.</summary>
    public const int MaximumPrograms = 100_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Rejects counts outside the transfer envelope without truncating the guide.</summary>
    public static void ValidateCounts(int channels, long programs)
    {
        if (channels > MaximumChannels || programs > MaximumPrograms)
        {
            throw new GuideTransferLimitException($"Guide transfer exceeds {MaximumChannels:N0} channels or {MaximumPrograms:N0} programs. Reduce the exported channel selection or guide coverage.");
        }
    }

    /// <summary>Validates an immutable projection before mapping or serialization allocates another representation.</summary>
    public static void Validate(VirtualGuideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCounts(snapshot.Channels.Count, snapshot.Channels.Sum(channel => (long)channel.Programs.Count));
    }

    /// <summary>Measures bounded JSON without materializing a complete UTF-8 buffer or JSON document.</summary>
    public static async Task<int> MeasureAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        using var destination = new SizeSink();
        await JsonSerializer.SerializeAsync(destination, value, SerializerOptions, cancellationToken);
        return checked((int)destination.Length);
    }

    /// <summary>Reads bounded JSON while checking collection counts before handing each input block to deserialization.</summary>
    public static async Task<T> ReadAsync<T>(
        Stream source,
        CancellationToken cancellationToken = default,
        int? frameLength = null,
        int maximumBytes = MaximumBytes,
        bool enforceCollectionLimits = true
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumBytes);
        if (frameLength is < 1 || frameLength > maximumBytes)
        {
            throw new GuideTransferLimitException($"Guide response length must be between 1 and {maximumBytes} bytes.");
        }
        using var input = new ReadStream(source, frameLength, maximumBytes, enforceCollectionLimits);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(input, SerializerOptions, cancellationToken)
                ?? throw new InvalidDataException("The guide response was empty.");
        }
        catch (JsonException exception)
        {
            throw new JsonException("The guide response was not valid JSON.", exception);
        }
    }

    private sealed class SizeSink : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Add(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(buffer.Length);
            return ValueTask.CompletedTask;
        }
        private void Add(int count)
        {
            if (_length + count > MaximumBytes)
            {
                throw new GuideTransferLimitException($"Guide transfer exceeds the {MaximumBytes} byte UTF-8 limit. Reduce the exported channel selection, coverage, or metadata size.");
            }
            _length += count;
        }
    }

    private sealed class ReadStream(Stream source, int? frameLength, int maximumBytes, bool enforceCollectionLimits) : Stream
    {
        private readonly CollectionCounter? _counter = enforceCollectionLimits ? new() : null;
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) { return 0; }
            var destination = buffer.AsSpan(offset, Limit(count));
            var read = destination.Length == 0 ? 0 : source.Read(destination);
            Accept(destination[..read], CancellationToken.None);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) { return 0; }
            buffer = buffer[..Limit(buffer.Length)];
            var read = buffer.Length == 0 ? 0 : await source.ReadAsync(buffer, cancellationToken);
            Accept(buffer.Span[..read], cancellationToken);
            return read;
        }
        private int Limit(int requested) => Math.Min(requested, frameLength.HasValue ? frameLength.Value - _read : maximumBytes - _read + 1);
        private void Accept(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
        {
            _read += bytes.Length;
            if (_read > maximumBytes)
            {
                throw new GuideTransferLimitException($"Guide response exceeds the {maximumBytes} byte UTF-8 limit.");
            }
            if (bytes.Length == 0 && frameLength.HasValue && _read != frameLength.Value)
            {
                throw new EndOfStreamException("The IPC stream ended before the complete guide response was received.");
            }
            _counter?.Accept(bytes, bytes.Length == 0, cancellationToken);
        }
    }

    private sealed class CollectionCounter
    {
        private byte[] _pending = new byte[16 * 1024];
        private int _pendingLength;
        private bool _started;
        private JsonReaderState _state;
        private readonly Stack<(int Depth, bool Programs)> _arrays = new();
        private bool? _nextCollection;
        private int _channels;
        private int _programs;

        internal void Accept(ReadOnlySpan<byte> bytes, bool final, CancellationToken cancellationToken)
        {
            if (_pending.Length - _pendingLength < bytes.Length)
            {
                Array.Resize(ref _pending, Math.Max(_pendingLength + bytes.Length, _pending.Length * 2));
            }
            bytes.CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += bytes.Length;
            if (!_started)
            {
                if (!final && _pendingLength < 3)
                {
                    return;
                }
                _started = true;
                if (_pending.AsSpan(0, _pendingLength).StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                {
                    _pendingLength -= 3;
                    _pending.AsSpan(3, _pendingLength).CopyTo(_pending);
                }
            }
            var reader = new Utf8JsonReader(_pending.AsSpan(0, _pendingLength), final, _state);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_arrays.TryPeek(out var array) && reader.CurrentDepth == array.Depth + 1 &&
                    reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName))
                {
                    if (array.Programs) { _programs++; } else { _channels++; }
                    ValidateCounts(_channels, _programs);
                }
                if (reader.TokenType == JsonTokenType.EndArray && _arrays.TryPeek(out array) && reader.CurrentDepth == array.Depth)
                {
                    _arrays.Pop();
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.ValueSpan.Length == 8 || reader.ValueIsEscaped ? reader.GetString() : null;
                    _nextCollection = string.Equals(name, "channels", StringComparison.OrdinalIgnoreCase) ? false
                        : string.Equals(name, "programs", StringComparison.OrdinalIgnoreCase) ? true : null;
                }
                else
                {
                    if (reader.TokenType == JsonTokenType.StartArray && _nextCollection is { } programs)
                    {
                        _arrays.Push((reader.CurrentDepth, programs));
                    }
                    _nextCollection = null;
                }
            }
            var consumed = checked((int)reader.BytesConsumed);
            _state = reader.CurrentState;
            _pendingLength -= consumed;
            _pending.AsSpan(consumed, _pendingLength).CopyTo(_pending);
        }
    }
}

/// <summary>Reports a guide rejected as a whole because it exceeds the supported transfer envelope.</summary>
public sealed class GuideTransferLimitException(string message) : IOException(message);

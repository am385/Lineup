using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Restores AC-4 registration for explicitly mapped copied tracks without changing media payloads or owning the input stream.</summary>
public sealed class MpegTsAc4SignalingStream : Stream
{
    private const int PacketSize = 188;
    private const int MaximumPendingPackets = 4096;
    private readonly Stream _input;
    private readonly HashSet<int> _expectedPacketIds;
    private readonly Queue<PendingPacket> _pending = new();
    private readonly Dictionary<int, PsiAssembler> _programMaps = [];
    private readonly Dictionary<int, int> _programNumbers = [];
    private readonly Dictionary<int, HashSet<int>> _mappedTracks = [];
    private readonly Dictionary<int, int> _continuity = [];
    private readonly Dictionary<(int Pid, bool Current), byte[]> _previousMaps = [];
    private readonly Dictionary<int, byte[]> _patSections = [];
    private readonly PsiAssembler _pat;
    private readonly byte[] _inputBuffer = new byte[PacketSize * 64];
    private byte[] _packet = new byte[PacketSize];
    private int _packetLength;
    private int _inputOffset;
    private int _inputLength;
    private int _patIdentity = -1;
    private int _patLastSection = -1;
    private ReadOnlyMemory<byte> _output;
    private ExceptionDispatchInfo? _failure;
    private bool _ended;
    private bool _disposed;

    /// <summary>Initializes a bounded signaling repair for known AC-4 elementary PIDs assigned by the output plan.</summary>
    public MpegTsAc4SignalingStream(Stream input, IReadOnlyList<int> packetIds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(packetIds);
        if (packetIds.Count == 0 || packetIds.Any(pid => pid is < 0x20 or >= 0x1fff) || packetIds.Distinct().Count() != packetIds.Count)
        {
            throw new ArgumentException("Copied AC-4 requires distinct elementary packet IDs.", nameof(packetIds));
        }
        _input = input;
        _expectedPacketIds = [.. packetIds];
        _pat = new PsiAssembler(0, ReadAssociation);
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _failure?.Throw();
        if (buffer.IsEmpty)
        {
            return 0;
        }
        try
        {
            var written = 0;
            do
            {
                if (_output.IsEmpty)
                {
                    if (written > 0 && !CanPublish())
                    {
                        break;
                    }
                    await PrepareOutputAsync(cancellationToken);
                    if (_output.IsEmpty)
                    {
                        break;
                    }
                }
                var count = Math.Min(buffer.Length - written, _output.Length);
                _output[..count].CopyTo(buffer[written..]);
                _output = _output[count..];
                written += count;
            } while (written < buffer.Length);
            return written;
        }
        catch (InvalidDataException exception)
        {
            _failure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    private bool CanPublish() =>
        _pending.TryPeek(out var packet) && !packet.Blocked && _expectedPacketIds.All(pid => _mappedTracks.Values.Any(tracks => tracks.Contains(pid)));

    private async ValueTask PrepareOutputAsync(CancellationToken cancellationToken)
    {
        while (_output.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CanPublish())
            {
                var packet = _pending.Dequeue();
                var bytes = packet.Replacement ?? packet.Bytes;
                for (var offset = 0; offset < bytes.Length; offset += PacketSize)
                {
                    RewriteContinuity(bytes.AsSpan(offset, PacketSize));
                }
                _output = bytes;
                continue;
            }
            if (_ended)
            {
                return;
            }
            if (_pending.Count >= MaximumPendingPackets)
            {
                throw Invalid("PAT/PMT acquisition or section assembly exceeded the bounded packet buffer.");
            }
            var next = await ReadPacketAsync(cancellationToken);
            if (next is null)
            {
                if (_pat.IsIncomplete || _programMaps.Values.Any(assembler => assembler.IsIncomplete))
                {
                    throw Invalid("The stream ended in an incomplete PAT or PMT section.");
                }
                if (!_expectedPacketIds.All(pid => _mappedTracks.Values.Any(tracks => tracks.Contains(pid))))
                {
                    throw Invalid("The stream ended before all planned AC-4 tracks were declared in a current PMT.");
                }
                _ended = true;
                continue;
            }
            var pending = new PendingPacket(next);
            _pending.Enqueue(pending);
            var payloadOffset = GetPayloadOffset(next);
            var pid = GetPacketId(next);
            if (pid == 0)
            {
                _pat.Push(pending, payloadOffset);
            }
            else if (_programMaps.TryGetValue(pid, out var assembler))
            {
                assembler.Push(pending, payloadOffset);
            }
        }
    }

    private async ValueTask<byte[]?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        while (_packetLength < PacketSize)
        {
            if (_inputOffset == _inputLength)
            {
                _inputLength = await _input.ReadAsync(_inputBuffer, cancellationToken);
                _inputOffset = 0;
                if (_inputLength == 0)
                {
                    return _packetLength == 0 ? null : throw Invalid("The stream ended in a truncated transport packet.");
                }
            }
            var count = Math.Min(PacketSize - _packetLength, _inputLength - _inputOffset);
            _inputBuffer.AsSpan(_inputOffset, count).CopyTo(_packet.AsSpan(_packetLength));
            _packetLength += count;
            _inputOffset += count;
        }
        var result = _packet;
        _packet = new byte[PacketSize];
        _packetLength = 0;
        return result;
    }

    private byte[] ReadAssociation(byte[] section)
    {
        if (section[0] != 0 || section.Length < 12 || (section.Length - 12) % 4 != 0)
        {
            throw Invalid("The program association table is malformed.");
        }
        if ((section[5] & 1) == 0)
        {
            return section;
        }
        var identity = BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(3)) << 5 | (section[5] >> 1 & 31);
        if (_patIdentity != identity)
        {
            _patSections.Clear();
            _patIdentity = identity;
            _patLastSection = section[7];
        }
        if (section[6] > section[7] || section[7] != _patLastSection)
        {
            throw Invalid("The PAT section numbering is inconsistent.");
        }
        _patSections[section[6]] = section;
        var complete = _patSections.Count == _patLastSection + 1;
        var programs = new Dictionary<int, int>();
        foreach (var part in _patSections.Values)
        {
            for (var offset = 8; offset < part.Length - 4; offset += 4)
            {
                var program = BinaryPrimitives.ReadUInt16BigEndian(part.AsSpan(offset));
                if (program == 0)
                {
                    continue;
                }
                var pid = BinaryPrimitives.ReadUInt16BigEndian(part.AsSpan(offset + 2)) & 0x1fff;
                if (pid is < 0x20 or >= 0x1fff || _expectedPacketIds.Contains(pid) || !programs.TryAdd(pid, program) || programs.Count > 256)
                {
                    throw Invalid("The PAT contains conflicting, reserved, or excessive program-map PIDs.");
                }
            }
        }
        foreach (var removed in _programMaps.Keys.Where(pid => programs.TryGetValue(pid, out var program) ? program != _programNumbers[pid] : complete).ToArray())
        {
            if (_programMaps[removed].IsIncomplete)
            {
                throw Invalid("The PAT replaced an incomplete program map.");
            }
            _programMaps.Remove(removed);
            _programNumbers.Remove(removed);
            _mappedTracks.Remove(removed);
            _continuity.Remove(removed);
            _previousMaps.Remove((removed, true));
            _previousMaps.Remove((removed, false));
        }
        foreach (var (pid, program) in programs)
        {
            if (_programMaps.ContainsKey(pid))
            {
                continue;
            }
            if (_pending.Any(packet => GetPacketId(packet.Bytes) == pid))
            {
                throw Invalid("A program map preceded its program association.");
            }
            if (_programMaps.Count >= 256)
            {
                throw Invalid("PAT updates exceeded the bounded active program-map inventory.");
            }
            _programNumbers.Add(pid, program);
            _programMaps.Add(pid, new PsiAssembler(pid, bytes => RepairMap(pid, bytes)));
        }
        return section;
    }

    private byte[] RepairMap(int pid, byte[] section)
    {
        if (section[0] != 2 || section.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(3)) != _programNumbers[pid] || section[6] != 0 || section[7] != 0)
        {
            throw Invalid("A PMT has an invalid program identity, size, or section number.");
        }
        var end = section.Length - 4;
        var offset = 12 + (BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(10)) & 0xfff);
        if (offset > end)
        {
            throw Invalid("PMT program descriptors exceed the section.");
        }
        ValidateDescriptors(section.AsSpan(12, offset - 12), false);
        var result = new List<byte>(section.Length + 6);
        result.AddRange(section.AsSpan(0, offset));
        var found = new HashSet<int>();
        var elementaryPids = new HashSet<int>();
        var changed = false;
        while (offset < end)
        {
            if (offset + 5 > end)
            {
                throw Invalid("A PMT elementary stream header is truncated.");
            }
            var elementaryPid = BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(offset + 1)) & 0x1fff;
            var length = BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(offset + 3)) & 0xfff;
            if (offset + 5 + length > end || !elementaryPids.Add(elementaryPid))
            {
                throw Invalid("PMT elementary descriptors are truncated or a PID is duplicated.");
            }
            var target = _expectedPacketIds.Contains(elementaryPid);
            var registered = ValidateDescriptors(section.AsSpan(offset + 5, length), target);
            if (target && section[offset] != 0x06)
            {
                throw Invalid($"Planned AC-4 PID {elementaryPid} has unexpected stream type {section[offset]}.");
            }
            result.AddRange(section.AsSpan(offset, 5));
            if (target)
            {
                found.Add(elementaryPid);
                if (!registered)
                {
                    result[^2] = (byte)((result[^2] & 0xf0) | (length + 6) >> 8);
                    result[^1] = (byte)(length + 6);
                    result.AddRange([0x05, 0x04, 0x41, 0x43, 0x2d, 0x34]);
                    changed = true;
                }
            }
            result.AddRange(section.AsSpan(offset + 5, length));
            offset += 5 + length;
        }
        var current = (section[5] & 1) != 0;
        if (current)
        {
            _mappedTracks[pid] = found;
        }
        if (!changed && !_previousMaps.ContainsKey((pid, current)))
        {
            return section;
        }
        var sectionLength = result.Count + 1;
        if (sectionLength > 1021)
        {
            throw Invalid("AC-4 registration exceeds the maximum PMT section length.");
        }
        result[1] = (byte)((result[1] & 0xf0) | sectionLength >> 8);
        result[2] = (byte)sectionLength;
        var body = result.ToArray();
        var version = (section[5] >> 1 & 31) + (changed ? 1 : 0);
        if (_previousMaps.TryGetValue((pid, current), out var previous))
        {
            // Once rewritten, track output content so an upstream version cannot collide with our corrected table.
            version = previous[5] >> 1 & 31;
            if (!SameMap(previous, body))
            {
                version++;
            }
        }
        body[5] = (byte)((body[5] & 0xc1) | (version & 31) << 1);
        var repaired = new byte[body.Length + 4];
        body.CopyTo(repaired, 0);
        BinaryPrimitives.WriteUInt32BigEndian(repaired.AsSpan(body.Length), Crc(body));
        _previousMaps[(pid, current)] = body;
        return repaired;
    }

    private static bool SameMap(byte[] previous, byte[] current) =>
        previous.Length == current.Length && previous.AsSpan(0, 5).SequenceEqual(current.AsSpan(0, 5)) &&
        (previous[5] & 0xc1) == (current[5] & 0xc1) && previous.AsSpan(6).SequenceEqual(current.AsSpan(6));

    private static bool ValidateDescriptors(ReadOnlySpan<byte> descriptors, bool ac4)
    {
        var registered = false;
        for (var offset = 0; offset < descriptors.Length;)
        {
            if (offset + 2 > descriptors.Length || offset + 2 + descriptors[offset + 1] > descriptors.Length)
            {
                throw Invalid("A PSI descriptor is truncated.");
            }
            var length = descriptors[offset + 1];
            if (ac4 && descriptors[offset] == 5)
            {
                if (length < 4 || !descriptors.Slice(offset + 2, 4).SequenceEqual("AC-4"u8))
                {
                    throw Invalid("A planned AC-4 PID has a conflicting registration descriptor.");
                }
                registered = true;
            }
            offset += 2 + length;
        }
        return registered;
    }

    private void RewriteContinuity(Span<byte> packet)
    {
        var pid = GetPacketId(packet);
        if (!_programMaps.ContainsKey(pid))
        {
            return;
        }
        var payload = (packet[3] & 0x10) != 0;
        var next = _continuity.GetValueOrDefault(pid, (packet[3] & 15) + (payload ? 0 : 1));
        packet[3] = (byte)((packet[3] & 0xf0) | (payload ? next : next + 15) & 15);
        _continuity[pid] = (next + (payload ? 1 : 0)) & 15;
    }

    private static int GetPacketId(ReadOnlySpan<byte> packet) => (packet[1] & 31) << 8 | packet[2];

    private static int GetPayloadOffset(byte[] packet)
    {
        var control = packet[3] >> 4 & 3;
        if (packet[0] != 0x47 || control == 0)
        {
            throw Invalid("The MPEG-TS packet header is invalid.");
        }
        var offset = (control & 2) != 0 ? 5 + packet[4] : 4;
        if (offset > PacketSize || (control & 1) != 0 && offset >= PacketSize)
        {
            throw Invalid("A transport adaptation field exceeds its packet.");
        }
        return (control & 1) == 0 ? PacketSize : offset;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
            }
        }
        return crc;
    }

    private static InvalidDataException Invalid(string message) => new($"Unable to preserve copied AC-4 MPEG-TS signaling: {message}");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private sealed class PendingPacket(byte[] bytes)
    {
        /// <summary>Gets the original transport packet.</summary>
        public byte[] Bytes { get; } = bytes;
        /// <summary>Gets or sets the replacement table packets, when necessary.</summary>
        public byte[]? Replacement { get; set; }
        /// <summary>Gets or sets whether section assembly prevents publication.</summary>
        public bool Blocked { get; set; }
    }

    private sealed class PsiAssembler(int pid, Func<byte[], byte[]> processSection)
    {
        private readonly byte[] _section = new byte[1024];
        private readonly List<PendingPacket> _packets = [];
        private readonly List<byte[]> _sections = [];
        private int _length;
        private int _expectedLength;
        private int? _lastContinuity;
        private byte[]? _lastPacket;
        private bool _changed;

        /// <summary>Gets whether the current section is incomplete.</summary>
        public bool IsIncomplete => _length != 0;

        /// <summary>Assembles sections and releases their original or rewritten table packets.</summary>
        public void Push(PendingPacket packet, int payloadOffset)
        {
            var bytes = packet.Bytes;
            if ((bytes[1] & 0x80) != 0 || (bytes[3] & 0xc0) != 0)
            {
                throw Invalid("A PAT/PMT packet is corrupt or scrambled.");
            }
            if ((bytes[3] & 0x20) != 0 && bytes[4] > 0 && (bytes[5] & 0x80) != 0)
            {
                if (IsIncomplete)
                {
                    throw Invalid("A discontinuity interrupted a PAT/PMT section.");
                }
                _lastContinuity = null;
            }
            if (payloadOffset == PacketSize)
            {
                return;
            }
            var continuity = bytes[3] & 15;
            if (_lastContinuity == continuity && _lastPacket is not null && bytes.AsSpan().SequenceEqual(_lastPacket))
            {
                if (pid != 0)
                {
                    packet.Replacement = [];
                }
                return;
            }
            if (_lastContinuity.HasValue && continuity != (_lastContinuity.Value + 1) % 16)
            {
                throw Invalid("A PAT/PMT payload continuity counter is inconsistent.");
            }
            _lastContinuity = continuity;
            _lastPacket = bytes.ToArray();
            _packets.Add(packet);
            packet.Blocked = true;
            var payload = bytes.AsSpan(payloadOffset);
            var start = (bytes[1] & 0x40) != 0;
            if (start)
            {
                var pointer = payload[0];
                if (pointer >= payload.Length)
                {
                    throw Invalid("A PSI pointer exceeds its payload.");
                }
                Consume(payload.Slice(1, pointer), false);
                if (IsIncomplete)
                {
                    throw Invalid("A PSI pointer did not finish the preceding section.");
                }
                payload = payload[(pointer + 1)..];
            }
            Consume(payload, start);
            if (!IsIncomplete)
            {
                CompleteBatch();
            }
        }

        private void Consume(ReadOnlySpan<byte> payload, bool allowStart)
        {
            while (!payload.IsEmpty)
            {
                if (_length == 0)
                {
                    if (payload[0] == 0xff)
                    {
                        if (payload.ContainsAnyExcept((byte)0xff))
                        {
                            throw Invalid("Non-stuffing data follows PSI stuffing.");
                        }
                        return;
                    }
                    if (!allowStart)
                    {
                        throw Invalid("A PSI section begins without a payload-unit-start pointer.");
                    }
                }
                var needed = _length < 3 ? 3 - _length : _expectedLength - _length;
                var count = Math.Min(needed, payload.Length);
                payload[..count].CopyTo(_section.AsSpan(_length));
                _length += count;
                payload = payload[count..];
                if (_length == 3)
                {
                    _expectedLength = 3 + ((_section[1] & 15) << 8 | _section[2]);
                    if ((_section[1] & 0x80) == 0 || _expectedLength is < 12 or > 1024)
                    {
                        throw Invalid("A PAT/PMT section length or syntax is invalid.");
                    }
                }
                if (_length == _expectedLength)
                {
                    var original = _section.AsSpan(0, _length).ToArray();
                    if (Crc(original) != 0)
                    {
                        throw Invalid("A PAT/PMT section has an invalid MPEG-2 CRC.");
                    }
                    var repaired = processSection(original);
                    _changed |= !original.AsSpan().SequenceEqual(repaired);
                    _sections.Add(repaired);
                    _length = 0;
                    _expectedLength = 0;
                }
            }
        }

        private void CompleteBatch()
        {
            if (_changed)
            {
                var replacement = new List<byte>();
                foreach (var section in _sections)
                {
                    for (var offset = 0; offset < section.Length;)
                    {
                        var packet = new byte[PacketSize];
                        packet.AsSpan().Fill(0xff);
                        packet[0] = 0x47;
                        packet[1] = (byte)((pid >> 8) | (offset == 0 ? 0x40 : 0));
                        packet[2] = (byte)pid;
                        packet[3] = 0x10;
                        var payload = offset == 0 ? 5 : 4;
                        if (offset == 0)
                        {
                            packet[4] = 0;
                        }
                        var count = Math.Min(PacketSize - payload, section.Length - offset);
                        section.AsSpan(offset, count).CopyTo(packet.AsSpan(payload));
                        offset += count;
                        replacement.AddRange(packet);
                    }
                }
                for (var index = 0; index < _packets.Count; index++)
                {
                    var packet = _packets[index];
                    var adaptation = PreserveAdaptation(packet.Bytes);
                    packet.Replacement = index == 0 ? [.. adaptation, .. replacement] : adaptation;
                }
            }
            foreach (var packet in _packets)
            {
                packet.Blocked = false;
            }
            _packets.Clear();
            _sections.Clear();
            _changed = false;
        }

        private static byte[] PreserveAdaptation(byte[] original)
        {
            if ((original[3] & 0x20) == 0)
            {
                return [];
            }
            var packet = new byte[PacketSize];
            packet.AsSpan().Fill(0xff);
            original.AsSpan(0, 5 + original[4]).CopyTo(packet);
            packet[1] &= 0xbf;
            packet[3] = (byte)((packet[3] & 0xcf) | 0x20);
            packet[4] = 183;
            if (original[4] == 0)
            {
                packet[5] = 0;
            }
            return packet;
        }
    }
}

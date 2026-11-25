using System.Buffers.Binary;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies bounded AC-4 signaling repair with synthetic transport tables and byte-exact media preservation.</summary>
public sealed class MpegTsAc4SignalingStreamTests
{
    private const int MapPid = 0x1234;
    private const int AudioPid = 0x352;
    private static readonly byte[] Registration = [5, 4, 0x41, 0x43, 0x2d, 0x34];
    private static readonly byte[] Language = [0x0a, 4, 0x65, 0x6e, 0x67, 0];

    /// <summary>Verifies input and consumer read boundaries do not affect PID-specific repair or other transport bytes.</summary>
    [Theory]
    [InlineData(1, 3)]
    [InlineData(187, 189)]
    [InlineData(4096, 4096)]
    public async Task Repair_SplitReads_PreservesOtherStreams(int inputChunk, int outputChunk)
    {
        // Arrange
        var map = Map([.. Language], extraPrivate: true);
        var media = MediaPacket(AudioPid);
        var unrelated = MediaPacket(AudioPid + 1);
        var original = Join(Table(0, Association()), Table(MapPid, map), media, unrelated);
        await using var input = new ChunkedInput(original, inputChunk);
        await using var repair = new MpegTsAc4SignalingStream(input, [AudioPid]);
        await using var output = new MemoryStream();

        // Act
        await repair.CopyToAsync(output, outputChunk, TestContext.Current.CancellationToken);

        // Assert
        var packets = Packets(output.ToArray());
        Assert.Equal(Join(Table(0, Association()), media, unrelated), Join(packets.Where(packet => Pid(packet) != MapPid).ToArray()));
        var repaired = Assert.Single(Sections(packets, MapPid));
        Assert.Equal(0u, Checksum(repaired));
        Assert.Equal(0x06, repaired[12]);
        Assert.Equal(Join(Registration, Language), repaired.AsSpan(17, 12).ToArray());
        Assert.Equal(map.AsSpan(23, map.Length - 27).ToArray(), repaired.AsSpan(29, repaired.Length - 33).ToArray());
    }

    /// <summary>Verifies descriptor growth adds table packets and maintains continuity across repeated tables, including version wrap.</summary>
    [Fact]
    public async Task Repair_FullPacket_RepacketizesRepeatedMaps()
    {
        // Arrange
        var map = Map([.. Language, .. Padding(154)], version: 31);
        Assert.Equal(183, map.Length);
        var original = Join(Table(0, Association()), Table(MapPid, map), MediaPacket(AudioPid), Table(MapPid, map, continuity: 1));

        // Act
        var bytes = await RepairAsync(original);

        // Assert
        var packets = Packets(bytes);
        var maps = Sections(packets, MapPid);
        Assert.Equal(2, maps.Count);
        Assert.Equal(maps[0], maps[1]);
        Assert.All(maps, section => Assert.Equal(0u, Checksum(section)));
        Assert.All(maps, section => Assert.Equal(0, section[5] >> 1 & 31));
        Assert.Equal([0, 1, 2, 3], packets.Where(packet => Pid(packet) == MapPid).Select(packet => packet[3] & 15));
        Assert.Equal(MediaPacket(AudioPid), Assert.Single(packets, packet => Pid(packet) == AudioPid));
    }

    /// <summary>Verifies a fragmented PMT can cross unrelated media while its adaptation/PCR bytes and media ordering remain intact.</summary>
    [Fact]
    public async Task Repair_FragmentedMap_PreservesAdaptationAndInterleavedMedia()
    {
        // Arrange
        var fragments = Packets(Table(MapPid, Map([.. Language, .. Padding(240)]), firstBytes: 80));
        var firstMedia = MediaPacket(AudioPid);
        var secondMedia = MediaPacket(AudioPid + 1);
        fragments[0][5] = 0x10;
        byte[] pcr = [0x12, 0x34, 0x56, 0x78, 0x7e, 0x9a];
        pcr.CopyTo(fragments[0], 6);
        var original = Join([Table(0, Association()), fragments[0], firstMedia, .. fragments.Skip(1), secondMedia]);

        // Act
        var bytes = await RepairAsync(original);

        // Assert
        var packets = Packets(bytes);
        Assert.Equal(Join(firstMedia, secondMedia), Join(packets.Where(packet => Pid(packet) != MapPid && Pid(packet) != 0).ToArray()));
        var adaptation = Assert.Single(packets, packet => Pid(packet) == MapPid && (packet[3] & 0x30) == 0x20);
        Assert.Equal(pcr, adaptation.AsSpan(6, 6).ToArray());
        Assert.Equal(0x10, adaptation[5]);
        Assert.Equal(0u, Checksum(Assert.Single(Sections(packets, MapPid))));
    }

    /// <summary>Verifies a pointer can finish one section and begin another within the same transport packet.</summary>
    [Fact]
    public async Task Repair_PointerFinishesSection_PreservesBothMaps()
    {
        // Arrange
        var first = Map([.. Language, .. Padding(171)]);
        Assert.Equal(200, first.Length);
        var second = Map(Language, version: 1);
        var initial = Table(MapPid, first).AsSpan(0, 188).ToArray();
        var next = Enumerable.Repeat((byte)0xff, 188).ToArray();
        next[0] = 0x47;
        next[1] = (byte)(0x40 | MapPid >> 8);
        next[2] = (byte)(MapPid & 255);
        next[3] = 0x11;
        next[4] = 17;
        first.AsSpan(183).CopyTo(next.AsSpan(5));
        second.CopyTo(next, 22);

        // Act
        var bytes = await RepairAsync(Join(Table(0, Association()), initial, MediaPacket(AudioPid), next));

        // Assert
        var maps = Sections(Packets(bytes), MapPid);
        Assert.Equal(2, maps.Count);
        Assert.All(maps, section => Assert.Equal(0u, Checksum(section)));
        Assert.Equal(1, maps[0][5] >> 1 & 31);
        Assert.Equal(2, maps[1][5] >> 1 & 31);
    }

    /// <summary>Verifies already-correct signaling remains byte-identical and does not duplicate registration descriptors.</summary>
    [Fact]
    public async Task Repair_AlreadyRegistered_IsByteExact()
    {
        // Arrange
        var original = Join(Table(0, Association()), Table(MapPid, Map([.. Registration, .. Language])), MediaPacket(AudioPid));

        // Act
        var repaired = await RepairAsync(original);

        // Assert
        Assert.Equal(original, repaired);
    }

    /// <summary>Verifies multiple sections in one payload and unrelated programs retain their own signaling.</summary>
    [Fact]
    public async Task Repair_MultipleProgramsAndSections_ChangesOnlyPlannedRegistration()
    {
        // Arrange
        var future = Map(Language);
        future[5] &= 0xfe;
        WriteChecksum(future);
        var current = Map(Language);
        var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
        Table(MapPid, future).AsSpan(0, 5).CopyTo(packet);
        future.CopyTo(packet, 5);
        current.CopyTo(packet, 5 + future.Length);
        var unrelated = Table(0x1235, Map([], program: 2, audioPid: AudioPid + 7));
        var association = Association(secondProgram: true);

        // Act
        var repaired = await RepairAsync(Join(Table(0, association), unrelated, packet, MediaPacket(AudioPid)));

        // Assert
        var packets = Packets(repaired);
        Assert.Equal(unrelated, Join(packets.Where(bytes => Pid(bytes) == 0x1235).ToArray()));
        var maps = Sections(packets, MapPid);
        Assert.Equal(2, maps.Count);
        Assert.Equal(0, maps[0][5] & 1);
        Assert.Equal(1, maps[1][5] & 1);
        Assert.All(maps, section => Assert.Equal(Registration, section.AsSpan(17, 6).ToArray()));
    }

    /// <summary>Verifies a program map may follow its own PAT section before the complete multi-section PAT arrives.</summary>
    [Fact]
    public async Task Repair_PartialPat_AcceptsAlreadyDeclaredProgram()
    {
        // Arrange
        var first = Association();
        first[7] = 1;
        WriteChecksum(first);
        var second = Association();
        second[6] = 1;
        second[7] = 1;
        second[9] = 2;
        second[11] = 0x35;
        WriteChecksum(second);
        var unrelated = Table(0x1235, Map([], program: 2, audioPid: AudioPid + 1));
        var bytes = Join(Table(0, first), Table(MapPid, Map(Language)), MediaPacket(AudioPid), Table(0, second, continuity: 1), unrelated);

        // Act
        var repaired = await RepairAsync(bytes);

        // Assert
        var packets = Packets(repaired);
        Assert.Equal(Join(Table(0, first), Table(0, second, continuity: 1)), Join(packets.Where(packet => Pid(packet) == 0).ToArray()));
        Assert.Equal(unrelated, Join(packets.Where(packet => Pid(packet) == 0x1235).ToArray()));
        Assert.Equal(Registration, Assert.Single(Sections(packets, MapPid)).AsSpan(17, 6).ToArray());
    }

    /// <summary>Verifies every explicitly copied AC-4 track receives its own registration.</summary>
    [Fact]
    public async Task Repair_MultipleCopiedTracks_RegistersBoth()
    {
        // Arrange
        var bytes = Join(Table(0, Association()), Table(MapPid, Map(Language, extraPrivate: true)), MediaPacket(AudioPid), MediaPacket(AudioPid + 1));

        // Act
        var repaired = await RepairAsync(bytes, [AudioPid, AudioPid + 1]);

        // Assert
        var section = Assert.Single(Sections(Packets(repaired), MapPid));
        Assert.Equal(Registration, section.AsSpan(17, 6).ToArray());
        Assert.Equal(Registration, section.AsSpan(34, 6).ToArray());
        Assert.Equal(0u, Checksum(section));
    }

    /// <summary>Verifies later natively registered tables cannot reuse a previously repaired version for changed content.</summary>
    [Fact]
    public async Task Repair_UpstreamSignalingChanges_MaintainsOutputVersions()
    {
        // Arrange
        byte[] spanish = [0x0a, 4, 0x73, 0x70, 0x61, 0];
        var bytes = Join(Table(0, Association()), Table(MapPid, Map(Language)),
            Table(MapPid, Map([.. Registration, .. spanish], version: 1), continuity: 1),
            Table(MapPid, Map([.. Registration, .. spanish], version: 2), continuity: 2));

        // Act
        var repaired = await RepairAsync(bytes);

        // Assert
        var maps = Sections(Packets(repaired), MapPid);
        Assert.Equal([1, 2, 2], maps.Select(section => section[5] >> 1 & 31));
        Assert.Equal(maps[1], maps[2]);
        Assert.All(maps, section => Assert.Equal(0u, Checksum(section)));
    }

    /// <summary>Verifies invalid or incomplete signaling fails explicitly rather than publishing an apparently usable stream.</summary>
    [Theory]
    [InlineData("crc")]
    [InlineData("wrong-type")]
    [InlineData("conflicting-registration")]
    [InlineData("missing-track")]
    [InlineData("truncated-section")]
    [InlineData("truncated-packet")]
    [InlineData("continuity")]
    [InlineData("pointer")]
    [InlineData("oversized")]
    [InlineData("expanded-limit")]
    public async Task Repair_InvalidSignaling_FailsBeforePublication(string fault)
    {
        // Arrange
        var map = Map(Language);
        byte[] input;
        switch (fault)
        {
            case "crc":
                map[^1] ^= 1;
                input = Join(Table(0, Association()), Table(MapPid, map));
                break;
            case "wrong-type":
                map[12] = 0x1b;
                WriteChecksum(map);
                input = Join(Table(0, Association()), Table(MapPid, map));
                break;
            case "conflicting-registration":
                input = Join(Table(0, Association()), Table(MapPid, Map([5, 4, 0x41, 0x43, 0x2d, 0x33])));
                break;
            case "missing-track":
                input = Join(Table(0, Association()), Table(MapPid, Map([], audioPid: AudioPid + 1)));
                break;
            case "truncated-section":
                input = Join(Table(0, Association()), Table(MapPid, Map(Padding(240))).AsSpan(0, 188).ToArray());
                break;
            case "truncated-packet":
                input = Table(0, Association()).AsSpan(0, 187).ToArray();
                break;
            case "continuity":
                var fragments = Table(MapPid, Map(Padding(240)));
                fragments[188 + 3] = 0x13;
                input = Join(Table(0, Association()), fragments);
                break;
            case "pointer":
                input = Join(Table(0, Association()), Table(MapPid, map));
                input[188 + 4] = 184;
                break;
            case "expanded-limit":
                input = Join(Table(0, Association()), Table(MapPid, Map([.. Padding(255), .. Padding(255), .. Padding(255), .. Padding(226)])));
                break;
            default:
                input = Join(Table(0, Association()), Table(MapPid, map));
                input[188 + 6] = 0xb3;
                input[188 + 7] = 0xfe;
                break;
        }
        await using var source = new MemoryStream(input);
        await using var repair = new MpegTsAc4SignalingStream(source, [AudioPid]);
        await using var output = new MemoryStream();

        // Act
        var exception = await Record.ExceptionAsync(() => repair.CopyToAsync(output, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
        Assert.Equal(0, output.Length);
    }

    /// <summary>Verifies missing signaling cannot retain an unbounded live transport prefix.</summary>
    [Fact]
    public async Task Repair_MissingMap_EnforcesBufferBound()
    {
        // Arrange
        await using var input = new MemoryStream(Join(Enumerable.Repeat(MediaPacket(0x1fff), 4097).ToArray()));
        await using var repair = new MpegTsAc4SignalingStream(input, [AudioPid]);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => repair.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("bounded packet buffer", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies canceling a pending map read preserves pending packets, and disposal never takes ownership of the input.</summary>
    [Fact]
    public async Task Read_Cancellation_PreservesPendingStateAndInputOwnership()
    {
        // Arrange
        var bytes = Join(Table(0, Association()), Table(MapPid, Map(Language)), MediaPacket(AudioPid));
        await using var input = new PausedInput(bytes);
        await using var repair = new MpegTsAc4SignalingStream(input, [AudioPid]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        var pending = repair.ReadAsync(buffer, cancellation.Token).AsTask();

        try
        {
            // Act
            await input.Waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            var exception = await Record.ExceptionAsync(() => pending);
            input.Release.TrySetResult();
            await using var output = new MemoryStream();
            await repair.CopyToAsync(output, TestContext.Current.CancellationToken);
            await repair.DisposeAsync();

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.True(input.CanRead);
            Assert.Equal(Table(0, Association()), Assert.Single(Packets(output.ToArray()), packet => Pid(packet) == 0));
            Assert.Equal(0u, Checksum(Assert.Single(Sections(Packets(output.ToArray()), MapPid))));
        }
        finally
        {
            input.Release.TrySetResult();
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => pending);
        }
    }

    /// <summary>Verifies zero-length reads do not consume input and synchronous consumers receive the same repaired transport.</summary>
    [Fact]
    public void Read_ZeroLengthAndSynchronous_PreservesRepair()
    {
        // Arrange
        var bytes = Join(Table(0, Association()), Table(MapPid, Map(Language)), MediaPacket(AudioPid));
        using var input = new MemoryStream(bytes);
        using var repair = new MpegTsAc4SignalingStream(input, [AudioPid]);
        using var output = new MemoryStream();

        // Act
        var empty = repair.Read([], 0, 0);
        var position = input.Position;
        repair.CopyTo(output, 7);

        // Assert
        Assert.Equal(0, empty);
        Assert.Equal(0, position);
        Assert.Equal(0u, Checksum(Assert.Single(Sections(Packets(output.ToArray()), MapPid))));
    }

    /// <summary>Creates synthetic process output or its independently checksummed repaired expectation.</summary>
    internal static byte[] CreateTransport(int audioPid, bool registered = false) =>
        Join(Table(0, Association()), Table(MapPid, Map(registered ? [.. Registration, .. Language] : Language, version: registered ? 1 : 0, audioPid: audioPid)), MediaPacket(audioPid));

    private static async Task<byte[]> RepairAsync(byte[] bytes, IReadOnlyList<int>? packetIds = null)
    {
        await using var input = new MemoryStream(bytes);
        await using var repair = new MpegTsAc4SignalingStream(input, packetIds ?? [AudioPid]);
        await using var output = new MemoryStream();
        await repair.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static byte[] Association(bool secondProgram = false)
    {
        var body = new List<byte> { 0, 0xb0, 0, 0, 1, 0xc1, 0, 0, 0, 1, 0xf2, 0x34 };
        if (secondProgram)
        {
            body.AddRange([0, 2, 0xf2, 0x35]);
        }
        return Section(body);
    }

    private static byte[] Map(byte[] descriptors, int version = 0, bool extraPrivate = false, int program = 1, int audioPid = AudioPid)
    {
        var body = new List<byte> { 2, 0xb0, 0, 0, (byte)program, (byte)(0xc1 | version << 1), 0, 0, (byte)(0xe0 | audioPid >> 8), (byte)(audioPid & 255), 0xf0, 0 };
        body.AddRange([6, (byte)(0xe0 | audioPid >> 8), (byte)(audioPid & 255), (byte)(0xf0 | descriptors.Length >> 8), (byte)descriptors.Length]);
        body.AddRange(descriptors);
        if (extraPrivate)
        {
            body.AddRange([6, 0xe3, 0x53, 0xf0, 3, 0x80, 1, 0x12]);
        }
        return Section(body);
    }

    private static byte[] Section(List<byte> body)
    {
        var length = body.Count + 1;
        body[1] = (byte)(0xb0 | length >> 8);
        body[2] = (byte)length;
        var section = new byte[body.Count + 4];
        body.CopyTo(section, 0);
        WriteChecksum(section);
        return section;
    }

    private static byte[] Padding(int length) => [0x80, (byte)length, .. Enumerable.Repeat((byte)0x55, length)];

    private static byte[] Table(int pid, byte[] section, int continuity = 0, int firstBytes = 183)
    {
        var result = new List<byte>();
        for (var offset = 0; offset < section.Length;)
        {
            var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
            var start = offset == 0;
            packet[0] = 0x47;
            packet[1] = (byte)((start ? 0x40 : 0) | pid >> 8);
            packet[2] = (byte)(pid & 255);
            packet[3] = (byte)(0x10 | (continuity++ & 15));
            var payload = start ? 5 : 4;
            var count = Math.Min(section.Length - offset, start ? firstBytes : 184);
            if (start && firstBytes < 182)
            {
                packet[3] |= 0x20;
                packet[4] = (byte)(182 - firstBytes);
                packet[5] = 0;
                payload = 6 + packet[4];
            }
            if (start)
            {
                packet[payload - 1] = 0;
            }
            section.AsSpan(offset, count).CopyTo(packet.AsSpan(payload));
            result.AddRange(packet);
            offset += count;
        }
        return result.ToArray();
    }

    private static byte[] MediaPacket(int pid)
    {
        var bytes = Enumerable.Range(0, 188).Select(value => (byte)value).ToArray();
        bytes[0] = 0x47;
        bytes[1] = (byte)(0x40 | pid >> 8);
        bytes[2] = (byte)(pid & 255);
        bytes[3] = 0x35;
        bytes[4] = 7;
        bytes[5] = 0x10;
        return bytes;
    }

    private static int Pid(byte[] packet) => (packet[1] & 31) << 8 | packet[2];

    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(bytes => bytes).ToArray();

    private static byte[][] Packets(byte[] bytes) => bytes.Chunk(188).ToArray();

    private static List<byte[]> Sections(byte[][] packets, int pid)
    {
        var result = new List<byte[]>();
        var current = new List<byte>();
        foreach (var packet in packets.Where(packet => Pid(packet) == pid && (packet[3] & 0x10) != 0))
        {
            var offset = (packet[3] & 0x20) == 0 ? 4 : 5 + packet[4];
            if ((packet[1] & 0x40) != 0)
            {
                offset += 1 + packet[offset];
            }
            for (; offset < 188; offset++)
            {
                if (current.Count == 0 && packet[offset] == 0xff)
                {
                    break;
                }
                current.Add(packet[offset]);
                if (current.Count >= 3 && current.Count == 3 + ((current[1] & 15) << 8 | current[2]))
                {
                    result.Add(current.ToArray());
                    current.Clear();
                }
            }
        }
        Assert.Empty(current);
        return result;
    }

    private static void WriteChecksum(byte[] bytes) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), Checksum(bytes.AsSpan(0, bytes.Length - 4)));

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var top = (crc >> 31) ^ ((uint)value >> bit & 1);
                crc <<= 1;
                if (top != 0)
                {
                    crc ^= 0x04c11db7;
                }
            }
        }
        return crc;
    }

    private sealed class ChunkedInput(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    private sealed class PausedInput(byte[] bytes) : MemoryStream(bytes)
    {
        /// <summary>Signals a read blocked after the PAT.</summary>
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Releases the remaining transport packets.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 188)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer[..Math.Min(188, buffer.Length)], cancellationToken);
        }
    }
}

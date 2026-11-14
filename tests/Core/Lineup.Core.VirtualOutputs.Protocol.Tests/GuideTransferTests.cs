using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Protocol.Tests;

/// <summary>Verifies coherent large-guide streaming, exact envelope boundaries, and incremental input limits.</summary>
public sealed class GuideTransferTests
{
    /// <summary>Streaming retains the existing JSON envelope in both old-writer/new-reader and new-writer/old-reader directions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuideFrame_ExistingEnvelope_PreservesCompatibility(bool streamingWriter)
    {
        // Arrange
        await using var stream = new MemoryStream();
        var snapshot = CreateSnapshot(1, 1);
        var payload = new VirtualOutputGuideResponse { Snapshot = snapshot };
        if (streamingWriter)
        {
            await VirtualOutputWireProtocol.WriteGuideAsync(stream, payload, TestContext.Current.CancellationToken);
        }
        else
        {
            await VirtualOutputWireProtocol.WriteAsync(stream, VirtualOutputIpcResponse.Success(payload), TestContext.Current.CancellationToken);
        }
        stream.Position = 0;

        // Act
        var actual = streamingWriter
            ? (await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(stream, TestContext.Current.CancellationToken)).ReadPayload<VirtualOutputGuideResponse>()
            : (await VirtualOutputWireProtocol.ReadGuideAsync(stream, TestContext.Current.CancellationToken)).Payload!;

        // Assert
        Assert.Equal(snapshot.Generation, actual.Snapshot.Generation);
        Assert.Equal(snapshot.Channels[0].Programs[0].StartUtc, actual.Snapshot.Channels[0].Programs[0].StartUtc);
        Assert.Equal(snapshot.Channels[0].Programs[0].SupplementalXml, actual.Snapshot.Channels[0].Programs[0].SupplementalXml);
    }

    /// <summary>Byte preflight accepts exactly 128 MiB but refuses the next byte before writing any response.</summary>
    [Fact]
    public async Task WriteGuideAsync_ByteBoundary_PreflightsExactLimit()
    {
        // Arrange
        var snapshot = CreateSnapshot(1, 0);
        var program = CreateSnapshot(1, 1).Channels[0].Programs[0] with { Synopsis = new string('x', 32 * 1024) };
        var channel = snapshot.Channels[0];
        var empty = await GuideTransfer.MeasureAsync(new VirtualOutputGuideWireResponse { Succeeded = true, Payload = new() { Snapshot = snapshot } }, TestContext.Current.CancellationToken);
        var single = await GuideTransfer.MeasureAsync(new VirtualOutputGuideWireResponse
        {
            Succeeded = true,
            Payload = new() { Snapshot = snapshot with { Channels = [channel with { Programs = [program] }] } }
        }, TestContext.Current.CancellationToken);
        var unit = single - empty + 1;
        var count = (GuideTransfer.MaximumBytes - empty + 1) / unit;
        var programs = Enumerable.Repeat(program, count).ToArray();
        programs[^1] = program with { Synopsis = program.Synopsis + new string('x', GuideTransfer.MaximumBytes - (empty + count * unit - 1)) };
        snapshot = snapshot with { Channels = [channel with { Programs = programs }] };
        await using var output = new MemoryStream();

        // Act
        var exact = await GuideTransfer.MeasureAsync(new VirtualOutputGuideWireResponse { Succeeded = true, Payload = new() { Snapshot = snapshot } }, TestContext.Current.CancellationToken);
        programs[^1] = programs[^1] with { Synopsis = programs[^1].Synopsis + "x" };
        await Assert.ThrowsAsync<GuideTransferLimitException>(() => VirtualOutputWireProtocol.WriteGuideAsync(output, new() { Snapshot = snapshot }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(GuideTransfer.MaximumBytes, exact);
        Assert.Equal(0, output.Length);
    }

    /// <summary>Rich schedules beyond the old frame cap stream into independent readers without changing their observed generation.</summary>
    [Fact]
    public async Task GuideFrame_RichSchedule_StreamsCoherentConcurrentReads()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"lineup-guide-transfer-{Guid.NewGuid():N}.json");
        var snapshot = CreateSnapshot(100, 7 * 48);
        try
        {
            long length;
            await using (var output = File.Create(path))
            {
                await VirtualOutputWireProtocol.WriteGuideAsync(output, new() { Snapshot = snapshot }, TestContext.Current.CancellationToken);
                length = output.Length;
                await output.WriteAsync(new byte[] { 42 }, TestContext.Current.CancellationToken);
            }

            // Act
            var responses = await Task.WhenAll(ReadAsync(), ReadAsync());

            // Assert
            Assert.InRange(length, 32 * 1024 * 1024 + 1, GuideTransfer.MaximumBytes);
            Assert.All(responses, response =>
            {
                Assert.Equal(snapshot.Generation, response.Generation);
                Assert.Equal(100, response.Channels.Count);
                Assert.Equal(33_600, response.Channels.Sum(channel => channel.Programs.Count));
                Assert.Equal(snapshot.Channels[0].Programs[0].Synopsis, response.Channels[0].Programs[0].Synopsis);
                Assert.Equal(snapshot.Channels[0].Programs[0].SupplementalXml, response.Channels[0].Programs[0].SupplementalXml);
            });
        }
        finally
        {
            File.Delete(path);
        }

        async Task<VirtualGuideSnapshot> ReadAsync()
        {
            await using var input = File.OpenRead(path);
            var response = await VirtualOutputWireProtocol.ReadGuideAsync(input, TestContext.Current.CancellationToken);
            Assert.True(response.Succeeded);
            Assert.Equal(42, input.ReadByte());
            return Assert.IsType<VirtualOutputGuideResponse>(response.Payload).Snapshot;
        }
    }

    /// <summary>Oversized collections reject before emitting even a frame header; the same connection can carry an explicit error.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteGuideAsync_OverCollectionLimit_DoesNotBeginResponse(bool channels)
    {
        // Arrange
        var snapshot = CreateSnapshot(channels ? GuideTransfer.MaximumChannels + 1 : 1, channels ? 0 : GuideTransfer.MaximumPrograms + 1);
        await using var output = new MemoryStream();

        // Act
        var error = await Assert.ThrowsAsync<GuideTransferLimitException>(() =>
            VirtualOutputWireProtocol.WriteGuideAsync(output, new() { Snapshot = snapshot }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(0, output.Length);
        Assert.Contains("Reduce", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Collection quotas are cumulative across channel arrays and cannot be bypassed by property casing or duplicate properties.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_Collections_EnforcesExactLimitAcrossFragments(bool programs)
    {
        // Arrange
        var name = programs ? "pRoGrAmS" : "cHaNnElS";
        var limit = programs ? GuideTransfer.MaximumPrograms : GuideTransfer.MaximumChannels;
        var entries = string.Join(",", Enumerable.Repeat("{}", limit));
        var valid = Encoding.UTF8.GetBytes($"{{\"{name}\":[{entries}],\"title\":\"épisode\"}}");
        using var accepted = new FragmentedStream(valid);
        using var rejected = new FragmentedStream(Encoding.UTF8.GetBytes($"{{\"{name}\":[{entries}],\"{name}\":[{{}}]}}"));

        // Act
        var result = await GuideTransfer.ReadAsync<JsonElement>(accepted, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GuideTransferLimitException>(() => GuideTransfer.ReadAsync<JsonElement>(rejected, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(limit, result.GetProperty(name).GetArrayLength());
        Assert.Equal("épisode", result.GetProperty("title").GetString());
    }

    /// <summary>UTF-8 byte limits include trailing whitespace and work without a Content-Length header.</summary>
    [Fact]
    public async Task ReadAsync_ByteLimit_AcceptsExactBoundaryAndRejectsNextByte()
    {
        // Arrange
        var json = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("{\"title\":\"épisode\"}")).ToArray();
        var exact = json.Concat(Enumerable.Repeat((byte)' ', 128 - json.Length)).ToArray();
        using var accepted = new FragmentedStream(exact, 1);
        using var rejected = new FragmentedStream(exact.Concat(new byte[] { (byte)' ' }).ToArray(), 1);

        // Act
        var result = await GuideTransfer.ReadAsync<JsonElement>(accepted, TestContext.Current.CancellationToken, maximumBytes: 128);
        await Assert.ThrowsAsync<GuideTransferLimitException>(() => GuideTransfer.ReadAsync<JsonElement>(rejected, TestContext.Current.CancellationToken, maximumBytes: 128));

        // Assert
        Assert.Equal("épisode", result.GetProperty("title").GetString());
        Assert.Equal(129, rejected.Position);
    }

    /// <summary>Invalid declared sizes are rejected before allocation and truncated valid frames cannot be returned as success.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadGuideAsync_InvalidFrame_RejectsWithoutPartialResult(bool oversized)
    {
        // Arrange
        await using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, oversized ? GuideTransfer.MaximumBytes + 1 : 100);
        stream.Write(header);
        stream.Write("{}"u8);
        stream.Position = 0;

        // Act
        if (oversized)
        {
            await Assert.ThrowsAsync<GuideTransferLimitException>(() => VirtualOutputWireProtocol.ReadGuideAsync(stream, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => VirtualOutputWireProtocol.ReadGuideAsync(stream, TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.Equal(oversized ? 4 : 6, stream.Position);
    }

    /// <summary>Caller cancellation prevents preflight from producing a partial framed response.</summary>
    [Fact]
    public async Task WriteGuideAsync_Canceled_DoesNotBeginResponse()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await using var output = new MemoryStream();
        var snapshot = CreateSnapshot(1, 1);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VirtualOutputWireProtocol.WriteGuideAsync(output, new() { Snapshot = snapshot }, canceled.Token));

        // Assert
        Assert.Equal(0, output.Length);
    }

    private static VirtualGuideSnapshot CreateSnapshot(int channelCount, int programsPerChannel)
    {
        var id = VirtualDeviceId.New();
        var source = GuideSourceId.New();
        var now = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        var synopsis = string.Concat(Enumerable.Repeat("A documentary follows a local team through a difficult season, with interviews, historical context and analysis. ", 8));
        return new()
        {
            VirtualDeviceId = id,
            Generation = new(now, "import-A", "configuration-A"),
            Channels = Enumerable.Range(1, channelCount).Select(channel => new VirtualGuideChannel
            {
                ChannelId = new(id, Guid.NewGuid()),
                Number = channel.ToString(),
                Name = $"Example network {channel}",
                Programs = Enumerable.Range(0, programsPerChannel).Select(index => new GuideProgram
                {
                    ChannelId = new(source, channel.ToString()),
                    StartUtc = now.AddMinutes(index * 30),
                    EndUtc = now.AddMinutes((index + 1) * 30),
                    Title = $"Documentary episode {index}",
                    Synopsis = synopsis,
                    Categories = ["Documentary", "Sport"],
                    SupplementalXml = "<programme><rating system=\"TVPG\"><value>TV-PG</value></rating></programme>"
                }).ToArray()
            }).ToArray()
        };
    }

    private sealed class FragmentedStream(byte[] content, int fragmentSize = 7) : MemoryStream(content)
    {
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Lineup.Tools.PerformanceCollector.Tests;

/// <summary>Checks the privileged request boundary without elevation, WPR, or live hardware.</summary>
public sealed class CollectorProtocolTests
{
    /// <summary>Accepts the exact minimum and maximum observation/root budgets.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(300, 16)]
    public void Validate_AcceptsBudgetBoundaries(int duration, int count)
    {
        // Arrange
        var request = new CollectorRequest
        {
            Action = "capture",
            DurationSeconds = duration,
            Roots = Enumerable.Range(1, count).Select(id => new CollectorRoot(id, 1)).ToArray()
        };

        // Act
        var exception = Record.Exception(request.Validate);

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Rejects arbitrary execution and out-of-budget capture requests.</summary>
    [Theory]
    [InlineData("powershell", 30, "safe", 1)]
    [InlineData("capture", 0, "safe", 1)]
    [InlineData("capture", 301, "safe", 1)]
    [InlineData("capture", 30, "..\\escape", 1)]
    [InlineData("capture", 30, "safe\n", 1)]
    [InlineData("capture", 30, "safe", 0)]
    [InlineData("capture", 30, "safe", 17)]
    public void Validate_RejectsInvalidRequest(string action, int duration, string scenario, int roots)
    {
        // Arrange
        var request = new CollectorRequest
        {
            Action = action,
            DurationSeconds = duration,
            Scenario = scenario,
            Roots = Enumerable.Range(1, roots).Select(id => new CollectorRoot(id, 1)).ToArray()
        };

        // Act
        var exception = Record.Exception(request.Validate);

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Rejects PID reuse ambiguity and provider selections on non-capture requests.</summary>
    [Fact]
    public void Validate_RejectsDuplicateRootsAndNonCaptureProviders()
    {
        // Arrange
        var duplicate = new CollectorRequest { Action = "capture", Roots = [new(1, 1), new(1, 2)] };
        var wrongOperation = new CollectorRequest { Action = "status", Gpu = true };

        // Act
        var duplicateFailure = Record.Exception(duplicate.Validate);
        var operationFailure = Record.Exception(wrongOperation.Validate);

        // Assert
        Assert.IsType<InvalidDataException>(duplicateFailure);
        Assert.IsType<InvalidDataException>(operationFailure);
    }

    /// <summary>Rejects caller-supplied elevated paths, scripts and unknown JSON members.</summary>
    [Theory]
    [InlineData("{\"action\":\"capture\",\"outputDirectory\":\"C:\\\\Windows\\\\anything\"}")]
    [InlineData("{\"action\":\"capture\",\"command\":\"powershell.exe\"}")]
    [InlineData("{\"action\":\"capture\",\"script\":\"anything.ps1\"}")]
    public async Task ReadAsync_RejectsUnknownPrivilegeSurface(string json)
    {
        // Arrange
        await using var stream = Frame(Encoding.UTF8.GetBytes(json));

        // Act
        var exception = await Record.ExceptionAsync(() => CollectorWire.ReadAsync<CollectorRequest>(stream, CancellationToken.None));

        // Assert
        Assert.IsType<JsonException>(exception);
    }

    /// <summary>Refuses excessive allocation before trying to read the claimed payload.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16385)]
    [InlineData(int.MaxValue)]
    public async Task ReadAsync_RejectsInvalidLength(int length)
    {
        // Arrange
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await using var stream = new MemoryStream(header);

        // Act
        var exception = await Record.ExceptionAsync(() => CollectorWire.ReadAsync<CollectorRequest>(stream, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Reports truncated peers as failures rather than empty/default requests.</summary>
    [Fact]
    public async Task ReadAsync_RejectsTruncatedMessage()
    {
        // Arrange
        await using var stream = Frame(Encoding.UTF8.GetBytes("{}"));
        stream.SetLength(stream.Length - 1);

        // Act
        var exception = await Record.ExceptionAsync(() => CollectorWire.ReadAsync<CollectorRequest>(stream, CancellationToken.None));

        // Assert
        Assert.IsType<EndOfStreamException>(exception);
    }

    /// <summary>Preserves exact root birth times and allowed profile choices across framing.</summary>
    [Fact]
    public async Task Wire_RoundTripsAllowedCapture()
    {
        // Arrange
        var request = new CollectorRequest { Action = "capture", DurationSeconds = 120, Gpu = true, DotNet = true, Roots = [new(4321, 123456789)] };
        await using var stream = new MemoryStream();

        // Act
        await CollectorWire.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var actual = await CollectorWire.ReadAsync<CollectorRequest>(stream, CancellationToken.None);
        actual.Validate();

        // Assert
        Assert.Equal(request.DurationSeconds, actual.DurationSeconds);
        Assert.Equal(request.Roots, actual.Roots);
        Assert.True(actual.Gpu);
        Assert.True(actual.DotNet);
    }

    private static MemoryStream Frame(byte[] payload)
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }
}

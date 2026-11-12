using System.Buffers.Binary;
using System.Text.Json;
using Lineup.Core.Common.Guides;

namespace Lineup.Core.VirtualOutputs.Protocol;

/// <summary>Identifies one request on the local virtual-output IPC transport.</summary>
public enum VirtualOutputIpcOperation
{
    /// <summary>Register using a one-time bootstrap credential.</summary>
    Register = 0,

    /// <summary>Renew the authenticated worker lease.</summary>
    RenewLease = 1,

    /// <summary>Read the assigned virtual-device manifest.</summary>
    GetManifest = 2,

    /// <summary>Read the assigned virtual-device lineup.</summary>
    GetLineup = 3,

    /// <summary>Read the assigned virtual-device guide.</summary>
    GetGuide = 4,

    /// <summary>Open one assigned channel stream.</summary>
    OpenStream = 5,

    /// <summary>Report worker readiness and listener health.</summary>
    ReportHealth = 6,

    /// <summary>Close one previously opened channel stream.</summary>
    CloseStream = 7,

    /// <summary>Report the final media presentation produced for one opened stream.</summary>
    ReportStreamStatus = 8,

    /// <summary>Retain one public TCP connection permit until this IPC ownership channel closes.</summary>
    AdmitConnection = 9,

    /// <summary>Retain one deployment-wide media reservation until this ownership channel closes.</summary>
    ReserveMediaWork = 10
}

/// <summary>Requests one producer or processing reservation for the authenticated worker.</summary>
public sealed record VirtualOutputMediaWorkRequest(VirtualOutputSessionContext Session, bool IsProcessing, bool Reclaimable);

/// <summary>Provides the trusted workspace admission watermark without accepting worker-supplied host paths.</summary>
public sealed record VirtualOutputMediaWorkResponse(int MinimumFreeSpaceMib);

/// <summary>Contains one length-prefixed IPC operation and typed JSON payload.</summary>
public sealed record VirtualOutputIpcRequest
{
    /// <summary>Gets the W3C parent context, excluding credentials, addresses and baggage.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Gets the requested operation.</summary>
    public VirtualOutputIpcOperation Operation { get; init; }

    /// <summary>Gets the serialized operation payload.</summary>
    public JsonElement Payload { get; init; }

    /// <summary>Creates one request from a typed payload.</summary>
    public static VirtualOutputIpcRequest Create<T>(VirtualOutputIpcOperation operation, T payload) =>
        new()
        {
            Operation = operation,
            TraceParent = System.Diagnostics.Activity.Current is { IdFormat: System.Diagnostics.ActivityIdFormat.W3C } activity ? activity.Id : null,
            Payload = JsonSerializer.SerializeToElement(payload, VirtualOutputWireProtocol.SerializerOptions)
        };

    /// <summary>Deserializes the typed operation payload.</summary>
    public T ReadPayload<T>() =>
        Payload.Deserialize<T>(VirtualOutputWireProtocol.SerializerOptions)
        ?? throw new InvalidDataException($"The {Operation} request payload is empty.");
}

/// <summary>Contains one length-prefixed IPC response header.</summary>
public sealed record VirtualOutputIpcResponse
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the serialized response payload when successful.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Gets a structured failure code.</summary>
    public VirtualOutputFailureCode? FailureCode { get; init; }

    /// <summary>Gets a safe failure description.</summary>
    public string? Failure { get; init; }

    /// <summary>Creates a successful response from a typed payload.</summary>
    public static VirtualOutputIpcResponse Success<T>(T payload) =>
        new()
        {
            Succeeded = true,
            Payload = JsonSerializer.SerializeToElement(payload, VirtualOutputWireProtocol.SerializerOptions)
        };

    /// <summary>Creates a failed response.</summary>
    public static VirtualOutputIpcResponse Error(VirtualOutputFailureCode failureCode, string failure) =>
        new()
        {
            FailureCode = failureCode,
            Failure = failure
        };

    /// <summary>Deserializes the successful typed payload.</summary>
    public T ReadPayload<T>()
    {
        if (!Succeeded || Payload is not { } payload)
        {
            throw new InvalidOperationException(Failure ?? "The IPC operation failed.");
        }
        return payload.Deserialize<T>(VirtualOutputWireProtocol.SerializerOptions)
            ?? throw new InvalidDataException("The IPC response payload is empty.");
    }
}

/// <summary>Preserves the guide response envelope without an intermediate JSON document.</summary>
public sealed record VirtualOutputGuideWireResponse
{
    /// <summary>Gets whether the guide operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the typed guide payload.</summary>
    public VirtualOutputGuideResponse? Payload { get; init; }

    /// <summary>Gets the structured failure code.</summary>
    public VirtualOutputFailureCode? FailureCode { get; init; }

    /// <summary>Gets the safe failure description.</summary>
    public string? Failure { get; init; }
}

/// <summary>Reads and writes bounded length-prefixed JSON messages over local IPC streams.</summary>
public static class VirtualOutputWireProtocol
{
    internal static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Preflights a coherent guide and streams its existing frame shape without whole-document buffers.</summary>
    public static async Task WriteGuideAsync(Stream stream, VirtualOutputGuideResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        GuideTransfer.Validate(response.Snapshot);
        var envelope = new VirtualOutputGuideWireResponse { Succeeded = true, Payload = response };
        var length = await GuideTransfer.MeasureAsync(envelope, cancellationToken);
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await stream.WriteAsync(header, cancellationToken);
        await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one guide frame directly into typed objects while enforcing byte and collection limits.</summary>
    public static async Task<VirtualOutputGuideWireResponse> ReadGuideAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        return await GuideTransfer.ReadAsync<VirtualOutputGuideWireResponse>(stream, cancellationToken, BinaryPrimitives.ReadInt32LittleEndian(header));
    }

    /// <summary>Writes one bounded message without closing the stream.</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        await WriteAsync(stream, message, VirtualOutputProtocol.MaximumControlMessageBytes, cancellationToken);
    }

    /// <summary>Writes one message using an explicit bounded size limit without closing the stream.</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, int maximumMessageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > maximumMessageBytes)
        {
            throw new InvalidDataException($"IPC control message exceeds {maximumMessageBytes} bytes.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one bounded message without consuming any following stream body.</summary>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        return await ReadAsync<T>(stream, VirtualOutputProtocol.MaximumControlMessageBytes, cancellationToken);
    }

    /// <summary>Reads one message using an explicit bounded size limit without consuming any following stream body.</summary>
    public static async Task<T> ReadAsync<T>(Stream stream, int maximumMessageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > maximumMessageBytes)
        {
            throw new InvalidDataException($"IPC control message length {length} is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new InvalidDataException("IPC control message is empty.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0)
            {
                throw new EndOfStreamException("The IPC stream ended before a complete control message was received.");
            }
            read += current;
        }
    }
}

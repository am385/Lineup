using System.Buffers.Binary;
using System.Text.Json;

namespace Lineup.VirtualOutputs.Protocol;

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
    ReportStreamStatus = 8
}

/// <summary>Contains one length-prefixed IPC operation and typed JSON payload.</summary>
public sealed record VirtualOutputIpcRequest
{
    /// <summary>Gets the requested operation.</summary>
    public VirtualOutputIpcOperation Operation { get; init; }

    /// <summary>Gets the serialized operation payload.</summary>
    public JsonElement Payload { get; init; }

    /// <summary>Creates one request from a typed payload.</summary>
    public static VirtualOutputIpcRequest Create<T>(VirtualOutputIpcOperation operation, T payload) =>
        new()
        {
            Operation = operation,
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

/// <summary>Reads and writes bounded length-prefixed JSON messages over local IPC streams.</summary>
public static class VirtualOutputWireProtocol
{
    internal static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

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

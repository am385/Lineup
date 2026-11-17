using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lineup.Tools.PerformanceCollector;

/// <summary>Identifies a target process without trusting a reusable PID alone.</summary>
internal sealed record CollectorRoot(int ProcessId, long StartedUtcTicks);

/// <summary>Contains the complete, deliberately closed collector request surface.</summary>
internal sealed record CollectorRequest
{
    /// <summary>Gets status, capture, or stop; no executable, script or output path is accepted.</summary>
    public string Action { get; init; } = "status";

    /// <summary>Gets a short, filesystem-safe scenario label.</summary>
    public string Scenario { get; init; } = "profile";

    /// <summary>Gets the bounded observation duration.</summary>
    public int DurationSeconds { get; init; } = 30;

    /// <summary>Gets whether GPU detail is included.</summary>
    public bool Gpu { get; init; }

    /// <summary>Gets whether runtime detail is included.</summary>
    public bool DotNet { get; init; }

    /// <summary>Gets whether disk, file and network detail is included.</summary>
    public bool Io { get; init; }

    /// <summary>Gets explicitly selected target lifetimes.</summary>
    public CollectorRoot[] Roots { get; init; } = [];

    /// <summary>Rejects unknown operations, excessive work and malformed identities before privilege is used.</summary>
    public void Validate()
    {
        if (Action is not ("status" or "capture" or "stop"))
        {
            throw new InvalidDataException("Only status, capture and stop are supported.");
        }
        if (DurationSeconds is < 1 or > 300 || Scenario is null || !Regex.IsMatch(Scenario, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Capture duration must be 1-300 seconds and the scenario must be a short alphanumeric label.");
        }
        if (Roots is null || Roots.Length > 16 || Roots.Any(root => root is null || root.ProcessId <= 0 || root.StartedUtcTicks <= 0) ||
            Roots.Select(root => root.ProcessId).Distinct().Count() != Roots.Length || Action == "capture" && Roots.Length == 0)
        {
            throw new InvalidDataException("Capture requires 1-16 distinct positive process identities with creation times.");
        }
        if (Action != "capture" && (Roots.Length != 0 || Gpu || DotNet || Io))
        {
            throw new InvalidDataException("Only capture requests may select targets or providers.");
        }
    }
}

/// <summary>Describes one generated file, never a caller-selected elevated filesystem destination.</summary>
internal sealed record CollectorArtifact(string Name, long Length, string Sha256);

/// <summary>Publishes the elevated process identity from its protected spool.</summary>
internal sealed record CollectorSession(string SessionId, int ProcessId, long StartedUtcTicks);

/// <summary>Returns bounded operational state and optional exported-artifact metadata.</summary>
internal sealed record CollectorReply(bool Success, string State, string? Message = null, string? RunId = null, CollectorArtifact[]? Artifacts = null);

/// <summary>Acknowledges a verified normal-privilege export before the private source files are removed.</summary>
internal sealed record CollectorAcknowledgment(string RunId);

/// <summary>Frames small, strict JSON messages; trace files are exported by the unprivileged client, not sent as JSON.</summary>
internal static class CollectorWire
{
    /// <summary>Gets strict serialization shared by local peers and session metadata.</summary>
    internal static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Reads one bounded message and fails on truncation or unknown JSON members.</summary>
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 16 * 1024)
        {
            throw new InvalidDataException("Collector message length must be 1-16384 bytes.");
        }
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("A collector message cannot be null.");
    }

    /// <summary>Writes one bounded reply without logging request content.</summary>
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length > 16 * 1024)
        {
            throw new InvalidDataException("Collector reply exceeds its message limit.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

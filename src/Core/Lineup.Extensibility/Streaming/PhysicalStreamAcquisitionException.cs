using Lineup.Common.Streaming;

namespace Lineup.Extensibility.Streaming;

/// <summary>Classifies a provider-neutral physical stream acquisition failure.</summary>
public enum PhysicalStreamFailureCategory
{
    /// <summary>The source is temporarily unavailable.</summary>
    Unavailable,

    /// <summary>The source could not lock a usable signal.</summary>
    NoSignal,

    /// <summary>The source returned no media data.</summary>
    NoData,

    /// <summary>The source has no currently available receiver capacity.</summary>
    Capacity,

    /// <summary>The requested source channel does not exist.</summary>
    NotFound,

    /// <summary>The source rejected invalid input.</summary>
    InvalidInput,

    /// <summary>The source is protected and cannot be streamed under the current policy.</summary>
    ProtectedContent
}

/// <summary>Represents a safe, structured failure reported by one physical streaming adapter.</summary>
public sealed class PhysicalStreamAcquisitionException : IOException
{
    /// <summary>Initializes a provider-neutral physical stream failure.</summary>
    public PhysicalStreamAcquisitionException(
        PhysicalStreamFailureCategory category,
        string code,
        string message,
        bool retryable,
        PhysicalDeviceId deviceId,
        SourceChannelId channelId,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Category = category;
        Code = code;
        Retryable = retryable;
        DeviceId = deviceId;
        ChannelId = channelId;
    }

    /// <summary>Gets the safe failure category.</summary>
    public PhysicalStreamFailureCategory Category { get; }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public string Code { get; }

    /// <summary>Gets whether retrying the same source later may succeed.</summary>
    public bool Retryable { get; }

    /// <summary>Gets the physical device that rejected the acquisition.</summary>
    public PhysicalDeviceId DeviceId { get; }

    /// <summary>Gets the exact source channel that failed.</summary>
    public SourceChannelId ChannelId { get; }
}

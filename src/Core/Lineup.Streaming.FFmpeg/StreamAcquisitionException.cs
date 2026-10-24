using Lineup.Extensibility.Streaming;

namespace Lineup.Streaming.FFmpeg;

/// <summary>Describes one safe candidate failure within a resolved streaming route.</summary>
public sealed record StreamAcquisitionCandidateFailure
{
    /// <summary>Gets the physical device that failed.</summary>
    public required PhysicalDeviceId DeviceId { get; init; }

    /// <summary>Gets the exact source channel that failed.</summary>
    public required SourceChannelId ChannelId { get; init; }

    /// <summary>Gets the provider-neutral failure category.</summary>
    public required PhysicalStreamFailureCategory Category { get; init; }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the safe operator-facing candidate summary.</summary>
    public required string Message { get; init; }

    /// <summary>Gets whether retrying this candidate later may succeed.</summary>
    public required bool Retryable { get; init; }
}

/// <summary>Represents failure to open every equivalent physical source for one resolved route.</summary>
public sealed class StreamAcquisitionException : IOException
{
    private StreamAcquisitionException(
        PhysicalStreamFailureCategory category,
        string code,
        string message,
        bool retryable,
        IReadOnlyList<StreamAcquisitionCandidateFailure> candidateFailures,
        Exception innerException)
        : base(message, innerException)
    {
        Category = category;
        Code = code;
        Retryable = retryable;
        CandidateFailures = candidateFailures;
    }

    /// <summary>Gets the aggregate failure category.</summary>
    public PhysicalStreamFailureCategory Category { get; }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public string Code { get; }

    /// <summary>Gets whether retrying the route later may succeed.</summary>
    public bool Retryable { get; }

    /// <summary>Gets safe per-candidate summaries with physical device and channel identities.</summary>
    public IReadOnlyList<StreamAcquisitionCandidateFailure> CandidateFailures { get; }

    /// <summary>Creates an aggregate failure from safe candidate summaries and their diagnostic exception chain.</summary>
    public static StreamAcquisitionException Create(
        IReadOnlyList<StreamAcquisitionCandidateFailure> candidateFailures,
        IReadOnlyList<Exception> failures)
    {
        if (candidateFailures.Count == 0 || failures.Count == 0)
        {
            throw new ArgumentException("At least one failed physical source is required.", nameof(candidateFailures));
        }

        var category = ResolveCategory(candidateFailures);
        var code = ResolveCode(category, candidateFailures);
        return new StreamAcquisitionException(
            category,
            code,
            CreateMessage(category),
            candidateFailures.Any(failure => failure.Retryable),
            candidateFailures.ToArray(),
            new AggregateException(failures));
    }

    private static PhysicalStreamFailureCategory ResolveCategory(IReadOnlyList<StreamAcquisitionCandidateFailure> failures)
    {
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.Capacity))
        {
            return PhysicalStreamFailureCategory.Capacity;
        }
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.NoSignal))
        {
            return PhysicalStreamFailureCategory.NoSignal;
        }
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.NoData))
        {
            return PhysicalStreamFailureCategory.NoData;
        }
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.NotFound))
        {
            return PhysicalStreamFailureCategory.NotFound;
        }
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.InvalidInput))
        {
            return PhysicalStreamFailureCategory.InvalidInput;
        }
        if (failures.All(failure => failure.Category == PhysicalStreamFailureCategory.ProtectedContent))
        {
            return PhysicalStreamFailureCategory.ProtectedContent;
        }
        return PhysicalStreamFailureCategory.Unavailable;
    }

    private static string ResolveCode(
        PhysicalStreamFailureCategory category,
        IReadOnlyList<StreamAcquisitionCandidateFailure> failures)
    {
        var distinctCodes = failures.Select(failure => failure.Code).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctCodes.Length == 1)
        {
            return distinctCodes[0];
        }
        return category switch
        {
            PhysicalStreamFailureCategory.Capacity => "source_capacity_exceeded",
            PhysicalStreamFailureCategory.NoSignal => "source_no_signal",
            PhysicalStreamFailureCategory.NoData => "source_no_video_data",
            PhysicalStreamFailureCategory.NotFound => "source_not_found",
            PhysicalStreamFailureCategory.InvalidInput => "source_invalid",
            PhysicalStreamFailureCategory.ProtectedContent => "protected_content",
            _ => "source_unavailable"
        };
    }

    private static string CreateMessage(PhysicalStreamFailureCategory category) =>
        category switch
        {
            PhysicalStreamFailureCategory.Capacity => "Every equivalent physical source is currently at tuner capacity.",
            PhysicalStreamFailureCategory.NoSignal => "Every equivalent physical source is currently unable to lock a usable signal.",
            PhysicalStreamFailureCategory.NoData => "Every equivalent physical source is currently returning no video data.",
            PhysicalStreamFailureCategory.NotFound => "The requested channel is no longer available from any equivalent physical source.",
            PhysicalStreamFailureCategory.InvalidInput => "Every equivalent physical source rejected the stream request.",
            PhysicalStreamFailureCategory.ProtectedContent => "The requested channel is protected and cannot be streamed.",
            _ => "Every equivalent physical source is currently unavailable."
        };
}

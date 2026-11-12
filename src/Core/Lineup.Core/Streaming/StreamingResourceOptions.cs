using Microsoft.Extensions.Configuration;

namespace Lineup.Core.Streaming;

/// <summary>Defines startup-scoped host resource budgets, independent of active-viewer and physical-tuner limits.</summary>
public sealed record StreamingResourceOptions
{
    /// <summary>Gets the deployment-wide retained-producer limit; zero is unlimited.</summary>
    public int MaximumProducers { get; init; }

    /// <summary>Gets the deployment-wide FFmpeg/FFprobe process limit; zero is unlimited.</summary>
    public int MaximumProcessingOperations { get; init; }

    /// <summary>Gets the minimum available MiB required before allocating media workspace; zero disables admission checks.</summary>
    public int MinimumMediaFreeSpaceMib { get; init; }

    /// <summary>Gets the maximum number of concurrent pending stream startups.</summary>
    public int MaximumPendingStarts { get; init; } = 8;

    /// <summary>Gets the maximum queued payload retained by one graph subscriber, in MiB.</summary>
    public int SubscriberBufferMib { get; init; } = 8;

    /// <summary>Gets the maximum distinct queued payload retained across the graph, in MiB.</summary>
    public int GraphBufferMib { get; init; } = 128;

    /// <summary>Gets the maximum age of unread graph payload, in seconds.</summary>
    public int MaximumBufferAgeSeconds { get; init; } = 5;

    /// <summary>Gets the complete physical-source acquisition allowance, in seconds.</summary>
    public int AcquisitionTimeoutSeconds { get; init; } = 20;

    /// <summary>Loads and validates host configuration; changes require a host restart.</summary>
    public static StreamingResourceOptions Load(IConfiguration? configuration)
    {
        var defaults = new StreamingResourceOptions();
        int Read(string name, int fallback) => configuration?[$"StreamingResources:{name}"] is { } value
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
        var options = new StreamingResourceOptions
        {
            MaximumProducers = Read(nameof(MaximumProducers), defaults.MaximumProducers),
            MaximumProcessingOperations = Read(nameof(MaximumProcessingOperations), defaults.MaximumProcessingOperations),
            MinimumMediaFreeSpaceMib = Read(nameof(MinimumMediaFreeSpaceMib), defaults.MinimumMediaFreeSpaceMib),
            MaximumPendingStarts = Read(nameof(MaximumPendingStarts), defaults.MaximumPendingStarts),
            SubscriberBufferMib = Read(nameof(SubscriberBufferMib), defaults.SubscriberBufferMib),
            GraphBufferMib = Read(nameof(GraphBufferMib), defaults.GraphBufferMib),
            MaximumBufferAgeSeconds = Read(nameof(MaximumBufferAgeSeconds), defaults.MaximumBufferAgeSeconds),
            AcquisitionTimeoutSeconds = Read(nameof(AcquisitionTimeoutSeconds), defaults.AcquisitionTimeoutSeconds)
        };
        options.Validate();
        return options;
    }

    /// <summary>Rejects invalid budgets rather than silently disabling enforcement.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumProducers);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumProcessingOperations);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumMediaFreeSpaceMib);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPendingStarts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumPendingStarts, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(SubscriberBufferMib, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SubscriberBufferMib, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(GraphBufferMib, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GraphBufferMib, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBufferAgeSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumBufferAgeSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(AcquisitionTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AcquisitionTimeoutSeconds, 300);
    }
}

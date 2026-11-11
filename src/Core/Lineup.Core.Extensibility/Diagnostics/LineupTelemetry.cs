using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lineup.Core.Extensibility.Diagnostics;

/// <summary>Provides portable, vendor-neutral operational instrumentation without address or credential labels.</summary>
public static class LineupTelemetry
{
    /// <summary>Gets the meter and activity-source name used by diagnostic collectors.</summary>
    public const string Name = "Lineup";

    /// <summary>Gets the shared activity source.</summary>
    public static ActivitySource Activities { get; } = new(Name);

    /// <summary>Gets bounded-cardinality application measurements.</summary>
    public static Meter Meter { get; } = new(Name);

    /// <summary>Gets the number of admitted pending startups.</summary>
    public static UpDownCounter<long> PendingStarts { get; } = Meter.CreateUpDownCounter<long>("lineup.streaming.pending", "{startup}");

    /// <summary>Gets the number of running graph producers.</summary>
    public static UpDownCounter<long> Producers { get; } = Meter.CreateUpDownCounter<long>("lineup.streaming.producers", "{producer}");

    /// <summary>Gets the number of warm graph producers.</summary>
    public static UpDownCounter<long> WarmProducers { get; } = Meter.CreateUpDownCounter<long>("lineup.streaming.warm", "{producer}");

    /// <summary>Gets active reusable CMAF producers, separately from their underlying graph stages.</summary>
    public static UpDownCounter<long> CmafProducers { get; } = Meter.CreateUpDownCounter<long>("lineup.cmaf.producers", "{producer}");

    /// <summary>Gets idle CMAF producers retained for reuse.</summary>
    public static UpDownCounter<long> WarmCmafProducers { get; } = Meter.CreateUpDownCounter<long>("lineup.cmaf.warm", "{producer}");

    /// <summary>Gets successful fallback selections, without channel or source labels.</summary>
    public static Counter<long> Fallbacks { get; } = Meter.CreateCounter<long>("lineup.streaming.fallbacks", "{selection}");

    /// <summary>Gets unexpected producer terminations.</summary>
    public static Counter<long> SourceLosses { get; } = Meter.CreateCounter<long>("lineup.streaming.source_losses", "{termination}");

    /// <summary>Gets rejected startup admissions.</summary>
    public static Counter<long> Rejections { get; } = Meter.CreateCounter<long>("lineup.streaming.rejections", "{startup}");

    /// <summary>Gets automatic worker restart attempts, without worker identities as labels.</summary>
    public static Counter<long> WorkerRestarts { get; } = Meter.CreateCounter<long>("lineup.worker.restarts", "{restart}");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("lineup.operation.duration", "s");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("lineup.operations", "{operation}");

    /// <summary>Starts a correlated operation; stage names must be fixed application constants, never external input.</summary>
    public static Operation Begin(string stage, string? parentId = null, CancellationToken cancellationToken = default) => new(stage, parentId, cancellationToken);

    /// <summary>Records a detached lifetime using fixed stage/outcome labels, without retaining an ambient activity.</summary>
    public static void Record(string stage, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "stage", stage }, { "outcome", outcome } };
        Operations.Add(1, tags);
        Duration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Owns one timed operation and its activity, including when no collector is attached.</summary>
    public sealed class Operation : IDisposable
    {
        private readonly string _stage;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private readonly CancellationToken _cancellationToken;
        private string _outcome = "failure";
        private bool _disposed;

        /// <summary>Initializes a timed operation with an optional incoming trace and caller cancellation.</summary>
        internal Operation(string stage, string? parentId, CancellationToken cancellationToken)
        {
            _stage = stage;
            _cancellationToken = cancellationToken;
            _activity = parentId is null
                ? Activities.StartActivity(stage)
                : Activities.StartActivity(stage, ActivityKind.Server, parentId);
            if (_activity is null)
            {
                var activity = new Activity(stage).SetIdFormat(ActivityIdFormat.W3C);
                if (parentId is not null)
                {
                    activity.SetParentId(parentId);
                }
                _activity = activity.Start();
            }
        }

        /// <summary>Marks successful completion.</summary>
        public void Succeed() => _outcome = "success";

        /// <summary>Marks expected caller cancellation.</summary>
        public void Cancel() => _outcome = "canceled";

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_outcome == "failure" && _cancellationToken.IsCancellationRequested)
            {
                _outcome = "canceled";
            }
            Record(_stage, _outcome, Stopwatch.GetElapsedTime(_started));
            _activity?.SetStatus(_outcome == "failure" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            _activity?.Dispose();
        }
    }
}

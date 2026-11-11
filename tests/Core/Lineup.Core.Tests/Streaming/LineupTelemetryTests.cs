using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lineup.Core.Extensibility.Diagnostics;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies correlation lifetimes and bounded metric attributes.</summary>
public sealed class LineupTelemetryTests
{
    /// <summary>Preserves trace identity across the IPC parent boundary and restores the caller's ambient activity.</summary>
    [Fact]
    public void Begin_ExplicitParent_PreservesTraceAndRestoresCaller()
    {
        // Arrange
        using var parent = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        parent.AddBaggage("credential", "secret-sentinel");
        var trace = parent.TraceId;
        string? child;

        // Act
        using (var operation = LineupTelemetry.Begin("broker", parent.Id, TestContext.Current.CancellationToken))
        {
            child = Activity.Current!.TraceId.ToString();
            operation.Succeed();
        }

        // Assert
        Assert.Equal(trace.ToString(), child);
        Assert.Same(parent, Activity.Current);
    }

    /// <summary>Emits stage/outcome only, never trace IDs, addresses, tokens or exception text as metric dimensions.</summary>
    [Fact]
    public void Operation_Measurements_OnlyExposeBoundedStageAndOutcome()
    {
        // Arrange
        var observed = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == LineupTelemetry.Name && instrument.Name == "lineup.operations")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var snapshot = tags.ToArray();
            if (snapshot.Any(tag => tag.Key == "stage" && Equals(tag.Value, "telemetry_test")))
            {
                lock (observed)
                {
                    observed.Add(snapshot);
                }
            }
        });
        listener.Start();

        // Act
        using (var operation = LineupTelemetry.Begin("telemetry_test", cancellationToken: TestContext.Current.CancellationToken))
        {
            operation.Succeed();
        }

        // Assert
        var tags = Assert.Single(observed);
        Assert.Equal(["stage", "outcome"], tags.Select(tag => tag.Key));
        Assert.Equal("success", tags.Single(tag => tag.Key == "outcome").Value);
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Streaming.Sweep;
using Microsoft.Playwright;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Actual HTTP response capture and owned synthetic-flow classification without a browser.</summary>
public sealed class StartupEvidenceTests
{
    /// <summary>Product inference and source survive the HTTP capture path and canonical serialization.</summary>
    [Theory]
    [InlineData("""{"code":"protected_content","inferred":true,"source":"channel-lineup","retryable":false}""", true, "channel-lineup", (int)SweepVerdict.InferredDrm)]
    [InlineData("""{"code":"protected_content","inferred":false,"source":"tuner","retryable":false}""", false, "tuner", (int)SweepVerdict.ConfirmedDrm)]
    [InlineData("""{"code":"protected_content","source":"tuner"}""", null, "tuner", (int)SweepVerdict.UnconfirmedDrm)]
    [InlineData("""{"code":"protected_content"}""", null, null, (int)SweepVerdict.UnconfirmedDrm)]
    public async Task HttpProtectionEvidenceIsNotPromoted(string body, bool? inferred, string? source, int expected)
    {
        // Arrange
        var response = DispatchProxy.Create<IResponse, ResponseFixture>();
        ((ResponseFixture)response).Body = body;
        var now = DateTimeOffset.UnixEpoch;
        // Act
        var outcome = await SweepHarness.CreateStartupOutcomeAsync(response, now, now.AddMilliseconds(25));
        var result = new ChannelAttemptResult("target", "1", "One", 1, false, "not-applicable", 0, "protected", null, outcome, 1, "startup", "protected-content");
        var verdict = SweepPolicy.Classify(result, [new("response", outcome, null, null, null, null, null)]);
        var roundTrip = JsonSerializer.Deserialize<StartupOutcome>(JsonSerializer.Serialize(outcome, SweepStore.Json), SweepStore.Json);
        // Assert
        Assert.Equal((SweepVerdict)expected, verdict);
        Assert.False(outcome.Retryable);
        Assert.Equal(inferred, outcome.Inferred);
        Assert.Equal(source, outcome.Source);
        Assert.Equal(inferred, roundTrip!.Inferred);
        Assert.Equal(source, roundTrip.Source);
        Assert.Equal(inferred is true ? "inferred-protected-content" : inferred is false ? "protected-content" : "unconfirmed-protected-content", outcome.Outcome);
    }

    /// <summary>Known generated flows retain their reason and never count as broadcast success; Issue 6 can still check transport.</summary>
    [Theory]
    [InlineData("Disabled channel slate", (int)SweepVerdict.DisabledSlate)]
    [InlineData("Protected content slate", (int)SweepVerdict.UnconfirmedDrm)]
    [InlineData("Other generated source", (int)SweepVerdict.UnknownSynthetic)]
    [InlineData(null, (int)SweepVerdict.UnknownSynthetic)]
    public void SyntheticFlowIsNotBroadcastPlayback(string? reason, int expected)
    {
        // Arrange
        var status = JsonNode.Parse("""
            {"streamingFlows":{"activePaths":[
              {"sessionId":"unrelated","isSynthetic":false},
              {"sessionId":"owned","isSynthetic":true,"stages":[{"kind":"PhysicalSource","label":null}]}
            ]}}
            """)!;
        status["streamingFlows"]!["activePaths"]![1]!["stages"]![0]!["label"] = reason;
        var run = SweepPolicyTests.CreateRun();
        var transport = new ChannelAttemptResult("target", "1", "One", 1, true, "not-applicable", 30, null, null, null, 1, null, "synthetic-playback");
        var attempt = new SweepAttempt
        {
            Id = "slate",
            Key = run.Selected[0].Key,
            Result = transport,
            FinishedUtc = DateTimeOffset.UnixEpoch,
            Cleanup = new(true, false, true, [], []),
            Ownership = [new("http://localhost", "generation", "owned", "http://localhost/stop/owned", "http://localhost/sessions")]
        };
        // Act
        SweepPolicy.CaptureFlow(attempt, status);
        attempt.Verdict = SweepPolicy.Classify(transport, [], attempt);
        run.Attempts.Add(attempt);
        run.Accepted[attempt.Key] = attempt.Id;
        var markdown = SweepStore.Markdown(run);
        // Assert
        Assert.True(transport.AvPassed);
        Assert.True(attempt.SyntheticPlayback);
        Assert.Equal(reason, attempt.SyntheticReason);
        Assert.Equal((SweepVerdict)expected, attempt.Verdict);
        Assert.Equal(0, run.Aggregates.PlaybackPassed);
        Assert.Contains(reason ?? "unknown reason", markdown);
        Assert.Equal("owned", attempt.ActiveFlow!["sessionId"]!.GetValue<string>());
    }

    /// <summary>Dedicated worker ownership correlates with its broker flow, not an unrelated consumer.</summary>
    [Fact]
    public void DedicatedSyntheticFlowUsesOwnedWorkerCorrelation()
    {
        // Arrange
        var status = JsonNode.Parse("""
            {"streamingFlows":{"activePaths":[
              {"sessionId":"unrelated","isSynthetic":false},
              {"sessionId":"broker","isSynthetic":true,"stages":[{"kind":"PhysicalSource","label":"Disabled channel slate"}]}
            ]}}
            """)!;
        var attempt = new SweepAttempt
        {
            Id = "slate",
            Key = "target|player|1",
            ActiveStatus = JsonNode.Parse("""{"sessionId":"broker","workerMediaSessionId":"worker"}"""),
            Ownership = [new("http://localhost", "generation", "worker", "http://localhost/media/stop/worker", "http://localhost/media/sessions")]
        };
        // Act
        SweepPolicy.CaptureFlow(attempt, status);
        // Assert
        Assert.True(attempt.SyntheticPlayback);
        Assert.Equal("Disabled channel slate", attempt.SyntheticReason);
        Assert.Equal("broker", attempt.ActiveFlow!["sessionId"]!.GetValue<string>());
    }

    /// <summary>Fallback classification uses the latest owned flow instead of an earlier still-draining physical source.</summary>
    [Fact]
    public void CurrentSyntheticFlowOverridesEarlierOwnedSource()
    {
        // Arrange
        var status = JsonNode.Parse("""
            {"streamingFlows":{"activePaths":[
              {"sessionId":"old","isSynthetic":false},
              {"sessionId":"current","isSynthetic":true,"stages":[{"kind":"PhysicalSource","label":"Protected content slate"}]}
            ]}}
            """)!;
        var now = DateTimeOffset.UnixEpoch;
        var startup = new StartupOutcome("success", 200, null, null, null, now, now, 0, "http://localhost/api/stream/cmaf/start/1", true, false);
        var transport = new ChannelAttemptResult("target", "1", "One", 1, true, "not-applicable", 30, null, null, startup, 1, null, "synthetic-playback");
        var attempt = new SweepAttempt
        {
            Id = "slate",
            Key = "target|player|1",
            Starts = [new("first", startup, "old", null, null, null, null), new("fallback", startup, "current", null, null, null, null)],
            Ownership = [
                new("http://localhost", "generation", "old", "http://localhost/stop/old", "http://localhost/sessions"),
                new("http://localhost", "generation", "current", "http://localhost/stop/current", "http://localhost/sessions")]
        };
        // Act
        SweepPolicy.CaptureFlow(attempt, status);
        var verdict = SweepPolicy.Classify(transport, attempt.Starts, attempt);
        // Assert
        Assert.True(attempt.SyntheticPlayback);
        Assert.Equal("current", attempt.ActiveFlow!["sessionId"]!.GetValue<string>());
        Assert.Equal(SweepVerdict.UnconfirmedDrm, verdict);
    }

    /// <summary>Only the HTTP members used by real startup evidence capture are supplied by this deterministic fixture.</summary>
    public class ResponseFixture : DispatchProxy
    {
        /// <summary>Gets or sets the product response body.</summary>
        public string Body { get; set; } = "{}";

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Status" => 422,
            "get_Url" => "http://localhost/api/stream/cmaf/start/1",
            "TextAsync" => Task.FromResult(Body),
            _ => throw new InvalidOperationException($"Unexpected response member: {targetMethod?.Name}")
        };
    }
}

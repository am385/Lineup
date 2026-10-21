using System.Net;
using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Fake HTTP coverage of ownership isolation, stop routing and independent cleanup cancellation.</summary>
public sealed class OwnedPlaybackTests
{
    /// <summary>Legitimate layered warm drain does not imply forced cleanup when viewers are already absent.</summary>
    [Fact]
    public async Task WarmDrainDoesNotTriggerEmergencyIntervention()
    {
        // Arrange
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        var owner = OwnedPlayback.CreateOwner("http://localhost/api/stream/cmaf/start/1", "owned", null, "app");
        var samples = 0;
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync([owner], () => Task.CompletedTask, TimeSpan.FromSeconds(2),
            _ => Task.FromResult(++samples >= 5));
        // Assert
        Assert.True(evidence.VerifiedAbsent);
        Assert.False(evidence.Forced);
        Assert.Equal(5, samples);
    }

    /// <summary>Late ownership is reconsidered while baseline reconciliation is still in progress.</summary>
    [Fact]
    public async Task LateOwnerDuringDrainIsStoppedAndRetained()
    {
        // Arrange
        var owners = new List<OwnedSession>();
        var active = true;
        var baselineSamples = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                active = false;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(active ? "[{\"sessionId\":\"late\"}]" : "[]") };
        }));
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync(owners, () => Task.CompletedTask, TimeSpan.FromSeconds(2), _ =>
        {
            if (++baselineSamples == 1)
            {
                owners.Add(OwnedPlayback.CreateOwner("http://localhost/api/stream/cmaf/start/1", "late", null, "app"));
                return Task.FromResult(false);
            }
            return Task.FromResult(!active);
        });
        // Assert
        Assert.True(evidence.VerifiedAbsent);
        Assert.True(evidence.Forced);
        Assert.Equal("late", Assert.Single(evidence.Sessions).SessionId);
    }

    /// <summary>Ownership discovered while normal teardown is pending is still verified, not lost.</summary>
    [Fact]
    public async Task LateSuccessfulStartIsIncludedInCleanup()
    {
        // Arrange
        var inventories = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            inventories++;
            return new(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var owners = new List<OwnedSession>();
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync(owners, async () =>
        {
            await Task.Yield();
            owners.Add(OwnedPlayback.CreateOwner("http://localhost/api/stream/cmaf/start/1", "late", null, "app"));
        }, TimeSpan.FromSeconds(1));
        // Assert
        Assert.True(evidence.VerifiedAbsent);
        Assert.Equal("late", Assert.Single(evidence.Sessions).SessionId);
        Assert.Equal(1, inventories);
    }

    /// <summary>Cancelled playback cannot cancel the independent cleanup token.</summary>
    [Fact]
    public async Task PlaybackCancellationStillVerifiesAbsence()
    {
        // Arrange
        using var playback = new CancellationTokenSource();
        playback.Cancel();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        var owner = OwnedPlayback.CreateOwner("http://localhost/api/stream/cmaf/start/1", "owned", null, "app");
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync([owner], () => Task.FromCanceled(playback.Token), TimeSpan.FromSeconds(1));
        // Assert
        Assert.True(evidence.VerifiedAbsent);
        Assert.False(evidence.Forced);
        Assert.NotEmpty(evidence.Errors);
    }

    /// <summary>Emergency cleanup honors the actual dedicated stop URL and leaves unrelated sessions alone.</summary>
    [Fact]
    public async Task EmergencyCleanupStopsOnlyOwnedReturnedUrl()
    {
        // Arrange
        var stopped = new List<string>();
        var active = true;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                stopped.Add(request.RequestUri!.AbsoluteUri);
                active = false;
                return new(HttpStatusCode.OK);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"generation\":1,\"sessionIds\":[\"unrelated\"{(active ? ",\"owned\"" : string.Empty)}]}}") };
        }));
        var owner = OwnedPlayback.CreateOwner("http://localhost:8600/media/start/1", "owned", "http://localhost:8600/media/stop/owned", "app|start|1");
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync([owner], () => Task.CompletedTask, TimeSpan.FromSeconds(2));
        // Assert
        Assert.True(evidence.Forced);
        Assert.True(evidence.VerifiedAbsent);
        Assert.Single(stopped);
        Assert.Equal(owner.StopUrl, stopped[0]);
    }

    /// <summary>Normal-stop/diagnostic errors do not skip cleanup verification.</summary>
    [Fact]
    public async Task NormalStopFailureCannotSkipOwnedCleanup()
    {
        // Arrange
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        var owner = OwnedPlayback.CreateOwner("http://localhost/api/stream/cmaf/start/1", "owned", null, "app");
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync([owner], () => throw new IOException("diagnostic failed"), TimeSpan.FromSeconds(1));
        // Assert
        Assert.True(evidence.VerifiedAbsent);
        Assert.False(evidence.Forced);
        Assert.Contains(evidence.Errors, error => error.Contains("diagnostic failed", StringComparison.Ordinal));
    }

    /// <summary>Runtime changes invalidate ownership rather than allowing reused IDs to be stopped.</summary>
    [Fact]
    public async Task ChangedWorkerGenerationForbidsEmergencyStop()
    {
        // Arrange
        var posts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            posts += request.Method == HttpMethod.Post ? 1 : 0;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"generation\":2,\"sessionIds\":[\"owned\"]}") };
        }));
        var owner = OwnedPlayback.CreateOwner("http://localhost/media/start/1", "owned", "/media/stop/owned", "app|start|1");
        // Act
        var evidence = await new OwnedPlayback(http).CleanupAsync([owner], () => Task.CompletedTask, TimeSpan.FromSeconds(1));
        // Assert
        Assert.False(evidence.VerifiedAbsent);
        Assert.Equal(0, posts);
    }

    /// <summary>Returned stop URLs cannot redirect the harness into another origin or session.</summary>
    [Theory]
    [InlineData("http://other/media/stop/owned")]
    [InlineData("/media/stop/unrelated")]
    [InlineData("/media/stop/owned?token=secret")]
    public void RejectsUnsafeStopUrls(string stopUrl)
    {
        // Arrange
        var start = "http://localhost/media/start/1";
        // Act
        var error = Record.Exception(() => OwnedPlayback.CreateOwner(start, "owned", stopUrl, "app|start|1"));
        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

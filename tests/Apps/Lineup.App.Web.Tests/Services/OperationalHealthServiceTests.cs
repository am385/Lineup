using Lineup.App.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies cheap readiness is independent of external sources and storage.</summary>
public sealed class OperationalHealthServiceTests
{
    /// <summary>Exercises the real HTTP endpoints and JSON results without UI settings, storage or external device services.</summary>
    [Theory]
    [InlineData(false, 200, "ready")]
    [InlineData(true, 503, "factoryResetRequired")]
    public async Task HealthEndpoints_ReturnIndependentLivenessAndReadiness(bool reset, int expectedStatus, string expectedState)
    {
        // Arrange
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(provider => new OperationalHealthService(
            new(reset ? LineupStartupStatus.FactoryResetRequired : LineupStartupStatus.Ready),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            () => (0, null)));
        await using var app = builder.Build();
        app.Use(next => context => UiLocalization.InvokeAsync(context, next));
        OperationalHealthService.MapEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };

        // Act
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var state = System.Text.Json.JsonDocument.Parse(await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(200, (int)live.StatusCode);
        Assert.Equal(expectedStatus, (int)ready.StatusCode);
        Assert.Equal(expectedState, state.RootElement.GetProperty("status").GetString());
        Assert.Equal(!reset, state.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, state.RootElement.GetProperty("guideAgeSeconds").ValueKind);
        Assert.True(live.Headers.CacheControl?.NoStore);
        Assert.True(ready.Headers.CacheControl?.NoStore);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Proves health requests bypass UI settings resolution, including first-request file loading and reset-mode settings errors.</summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthRequest_BypassesLocalizationSettings(string path)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var called = false;

        // Act
        await UiLocalization.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        // Assert
        Assert.True(called);
    }

    /// <summary>Defines readiness for first run, source degradation, reset-required mode and host transitions.</summary>
    [Theory]
    [InlineData(false, false, false, 0, "starting", false)]
    [InlineData(true, false, false, 0, "ready", true)]
    [InlineData(true, false, false, 2, "degraded", true)]
    [InlineData(true, true, false, 0, "factoryResetRequired", false)]
    [InlineData(true, false, true, 0, "stopping", false)]
    public void Readiness_UsesOnlyLifecycleAndCachedSummary(bool started, bool reset, bool stopping, int failures, string expected, bool ready)
    {
        // Arrange
        using var start = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        if (started)
        {
            start.Cancel();
        }
        if (stopping)
        {
            stop.Cancel();
        }
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(start.Token);
        lifetime.ApplicationStopping.Returns(stop.Token);
        var reads = 0;
        using var health = new OperationalHealthService(new(reset ? LineupStartupStatus.FactoryResetRequired : LineupStartupStatus.Ready), lifetime, () =>
        {
            reads++;
            return (failures, null);
        });

        // Act
        var state = health.Readiness();

        // Assert
        Assert.Equal(expected, state.Status);
        Assert.Equal(ready, state.Ready);
        Assert.Equal(ready ? 1 : 0, reads);
        Assert.Null(state.GuideAgeSeconds);
    }

    /// <summary>Leaves exact health endpoints accessible in factory-reset mode without broadening other recovery routes.</summary>
    [Theory]
    [InlineData("/health/live", FactoryResetRecoveryRequestAction.Continue)]
    [InlineData("/health/ready", FactoryResetRecoveryRequestAction.Continue)]
    [InlineData("/health/ready/other", FactoryResetRecoveryRequestAction.RedirectToSettings)]
    public void RecoveryPolicy_PreservesHealthRoutes(string path, FactoryResetRecoveryRequestAction expected)
    {
        // Arrange
        var requestPath = new PathString(path);

        // Act
        var action = FactoryResetRecoveryRequestPolicy.GetAction(requestPath);

        // Assert
        Assert.Equal(expected, action);
    }
}

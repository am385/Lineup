using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies real HDHomeRun control deadlines remain isolated from healthy physical devices.</summary>
public sealed class HdHomeRunControlIsolationTests
{
    /// <summary>Verifies stalled discovery and lineup requests preserve healthy publication and saved channel identities.</summary>
    [Theory]
    [InlineData("discovery", false)]
    [InlineData("discovery", true)]
    [InlineData("lineup", false)]
    [InlineData("lineup", true)]
    public async Task ControlFailure_HealthySibling_RemainsAvailable(string phase, bool timeout)
    {
        // Arrange
        using var http = new HttpClient(new ControlHandler(phase, timeout)) { Timeout = TimeSpan.FromMilliseconds(100) };
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(HDHomeRunPhysicalTunerServiceCollectionExtensions.HttpClientName).Returns(http);
        var native = new HDHomeRunService(NullLoggerFactory.Instance, Substitute.For<IHDHomeRunHttpControlFactory>());
        var driver = new HDHomeRunPhysicalTunerDriver(clients, NullLoggerFactory.Instance, native);
        var healthyId = PhysicalDeviceId.New();
        var failedId = PhysicalDeviceId.New();
        var healthy = Substitute.For<IPhysicalTunerDevice>();
        healthy.Snapshot.Returns(new PhysicalDeviceSnapshot
        {
            Id = healthyId,
            Kind = "healthy",
            Name = "Healthy",
            Capacity = new PhysicalTunerCapacity(1)
        });
        healthy.RefreshLineupAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>(
            [new PhysicalSourceChannel { Id = new SourceChannelId(healthyId, "7.1"), ProviderNumber = "7.1", ProviderName = "News" }]));
        var healthyDriver = Substitute.For<IPhysicalTunerDriver>();
        healthyDriver.Validate(Arg.Any<AdapterConfiguration>()).Returns(Array.Empty<string>());
        healthyDriver.CreateAsync(healthyId, "Healthy", Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(healthy));
        var adapters = Substitute.For<IStreamingAdapterRegistry>();
        adapters.GetPhysical("healthy").Returns(healthyDriver);
        adapters.GetPhysical(driver.Kind).Returns(driver);
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = healthyId, Name = "Healthy", Order = phase == "discovery" ? 0 : 1,
                    Adapter = new AdapterConfiguration("healthy", JsonSerializer.SerializeToElement(new { }))
                },
                new PhysicalDeviceDefinition
                {
                    Id = failedId, Name = "Stalled", Order = phase == "discovery" ? 1 : 0,
                    Adapter = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement(new { address = "http://hdhomerun.invalid/" }))
                }
            ]
        };
        var catalog = new PhysicalDeviceCatalog(adapters);
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        var factory = Substitute.For<IDbContextFactory<EpgDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new EpgDbContext(options)));
        var store = new StreamingTopologyStore(factory);
        var priorId = new SourceChannelId(failedId, "saved");
        await store.ReconcileAsync(failedId,
            [new PhysicalSourceChannel { Id = priorId, ProviderNumber = "9.1", ProviderName = "Saved" }], TestContext.Current.CancellationToken);
        await store.SetIdentityAsync(priorId, "109.1", "Saved identity", Guid.NewGuid(), TestContext.Current.CancellationToken);
        var prior = Assert.Single(await store.ReadActiveAsync(TestContext.Current.CancellationToken));
        IReadOnlyList<PhysicalLineupRefreshResult>? results = null;

        // Act
        if (phase == "discovery")
        {
            await catalog.ApplyAsync(configuration, TestContext.Current.CancellationToken);
        }
        else
        {
            results = await new PhysicalLineupService(catalog, store).RefreshAsync(configuration, TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Contains(catalog.Devices, device => device.Snapshot.Id == healthyId);
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var saved = Assert.Single(await store.ReadActiveAsync(TestContext.Current.CancellationToken), channel => channel.Source.Id == priorId);
        Assert.Equivalent(prior, saved);
        if (phase == "discovery")
        {
            Assert.Single(catalog.Devices);
        }
        else
        {
            Assert.NotNull(results);
            Assert.Equal([failedId, healthyId], results.Select(result => result.DeviceId));
            Assert.False(results[0].Succeeded);
            Assert.Contains("preserved", results[0].Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(results[1].Succeeded);
            Assert.Equal(1, results[1].ChannelCount);
            await healthy.Received(1).RefreshLineupAsync(Arg.Any<CancellationToken>());
        }
    }

    private sealed class ControlHandler(string phase, bool timeout) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(phase == "discovery" ? "discover.json" : "lineup.json", StringComparison.Ordinal))
            {
                if (timeout)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HDHomeRunDeviceInfo
                {
                    DeviceID = "12345678",
                    FriendlyName = "Probe",
                    ModelNumber = "HDHR",
                    FirmwareName = "test",
                    FirmwareVersion = "1",
                    DeviceAuth = "probe-only",
                    LineupURL = "http://hdhomerun.invalid/lineup.json",
                    TunerCount = 2,
                    BaseURL = "http://hdhomerun.invalid/"
                })
            };
        }
    }
}

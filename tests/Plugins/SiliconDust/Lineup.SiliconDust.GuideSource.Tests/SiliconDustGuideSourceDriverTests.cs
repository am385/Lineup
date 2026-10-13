using System.Text.Json;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;
using Xunit;

namespace Lineup.SiliconDust.GuideSource.Tests;

/// <summary>
/// Verifies the SiliconDust guide-source adapter.
/// </summary>
public sealed class SiliconDustGuideSourceDriverTests
{
    private readonly ISiliconDustDeviceAuthorizationProvider _authorizationProvider = Substitute.For<ISiliconDustDeviceAuthorizationProvider>();
    private readonly MockHttpMessageHandler _http = new();

    /// <summary>
    /// Verifies that a multi-device source concatenates fresh authorization and creates stable station channels.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ImportsDeviceOwnedGuide()
    {
        // Arrange
        var firstDeviceId = PhysicalDeviceId.New();
        var secondDeviceId = PhysicalDeviceId.New();
        var sourceId = GuideSourceId.New();
        _authorizationProvider.GetDeviceAuthAsync(firstDeviceId, Arg.Any<CancellationToken>()).Returns("first");
        _authorizationProvider.GetDeviceAuthAsync(secondDeviceId, Arg.Any<CancellationToken>()).Returns("second");
        _http.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=firstsecond")
            .Respond("application/xml", """
                <tv>
                  <channel id="station-7"><display-name>WXYZ</display-name><lcn>7.1</lcn></channel>
                  <channel id="station-9"><display-name>News</display-name><lcn>9</lcn></channel>
                  <programme channel="station-9" start="20261002150000 +0000" stop="20261002160000 +0000">
                    <title>Later</title>
                  </programme>
                  <programme channel="station-7" start="20261002130000 +0000" stop="20261002143000 +0000">
                    <title>Morning Show</title><desc>Daily news.</desc>
                  </programme>
                </tv>
                """);
        var source = await CreateDriver().CreateAsync(
            sourceId,
            Configuration(),
            [firstDeviceId, secondDeviceId],
            TestContext.Current.CancellationToken);

        // Act
        var result = await source.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(sourceId, result.SourceId);
        Assert.Equal(["9:station-77.1", "9:station-99"], result.Channels.Select(channel => channel.Id.ProviderKey));
        Assert.All(result.Channels, channel => Assert.Equal(sourceId, channel.Id.GuideSourceId));
        Assert.Equal(["Morning Show", "Later"], result.Programs.Select(program => program.Title));
        Assert.Equal("Daily news.", result.Programs[0].Synopsis);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T13:00:00Z"), result.CoverageStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T16:00:00Z"), result.CoverageEndUtc);
        await _authorizationProvider.Received(1).GetDeviceAuthAsync(firstDeviceId, Arg.Any<CancellationToken>());
        await _authorizationProvider.Received(1).GetDeviceAuthAsync(secondDeviceId, Arg.Any<CancellationToken>());
        _http.VerifyNoOutstandingExpectation();
    }

    /// <summary>
    /// Verifies a forbidden retry reacquires authorization for every subscribed device.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ForbiddenRetry_RebuildsCombinedAuthorization()
    {
        // Arrange
        var firstDeviceId = PhysicalDeviceId.New();
        var secondDeviceId = PhysicalDeviceId.New();
        _authorizationProvider.GetDeviceAuthAsync(firstDeviceId, Arg.Any<CancellationToken>()).Returns("stale-a", "fresh-a");
        _authorizationProvider.GetDeviceAuthAsync(secondDeviceId, Arg.Any<CancellationToken>()).Returns("stale-b", "fresh-b");
        _http.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=stale-astale-b")
            .Respond(System.Net.HttpStatusCode.Forbidden, "text/plain", "authorization rejected");
        _http.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=fresh-afresh-b")
            .Respond("application/xml", """
                <tv>
                  <channel id="station"><display-name>Station</display-name><lcn>2</lcn></channel>
                  <programme channel="station" start="20261002130000 +0000" stop="20261002140000 +0000">
                    <title>Show</title>
                  </programme>
                </tv>
                """);
        var source = await CreateDriver().CreateAsync(
            GuideSourceId.New(),
            Configuration(),
            [firstDeviceId, secondDeviceId],
            TestContext.Current.CancellationToken);

        // Act
        var result = await source.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(result.Channels);
        await _authorizationProvider.Received(2).GetDeviceAuthAsync(firstDeviceId, Arg.Any<CancellationToken>());
        await _authorizationProvider.Received(2).GetDeviceAuthAsync(secondDeviceId, Arg.Any<CancellationToken>());
        _http.VerifyNoOutstandingExpectation();
    }

    /// <summary>
    /// Verifies that a guide without publishable program data is rejected.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_Throws_WhenGuideHasNoUsablePrograms()
    {
        // Arrange
        var physicalDeviceId = PhysicalDeviceId.New();
        _authorizationProvider.GetDeviceAuthAsync(physicalDeviceId, Arg.Any<CancellationToken>()).Returns("short-lived");
        _http.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=short-lived")
            .Respond("application/xml", "<tv><channel id=\"station\"><display-name>Station</display-name><lcn>2</lcn></channel></tv>");
        var source = await CreateDriver().CreateAsync(
            GuideSourceId.New(),
            Configuration(),
            [physicalDeviceId],
            TestContext.Current.CancellationToken);

        // Act
        var action = () => source.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidDataException>(action);
        Assert.Contains("programs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that adapter configuration cannot contain persisted authorization.
    /// </summary>
    [Fact]
    public void Validate_RejectsConfigurationPropertiesAndWrongKind()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"deviceAuth":"must-not-persist"}""");
        var configuration = new AdapterConfiguration("other", document.RootElement.Clone());
        var driver = CreateDriver();

        // Act
        var errors = driver.Validate(configuration);

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Contains("kind", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("deviceAuth", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that every SiliconDust guide source has at least one physical device.
    /// </summary>
    [Fact]
    public async Task CreateAsync_Throws_WhenPhysicalDevicesAreEmpty()
    {
        // Arrange
        var sourceId = GuideSourceId.New();
        var driver = CreateDriver();

        // Act
        var action = async () => await driver.CreateAsync(sourceId, Configuration(), [], TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(action);
        Assert.Equal("physicalDeviceIds", exception.ParamName);
        Assert.Contains("physical device", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private SiliconDustGuideSourceDriver CreateDriver()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_http.ToHttpClient());
        return new SiliconDustGuideSourceDriver(
            httpClientFactory,
            Substitute.For<ILoggerFactory>(),
            _authorizationProvider,
            new SiliconDustXmltvParser());
    }

    private static AdapterConfiguration Configuration()
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(SiliconDustGuideSourceDriver.DriverKind, document.RootElement.Clone());
    }
}

using Lineup.HDHomeRun.Api.Models;
using Lineup.Core.Converters;
using Lineup.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Converters;

public class HDHomeRunToXmltvConverterTests
{
    private readonly ILogger<HDHomeRunToXmltvConverter> _logger;
    private readonly HDHomeRunToXmltvConverter _converter;

    public HDHomeRunToXmltvConverterTests()
    {
        _logger = Substitute.For<ILogger<HDHomeRunToXmltvConverter>>();
        _converter = new HDHomeRunToXmltvConverter(_logger);
    }

    [Fact]
    public void ConvertChannel_ReturnsValidXmltvChannel_WhenGivenEnrichedChannel()
    {
        // Arrange
        var enrichedChannel = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1",
            GuideName = "PBS NC",
            Affiliate = "PBS",
            ImageURL = "http://example.com/pbs.png"
        };

        // Act
        var result = _converter.ConvertChannel(enrichedChannel);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("5.1", result.Id);
        Assert.Equal("PBS NC", result.DisplayName);
        Assert.NotNull(result.Icon);
        Assert.Equal("http://example.com/pbs.png", result.Icon.Source);
    }

    [Fact]
    public void ConvertChannel_UsesUnknown_WhenGuideNameIsNull()
    {
        // Arrange
        var enrichedChannel = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "2.1",
            GuideName = null,
            ImageURL = null
        };

        // Act
        var result = _converter.ConvertChannel(enrichedChannel);

        // Assert
        Assert.Equal("Unknown", result.DisplayName);
        Assert.NotNull(result.Icon);
        Assert.Equal("", result.Icon.Source);
    }

    [Fact]
    public void ConvertProgramme_ReturnsValidXmltvProgramme_WhenGivenProgram()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endTime = startTime + 3600; // 1 hour later

        var program = new HDHomeRunProgram
        {
            StartTime = startTime,
            EndTime = endTime,
            Title = "Test Show",
            EpisodeTitle = "Pilot Episode",
            Synopsis = "This is a test synopsis.",
            OriginalAirdate = startTime - 86400 // 1 day ago
        };

        // Act
        var result = _converter.ConvertProgramme(program, "5.1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("5.1", result.Channel);
        Assert.Equal("Test Show", result.Title?.Value);
        Assert.Equal("Pilot Episode", result.SubTitle?.Value);
        Assert.Equal("This is a test synopsis.", result.Description?.Value);
    }

    [Fact]
    public void ConvertProgramme_OmitsOptionalFields_WhenNotProvided()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var endTime = startTime + 1800; // 30 minutes later

        var program = new HDHomeRunProgram
        {
            StartTime = startTime,
            EndTime = endTime,
            Title = "Simple Show"
        };

        // Act
        var result = _converter.ConvertProgramme(program, "2.1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Simple Show", result.Title?.Value);
        Assert.Null(result.SubTitle);
        Assert.Null(result.Description);
    }

    [Fact]
    public void ConvertProgramme_SetsCorrectLanguage()
    {
        // Arrange
        var program = new HDHomeRunProgram
        {
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            EndTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            Title = "English Show",
            Synopsis = "English description"
        };

        // Act
        var result = _converter.ConvertProgramme(program, "3.1");

        // Assert
        Assert.Equal("en", result?.Title?.Language);
        Assert.Equal("en", result?.Description?.Language);
    }
}

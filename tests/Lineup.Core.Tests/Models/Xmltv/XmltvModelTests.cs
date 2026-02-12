using Lineup.Core.Models.Xmltv;
using Xunit;

namespace Lineup.Core.Tests.Models.Xmltv;

public class XmltvDocumentTests
{
    [Fact]
    public void XmltvDocument_HasDefaultEmptyCollections()
    {
        // Arrange & Act
        var document = new XmltvDocument();

        // Assert
        Assert.NotNull(document.Channels);
        Assert.NotNull(document.Programmes);
        Assert.Empty(document.Channels);
        Assert.Empty(document.Programmes);
        Assert.Equal(string.Empty, document.SourceInfoName);
        Assert.Equal(string.Empty, document.GeneratorInfoName);
    }

    [Fact]
    public void XmltvDocument_CanAddChannels()
    {
        // Arrange
        var document = new XmltvDocument();
        var channel = new XmltvChannel
        {
            Id = "5.1",
            DisplayName = "PBS"
        };

        // Act
        document.Channels.Add(channel);

        // Assert
        Assert.Single(document.Channels);
        Assert.Equal("5.1", document.Channels[0].Id);
    }

    [Fact]
    public void XmltvDocument_CanAddProgrammes()
    {
        // Arrange
        var document = new XmltvDocument();
        var programme = new XmltvProgramme
        {
            Channel = "5.1",
            Start = "20240101120000 +0000",
            Stop = "20240101130000 +0000",
            Title = new XmltvText { Value = "Test Show" }
        };

        // Act
        document.Programmes.Add(programme);

        // Assert
        Assert.Single(document.Programmes);
        Assert.Equal("Test Show", document.Programmes[0].Title?.Value);
    }
}

public class XmltvChannelTests
{
    [Fact]
    public void XmltvChannel_HasDefaultEmptyStrings()
    {
        // Arrange & Act
        var channel = new XmltvChannel();

        // Assert
        Assert.Equal(string.Empty, channel.Id);
        Assert.Equal(string.Empty, channel.DisplayName);
        Assert.Null(channel.Icon);
    }

    [Fact]
    public void XmltvChannel_CanSetAllProperties()
    {
        // Arrange & Act
        var channel = new XmltvChannel
        {
            Id = "7.1",
            DisplayName = "ABC Local",
            Icon = new XmltvIcon { Source = "http://example.com/abc.png" }
        };

        // Assert
        Assert.Equal("7.1", channel.Id);
        Assert.Equal("ABC Local", channel.DisplayName);
        Assert.NotNull(channel.Icon);
        Assert.Equal("http://example.com/abc.png", channel.Icon.Source);
    }
}

public class XmltvProgrammeTests
{
    [Fact]
    public void XmltvProgramme_HasDefaultEmptyCollections()
    {
        // Arrange & Act
        var programme = new XmltvProgramme();

        // Assert
        Assert.NotNull(programme.Categories);
        Assert.NotNull(programme.EpisodeNumbers);
        Assert.Empty(programme.Categories);
        Assert.Empty(programme.EpisodeNumbers);
        Assert.Equal(string.Empty, programme.Start);
        Assert.Equal(string.Empty, programme.Stop);
        Assert.Equal(string.Empty, programme.Channel);
    }

    [Fact]
    public void XmltvProgramme_CanSetOptionalFields()
    {
        // Arrange & Act
        var programme = new XmltvProgramme
        {
            Start = "20240101120000 +0000",
            Stop = "20240101130000 +0000",
            Channel = "5.1",
            Title = new XmltvText { Language = "en", Value = "Main Title" },
            SubTitle = new XmltvText { Language = "en", Value = "Episode Title" },
            Description = new XmltvText { Language = "en", Value = "Program description" },
            Icon = new XmltvIcon { Source = "http://example.com/show.png" },
            PreviouslyShown = new XmltvPreviouslyShown { Start = "20230101120000 +0000" },
            New = new XmltvNew()
        };

        // Assert
        Assert.Equal("Main Title", programme.Title?.Value);
        Assert.Equal("Episode Title", programme.SubTitle?.Value);
        Assert.Equal("Program description", programme.Description?.Value);
        Assert.NotNull(programme.Icon);
        Assert.NotNull(programme.PreviouslyShown);
        Assert.NotNull(programme.New);
    }

    [Fact]
    public void XmltvProgramme_CanAddCategories()
    {
        // Arrange
        var programme = new XmltvProgramme();

        // Act
        programme.Categories.Add(new XmltvText { Language = "en", Value = "Drama" });
        programme.Categories.Add(new XmltvText { Language = "en", Value = "Action" });

        // Assert
        Assert.Equal(2, programme.Categories.Count);
        Assert.Equal("Drama", programme.Categories[0].Value);
        Assert.Equal("Action", programme.Categories[1].Value);
    }

    [Fact]
    public void XmltvProgramme_CanAddEpisodeNumbers()
    {
        // Arrange
        var programme = new XmltvProgramme();

        // Act
        programme.EpisodeNumbers.Add(new XmltvEpisodeNum { System = "xmltv_ns", Value = "1.5.0/1" });
        programme.EpisodeNumbers.Add(new XmltvEpisodeNum { System = "onscreen", Value = "S2E6" });

        // Assert
        Assert.Equal(2, programme.EpisodeNumbers.Count);
        Assert.Equal("xmltv_ns", programme.EpisodeNumbers[0].System);
        Assert.Equal("S2E6", programme.EpisodeNumbers[1].Value);
    }
}

public class XmltvTextTests
{
    [Fact]
    public void XmltvText_HasDefaultValues()
    {
        // Arrange & Act
        var text = new XmltvText();

        // Assert
        Assert.Null(text.Language);
        Assert.Equal(string.Empty, text.Value);
    }

    [Fact]
    public void XmltvText_CanSetProperties()
    {
        // Arrange & Act
        var text = new XmltvText
        {
            Language = "es",
            Value = "Hola Mundo"
        };

        // Assert
        Assert.Equal("es", text.Language);
        Assert.Equal("Hola Mundo", text.Value);
    }
}

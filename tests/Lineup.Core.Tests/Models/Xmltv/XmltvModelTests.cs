using Lineup.Core.Models.Xmltv;
using Xunit;

namespace Lineup.Core.Tests.Models.Xmltv;

/// <summary>
/// Represents xmltv document tests.
/// </summary>
public class XmltvDocumentTests
{
    /// <summary>
    /// Performs the xmltv document_has default empty collections operation.
    /// </summary>
    [Fact]
    public void XmltvDocument_HasDefaultEmptyCollections()
    {
        // Arrange
        // Act
        var document = new XmltvDocument();

        // Assert
        Assert.NotNull(document.Channels);
        Assert.NotNull(document.Programmes);
        Assert.Empty(document.Channels);
        Assert.Empty(document.Programmes);
        Assert.Equal(string.Empty, document.SourceInfoName);
        Assert.Equal(string.Empty, document.GeneratorInfoName);
    }

    /// <summary>
    /// Performs the xmltv document_can add channels operation.
    /// </summary>
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

    /// <summary>
    /// Performs the xmltv document_can add programmes operation.
    /// </summary>
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

/// <summary>
/// Represents xmltv channel tests.
/// </summary>
public class XmltvChannelTests
{
    /// <summary>
    /// Performs the xmltv channel_has default empty strings operation.
    /// </summary>
    [Fact]
    public void XmltvChannel_HasDefaultEmptyStrings()
    {
        // Arrange
        // Act
        var channel = new XmltvChannel();

        // Assert
        Assert.Equal(string.Empty, channel.Id);
        Assert.Equal(string.Empty, channel.DisplayName);
        Assert.Null(channel.Icon);
    }

    /// <summary>
    /// Performs the xmltv channel_can set all properties operation.
    /// </summary>
    [Fact]
    public void XmltvChannel_CanSetAllProperties()
    {
        // Arrange
        // Act
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

/// <summary>
/// Represents xmltv programme tests.
/// </summary>
public class XmltvProgrammeTests
{
    /// <summary>
    /// Performs the xmltv programme_has default empty collections operation.
    /// </summary>
    [Fact]
    public void XmltvProgramme_HasDefaultEmptyCollections()
    {
        // Arrange
        // Act
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

    /// <summary>
    /// Performs the xmltv programme_can set optional fields operation.
    /// </summary>
    [Fact]
    public void XmltvProgramme_CanSetOptionalFields()
    {
        // Arrange
        // Act
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

    /// <summary>
    /// Performs the xmltv programme_can add categories operation.
    /// </summary>
    [Fact]
    public void XmltvProgramme_CanAddCategories()
    {
        // Arrange
        var programme = new XmltvProgramme();

        programme.Categories.Add(new XmltvText { Language = "en", Value = "Drama" });
        // Act
        programme.Categories.Add(new XmltvText { Language = "en", Value = "Action" });

        // Assert
        Assert.Equal(2, programme.Categories.Count);
        Assert.Equal("Drama", programme.Categories[0].Value);
        Assert.Equal("Action", programme.Categories[1].Value);
    }

    /// <summary>
    /// Performs the xmltv programme_can add episode numbers operation.
    /// </summary>
    [Fact]
    public void XmltvProgramme_CanAddEpisodeNumbers()
    {
        // Arrange
        var programme = new XmltvProgramme();

        programme.EpisodeNumbers.Add(new XmltvEpisodeNum { System = "xmltv_ns", Value = "1.5.0/1" });
        // Act
        programme.EpisodeNumbers.Add(new XmltvEpisodeNum { System = "onscreen", Value = "S2E6" });

        // Assert
        Assert.Equal(2, programme.EpisodeNumbers.Count);
        Assert.Equal("xmltv_ns", programme.EpisodeNumbers[0].System);
        Assert.Equal("S2E6", programme.EpisodeNumbers[1].Value);
    }
}

/// <summary>
/// Represents xmltv text tests.
/// </summary>
public class XmltvTextTests
{
    /// <summary>
    /// Performs the xmltv text_has default values operation.
    /// </summary>
    [Fact]
    public void XmltvText_HasDefaultValues()
    {
        // Arrange
        // Act
        var text = new XmltvText();

        // Assert
        Assert.Null(text.Language);
        Assert.Equal(string.Empty, text.Value);
    }

    /// <summary>
    /// Performs the xmltv text_can set properties operation.
    /// </summary>
    [Fact]
    public void XmltvText_CanSetProperties()
    {
        // Arrange
        // Act
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

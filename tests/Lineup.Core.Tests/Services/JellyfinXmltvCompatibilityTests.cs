using Lineup.Core.Models.Xmltv;
using Lineup.Core.Services;
using Jellyfin.XmlTv;
using Jellyfin.XmlTv.Entities;
using Xunit;

namespace Lineup.Core.Tests.Services;

/// <summary>
/// Tests that verify Jellyfin's XmlTvReader can correctly deserialize
/// XMLTV documents produced by <see cref="XmltvSerializer"/>.
/// </summary>
public class JellyfinXmltvCompatibilityTests : IDisposable
{
    private readonly string _tempFile;

    public JellyfinXmltvCompatibilityTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"jellyfin_compat_{Guid.NewGuid():N}.xml");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    /// <summary>
    /// Writes an <see cref="XmltvDocument"/> to the temp file and returns
    /// a <see cref="XmlTvReader"/> pointed at it.
    /// </summary>
    private XmlTvReader WriteAndCreateReader(XmltvDocument document, string language = "en")
    {
        XmltvSerializer.SerializeToFile(document, _tempFile);
        return new XmlTvReader(_tempFile, language);
    }

    [Fact]
    public void Jellyfin_CanReadChannels()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel
                {
                    Id = "5.1",
                    DisplayName = "PBS NC",
                    Icon = new XmltvIcon { Source = "http://example.com/pbs.png" }
                },
                new XmltvChannel
                {
                    Id = "11.1",
                    DisplayName = "FOX",
                    Icon = new XmltvIcon { Source = "http://example.com/fox.png" }
                }
            ]
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var channels = reader.GetChannels().ToList();

        // Assert
        Assert.Equal(2, channels.Count);

        var pbs = channels.Single(c => c.Id == "5.1");
        Assert.Equal("PBS NC", pbs.DisplayName);
        Assert.NotNull(pbs.Icon);
        Assert.Equal("http://example.com/pbs.png", pbs.Icon.Source);

        var fox = channels.Single(c => c.Id == "11.1");
        Assert.Equal("FOX", fox.DisplayName);
    }

    [Fact]
    public void Jellyfin_CanReadBasicProgramme()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel { Id = "5.1", DisplayName = "PBS NC" }
            ],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Nature" },
                    Description = new XmltvText { Language = "en", Value = "A nature documentary." }
                }
            ]
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var programmes = reader.GetProgrammes(
            "5.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();

        // Assert
        Assert.Single(programmes);
        var prog = programmes[0];
        Assert.Equal("5.1", prog.ChannelId);
        Assert.Equal("Nature", prog.Title);
        Assert.Equal("A nature documentary.", prog.Description);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero), prog.StartDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 13, 0, 0, TimeSpan.Zero), prog.EndDate);
    }

    [Fact]
    public void Jellyfin_CanReadSubTitle()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.SubTitle = new XmltvText { Language = "en", Value = "The Pilot Episode" };
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        Assert.NotNull(programmes[0].Episode);
        Assert.Equal("The Pilot Episode", programmes[0].Episode.Title);
    }

    [Fact]
    public void Jellyfin_CanReadCategories()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.Categories =
            [
                new XmltvText { Language = "en", Value = "Documentary" },
                new XmltvText { Language = "en", Value = "Nature" }
            ];
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        Assert.Contains("Documentary", programmes[0].Categories);
        Assert.Contains("Nature", programmes[0].Categories);
    }

    [Fact]
    public void Jellyfin_CanReadProgrammeIcon()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.Icon = new XmltvIcon { Source = "http://example.com/show.jpg" };
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        Assert.NotNull(programmes[0].Icon);
        Assert.Equal("http://example.com/show.jpg", programmes[0].Icon!.Source);
    }

    [Fact]
    public void Jellyfin_CanReadXmltvNsEpisodeNumber()
    {
        // Arrange — xmltv_ns format: "season.episode.part" (0-based)
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.EpisodeNumbers =
            [
                new XmltvEpisodeNum { System = "onscreen", Value = "S03E05" },
                new XmltvEpisodeNum { System = "xmltv_ns", Value = "2.4.0/0" }
            ];
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        var episode = programmes[0].Episode;
        Assert.NotNull(episode);
        // xmltv_ns is 0-based, Jellyfin converts to 1-based
        Assert.Equal(3, episode.Series);
        Assert.Equal(5, episode.Episode);
    }

    [Fact]
    public void Jellyfin_CanReadPreviouslyShown()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.PreviouslyShown = new XmltvPreviouslyShown
            {
                Start = "20240101120000"
            };
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        Assert.True(programmes[0].IsPreviouslyShown);
    }

    [Fact]
    public void Jellyfin_CanReadNewIndicator()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.New = new XmltvNew();
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        Assert.True(programmes[0].IsNew);
    }

    [Fact]
    public void Jellyfin_CanReadTimezoneOffset()
    {
        // Arrange — verify non-UTC timezone offsets are parsed correctly
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "7.1", DisplayName = "ABC" }],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240615200000 -0400",
                    Stop = "20240615210000 -0400",
                    Channel = "7.1",
                    Title = new XmltvText { Language = "en", Value = "Evening News" }
                }
            ]
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var programmes = reader.GetProgrammes(
            "7.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 17, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();

        // Assert — 20:00 -0400 = 00:00 UTC on the 16th
        Assert.Single(programmes);
        Assert.Equal(new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), programmes[0].StartDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 16, 1, 0, 0, TimeSpan.Zero), programmes[0].EndDate);
    }

    [Fact]
    public void Jellyfin_CanReadMultipleProgrammesSameChannel()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "5.1", DisplayName = "PBS NC" }],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show A" }
                },
                new XmltvProgramme
                {
                    Start = "20240615130000 +0000",
                    Stop = "20240615140000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show B" }
                },
                new XmltvProgramme
                {
                    Start = "20240615140000 +0000",
                    Stop = "20240615150000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show C" }
                }
            ]
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var programmes = reader.GetProgrammes(
            "5.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();

        // Assert
        Assert.Equal(3, programmes.Count);
        Assert.Equal("Show A", programmes[0].Title);
        Assert.Equal("Show B", programmes[1].Title);
        Assert.Equal("Show C", programmes[2].Title);
    }

    [Fact]
    public void Jellyfin_CanReadFullyPopulatedProgramme()
    {
        // Arrange — a programme with all fields populated as the converter would produce
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.Title = new XmltvText { Language = "en", Value = "Breaking Bad" };
            prog.SubTitle = new XmltvText { Language = "en", Value = "Ozymandias" };
            prog.Description = new XmltvText { Language = "en", Value = "Walt faces the consequences." };
            prog.Categories =
            [
                new XmltvText { Language = "en", Value = "Drama" },
                new XmltvText { Language = "en", Value = "Thriller" }
            ];
            prog.Icon = new XmltvIcon { Source = "http://example.com/bb.jpg" };
            prog.EpisodeNumbers =
            [
                new XmltvEpisodeNum { System = "onscreen", Value = "S05E14" },
                new XmltvEpisodeNum { System = "xmltv_ns", Value = "4.13.0/0" }
            ];
            prog.PreviouslyShown = new XmltvPreviouslyShown { Start = "20130915000000" };
        });

        // Act
        var programmes = GetAllProgrammes(document);

        // Assert
        Assert.Single(programmes);
        var prog = programmes[0];
        Assert.Equal("Breaking Bad", prog.Title);
        Assert.NotNull(prog.Episode);
        Assert.Equal("Ozymandias", prog.Episode.Title);
        Assert.Equal("Walt faces the consequences.", prog.Description);
        Assert.Contains("Drama", prog.Categories);
        Assert.Contains("Thriller", prog.Categories);
        Assert.NotNull(prog.Icon);
        Assert.Equal("http://example.com/bb.jpg", prog.Icon.Source);
        Assert.Equal(5, prog.Episode.Series);
        Assert.Equal(14, prog.Episode.Episode);
        Assert.True(prog.IsPreviouslyShown);
    }

    [Fact]
    public void Jellyfin_CanReadLanguages()
    {
        // Arrange
        var document = CreateDocumentWithProgramme(prog =>
        {
            prog.Title = new XmltvText { Language = "en", Value = "Test Show" };
            prog.Description = new XmltvText { Language = "en", Value = "A description." };
        });

        // Act
        var reader = WriteAndCreateReader(document);
        var languages = reader.GetLanguages(CancellationToken.None).ToList();

        // Assert
        Assert.NotEmpty(languages);
        Assert.Contains(languages, l => l.Name == "en");
    }

    [Fact]
    public void Jellyfin_FiltersChannelProgrammesCorrectly()
    {
        // Arrange — programmes on different channels
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel { Id = "5.1", DisplayName = "PBS" },
                new XmltvChannel { Id = "7.1", DisplayName = "ABC" }
            ],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "PBS Show" }
                },
                new XmltvProgramme
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "7.1",
                    Title = new XmltvText { Language = "en", Value = "ABC Show" }
                }
            ]
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var pbsProgs = reader.GetProgrammes(
            "5.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();
        var abcProgs = reader.GetProgrammes(
            "7.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();

        // Assert
        Assert.Single(pbsProgs);
        Assert.Equal("PBS Show", pbsProgs[0].Title);
        Assert.Single(abcProgs);
        Assert.Equal("ABC Show", abcProgs[0].Title);
    }

    [Fact]
    public void Jellyfin_ReturnsEmptyForEmptyDocument()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core"
        };

        // Act
        var reader = WriteAndCreateReader(document);
        var channels = reader.GetChannels().ToList();

        // Assert
        Assert.Empty(channels);
    }

    /// <summary>
    /// Creates a document with a single channel and a single programme,
    /// applying the given customization action to the programme before returning.
    /// </summary>
    private static XmltvDocument CreateDocumentWithProgramme(Action<XmltvProgramme> customize)
    {
        var programme = new XmltvProgramme
        {
            Start = "20240615120000 +0000",
            Stop = "20240615130000 +0000",
            Channel = "5.1",
            Title = new XmltvText { Language = "en", Value = "Test Show" }
        };

        customize(programme);

        return new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "5.1", DisplayName = "PBS NC" }],
            Programmes = [programme]
        };
    }

    /// <summary>
    /// Writes the document and reads all programmes for channel 5.1 on 2024-06-15.
    /// </summary>
    private List<XmlTvProgram> GetAllProgrammes(XmltvDocument document)
    {
        var reader = WriteAndCreateReader(document);
        return reader.GetProgrammes(
            "5.1",
            new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None).ToList();
    }
}

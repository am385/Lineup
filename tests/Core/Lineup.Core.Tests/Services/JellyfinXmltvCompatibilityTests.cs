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

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinXmltvCompatibilityTests"/> class.
    /// </summary>
    public JellyfinXmltvCompatibilityTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"jellyfin_compat_{Guid.NewGuid():N}.xml");
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
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

    /// <summary>
    /// Performs the jellyfin_can read channels operation.
    /// </summary>
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

        var reader = WriteAndCreateReader(document);
        // Act
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

    /// <summary>
    /// Performs the jellyfin_can read basic program operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadBasicProgram()
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
            Programs =
            [
                new XmltvProgram
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Nature" },
                    Description = new XmltvText { Language = "en", Value = "A nature documentary." }
                }
            ]
        };

        var reader = WriteAndCreateReader(document);
        // Act
        var programs = reader.GetProgrammes("5.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();

        // Assert
        Assert.Single(programs);
        var prog = programs[0];
        Assert.Equal("5.1", prog.ChannelId);
        Assert.Equal("Nature", prog.Title);
        Assert.Equal("A nature documentary.", prog.Description);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero), prog.StartDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 15, 13, 0, 0, TimeSpan.Zero), prog.EndDate);
    }

    /// <summary>
    /// Performs the jellyfin_can read sub title operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadSubTitle()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.SubTitle = new XmltvText { Language = "en", Value = "The Pilot Episode" };
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        Assert.NotNull(programs[0].Episode);
        Assert.Equal("The Pilot Episode", programs[0].Episode.Title);
    }

    /// <summary>
    /// Performs the jellyfin_can read categories operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadCategories()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.Categories =
            [
                new XmltvText { Language = "en", Value = "Documentary" },
                new XmltvText { Language = "en", Value = "Nature" }
            ];
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        Assert.Contains("Documentary", programs[0].Categories);
        Assert.Contains("Nature", programs[0].Categories);
    }

    /// <summary>
    /// Performs the jellyfin_can read program icon operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadProgramIcon()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.Icon = new XmltvIcon { Source = "http://example.com/show.jpg" };
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        Assert.NotNull(programs[0].Icon);
        Assert.Equal("http://example.com/show.jpg", programs[0].Icon!.Source);
    }

    /// <summary>
    /// Performs the jellyfin_can read xmltv ns episode number operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadXmltvNsEpisodeNumber()
    {
        // Arrange — xmltv_ns format: "season.episode.part" (0-based)
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.EpisodeNumbers =
            [
                new XmltvEpisodeNum { System = "onscreen", Value = "S03E05" },
                new XmltvEpisodeNum { System = "xmltv_ns", Value = "2.4.0/0" }
            ];
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        var episode = programs[0].Episode;
        Assert.NotNull(episode);
        // xmltv_ns is 0-based, Jellyfin converts to 1-based
        Assert.Equal(3, episode.Series);
        Assert.Equal(5, episode.Episode);
    }

    /// <summary>
    /// Performs the jellyfin_can read previously shown operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadPreviouslyShown()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.PreviouslyShown = new XmltvPreviouslyShown
            {
                Start = "20240101120000"
            };
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        Assert.True(programs[0].IsPreviouslyShown);
    }

    /// <summary>
    /// Performs the jellyfin_can read new indicator operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadNewIndicator()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.New = new XmltvNew();
        });

        // Act
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        Assert.True(programs[0].IsNew);
    }

    /// <summary>
    /// Performs the jellyfin_can read timezone offset operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadTimezoneOffset()
    {
        // Arrange — verify non-UTC timezone offsets are parsed correctly
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "7.1", DisplayName = "ABC" }],
            Programs =
            [
                new XmltvProgram
                {
                    Start = "20240615200000 -0400",
                    Stop = "20240615210000 -0400",
                    Channel = "7.1",
                    Title = new XmltvText { Language = "en", Value = "Evening News" }
                }
            ]
        };

        var reader = WriteAndCreateReader(document);
        // Act
        var programs = reader.GetProgrammes("7.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 17, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();

        // Assert — 20:00 -0400 = 00:00 UTC on the 16th
        // Assert
        Assert.Single(programs);
        Assert.Equal(new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), programs[0].StartDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 16, 1, 0, 0, TimeSpan.Zero), programs[0].EndDate);
    }

    /// <summary>
    /// Performs the jellyfin_can read multiple programs same channel operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadMultipleProgramsSameChannel()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "5.1", DisplayName = "PBS NC" }],
            Programs =
            [
                new XmltvProgram
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show A" }
                },
                new XmltvProgram
                {
                    Start = "20240615130000 +0000",
                    Stop = "20240615140000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show B" }
                },
                new XmltvProgram
                {
                    Start = "20240615140000 +0000",
                    Stop = "20240615150000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Show C" }
                }
            ]
        };

        var reader = WriteAndCreateReader(document);
        // Act
        var programs = reader.GetProgrammes("5.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();

        // Assert
        Assert.Equal(3, programs.Count);
        Assert.Equal("Show A", programs[0].Title);
        Assert.Equal("Show B", programs[1].Title);
        Assert.Equal("Show C", programs[2].Title);
    }

    /// <summary>
    /// Performs the jellyfin_can read fully populated program operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadFullyPopulatedProgram()
    {
        // Arrange — a program with all fields populated as the converter would produce
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
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
        var programs = GetAllPrograms(document);

        // Assert
        Assert.Single(programs);
        var prog = programs[0];
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

    /// <summary>
    /// Performs the jellyfin_can read languages operation.
    /// </summary>
    [Fact]
    public void Jellyfin_CanReadLanguages()
    {
        // Arrange
        var document = CreateDocumentWithProgram(prog =>
        {
            prog.Title = new XmltvText { Language = "en", Value = "Test Show" };
            prog.Description = new XmltvText { Language = "en", Value = "A description." };
        });

        var reader = WriteAndCreateReader(document);
        // Act
        var languages = reader.GetLanguages(CancellationToken.None).ToList();

        // Assert
        Assert.NotEmpty(languages);
        Assert.Contains(languages, l => l.Name == "en");
    }

    /// <summary>
    /// Performs the jellyfin_filters channel programs correctly operation.
    /// </summary>
    [Fact]
    public void Jellyfin_FiltersChannelProgramsCorrectly()
    {
        // Arrange — programs on different channels
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel { Id = "5.1", DisplayName = "PBS" },
                new XmltvChannel { Id = "7.1", DisplayName = "ABC" }
            ],
            Programs =
            [
                new XmltvProgram
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "PBS Show" }
                },
                new XmltvProgram
                {
                    Start = "20240615120000 +0000",
                    Stop = "20240615130000 +0000",
                    Channel = "7.1",
                    Title = new XmltvText { Language = "en", Value = "ABC Show" }
                }
            ]
        };

        var reader = WriteAndCreateReader(document);
        var pbsProgs = reader.GetProgrammes("5.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();
        // Act
        var abcProgs = reader.GetProgrammes("7.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();

        // Assert
        Assert.Single(pbsProgs);
        Assert.Equal("PBS Show", pbsProgs[0].Title);
        Assert.Single(abcProgs);
        Assert.Equal("ABC Show", abcProgs[0].Title);
    }

    /// <summary>
    /// Performs the jellyfin_returns empty for empty document operation.
    /// </summary>
    [Fact]
    public void Jellyfin_ReturnsEmptyForEmptyDocument()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core"
        };

        var reader = WriteAndCreateReader(document);
        // Act
        var channels = reader.GetChannels().ToList();

        // Assert
        Assert.Empty(channels);
    }

    /// <summary>
    /// Creates a document with a single channel and a single program,
    /// applying the given customization action to the program before returning.
    /// </summary>
    private static XmltvDocument CreateDocumentWithProgram(Action<XmltvProgram> customize)
    {
        var program = new XmltvProgram
        {
            Start = "20240615120000 +0000",
            Stop = "20240615130000 +0000",
            Channel = "5.1",
            Title = new XmltvText { Language = "en", Value = "Test Show" }
        };

        customize(program);

        return new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels = [new XmltvChannel { Id = "5.1", DisplayName = "PBS NC" }],
            Programs = [program]
        };
    }

    /// <summary>
    /// Writes the document and reads all programs for channel 5.1 on 2024-06-15.
    /// </summary>
    private List<XmlTvProgram> GetAllPrograms(XmltvDocument document)
    {
        var reader = WriteAndCreateReader(document);
        return reader.GetProgrammes("5.1", new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 6, 16, 0, 0, 0, TimeSpan.Zero), CancellationToken.None).ToList();
    }
}

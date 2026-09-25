using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>
/// Verifies cached channel metadata persistence.
/// </summary>
public class EpgRepositoryChannelTests
{
    /// <summary>
    /// Verifies that tuner-provided channel flags survive a cache round trip.
    /// </summary>
    [Fact]
    public async Task StoreAndLoadChannel_PreservesTunerFlags()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        var channel = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "117.1",
            GuideName = "Protected",
            DRM = true,
            Favorite = true
        };

        // Act
        await repository.StoreChannelAsync(channel);
        // Assert
        var cachedChannel = Assert.Single(await repository.GetChannelsAsync());

        Assert.True(cachedChannel.DRM);
        Assert.True(cachedChannel.Favorite);
    }

    /// <summary>
    /// Verifies existing databases receive newly introduced tuner metadata columns.
    /// </summary>
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_ExistingChannelTable_AddsTunerMetadataColumns()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Channels (
                    Id INTEGER NOT NULL CONSTRAINT PK_Channels PRIMARY KEY AUTOINCREMENT,
                    GuideNumber TEXT NOT NULL,
                    GuideName TEXT NULL,
                    Affiliate TEXT NULL,
                    ImageURL TEXT NULL,
                    LastUpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE Programs (
                    Id INTEGER NOT NULL CONSTRAINT PK_Programs PRIMARY KEY AUTOINCREMENT,
                    GuideNumber TEXT NOT NULL,
                    Title TEXT NULL,
                    StartTime INTEGER NOT NULL,
                    EndTime INTEGER NOT NULL
                );
                CREATE TABLE GuideImports (
                    Id TEXT NOT NULL CONSTRAINT PK_GuideImports PRIMARY KEY,
                    CompletedUtc TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);

        // Act
        await repository.EnsureDatabaseCreatedAsync();

        // Assert
        var channelColumns = await GetColumnsAsync(connection, "Channels");
        var programColumns = await GetColumnsAsync(connection, "Programs");
        var importColumns = await GetColumnsAsync(connection, "GuideImports");
        Assert.Contains(nameof(StoredChannel.DRM), channelColumns);
        Assert.Contains(nameof(StoredChannel.Favorite), channelColumns);
        Assert.Contains(nameof(StoredChannel.SupplementalXml), channelColumns);
        Assert.Contains(nameof(StoredProgram.SupplementalXml), programColumns);
        Assert.Contains(nameof(StoredGuideImport.SupplementalXml), importColumns);
    }

    /// <summary>
    /// Verifies that importing a complete guide snapshot removes stale cache records.
    /// </summary>
    [Fact]
    public async Task ReplaceRawEpgDataAsync_ReplacesExistingGuide()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Old",
                Guide = [new HDHomeRunProgram { Title = "Old Show", StartTime = 100, EndTime = 200 }]
            }
        ]);
        var replacement = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "New",
            Guide = [new HDHomeRunProgram { Title = "New Show", StartTime = 300, EndTime = 400 }]
        };

        // Act
        await repository.ReplaceRawEpgDataAsync([replacement], TestContext.Current.CancellationToken);

        // Assert
        var channel = Assert.Single(await repository.GetChannelsAsync());
        var programme = Assert.Single(await repository.GetProgramsAsync());
        Assert.Equal("7.1", channel.GuideNumber);
        Assert.Equal("New Show", programme.Title);
    }

    /// <summary>
    /// Verifies cancellation rolls back a guide replacement before its commit point.
    /// </summary>
    [Fact]
    public async Task ReplaceRawEpgDataAsync_WhenCancelled_PreservesExistingGuide()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Old"
            }
        ]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReplaceRawEpgDataAsync([new HDHomeRunChannelEpgSegment { GuideNumber = "7.1", GuideName = "New" }], cancellation.Token));

        // Assert
        var channel = Assert.Single(await repository.GetChannelsAsync());
        Assert.Equal("2.1", channel.GuideNumber);
    }

    /// <summary>
    /// Verifies authoritative imports retain recent history, prune expired history, and replace stale future schedules.
    /// </summary>
    [Fact]
    public async Task ImportGuideAsync_ReconcilesFutureAndRetainsConfiguredHistory()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "7.1",
                GuideName = "Channel",
                Guide =
                [
                    CreateProgram("Expired", now.AddHours(-30), now.AddHours(-29)),
                    CreateProgram("Recent", now.AddHours(-2), now.AddHours(-1)),
                    CreateProgram("Stale Future", now.AddHours(1), now.AddHours(2))
                ]
            },
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "9.1",
                GuideName = "Removed Channel",
                Guide = [CreateProgram("Removed Future", now.AddHours(1), now.AddHours(2))]
            }
        ]);
        var replacement = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "Channel",
            Guide = [CreateProgram("Updated Future", now.AddHours(1), now.AddHours(2))]
        };

        // Act
        await repository.ImportGuideAsync([replacement], TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        // Assert
        var titles = (await repository.GetProgramsAsync()).Select(program => program.Title).ToArray();
        Assert.Contains("Recent", titles);
        Assert.Contains("Updated Future", titles);
        Assert.DoesNotContain("Expired", titles);
        Assert.DoesNotContain("Stale Future", titles);
        Assert.DoesNotContain("Removed Future", titles);
        Assert.Equal("7.1", Assert.Single(await repository.GetChannelsAsync()).GuideNumber);
        Assert.Single(await context.GuideImports.Where(import => import.CompletedUtc != null).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies root, channel, and programme supplemental metadata commits with an authoritative import.
    /// </summary>
    [Fact]
    public async Task ImportGuideAsync_SupplementalMetadata_RoundTripsCompleteSnapshot()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        const string programmeSupplementalXml = """<s:supplemental xmlns:s="urn:lineup:xmltv-supplemental:v1"><s:elements><keyword>metadata</keyword></s:elements></s:supplemental>""";
        var snapshot = new XmltvGuideSnapshot
        {
            SupplementalXml = "<root-metadata />",
            Segments =
            [
                new HDHomeRunChannelEpgSegment
                {
                    GuideNumber = "7.1",
                    GuideName = "Channel",
                    SupplementalXml = "<channel-metadata />",
                    Guide =
                    [
                        CreateProgram("Show", now, now.AddMinutes(30)) with { SupplementalXml = programmeSupplementalXml }
                    ]
                }
            ]
        };

        // Act
        await repository.ImportGuideAsync(snapshot, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);
        var result = await repository.GetGuideSnapshotAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(snapshot.SupplementalXml, result.SupplementalXml);
        var channel = Assert.Single(result.Segments);
        Assert.Equal("<channel-metadata />", channel.SupplementalXml);
        Assert.Equal(programmeSupplementalXml, Assert.Single(channel.Guide).SupplementalXml);
    }

    /// <summary>
    /// Verifies official and provider XMLTV metadata survives the complete database publication pipeline.
    /// </summary>
    [Fact]
    public async Task XmltvPipeline_SupplementalMetadata_SurvivesDatabasePublication()
    {
        // Arrange
        const string xml = """
            <tv source-info-name="Provider">
              <channel id="station"><display-name>Guide Name</display-name><lcn>7.1</lcn><url>https://example.test/channel</url></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station">
                <title>Show</title>
                <credits><director>Director</director></credits>
                <episode-num system="xmltv_ns">0.1.0/1</episode-num>
                <rating system="MPAA"><value>TV-14</value></rating>
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();
        var parsed = parser.Parse(Encoding.UTF8.GetBytes(xml));
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "7.1",
            GuideName = "Tuner Name",
            URL = "http://device/auto/v7.1"
        };
        var projected = new GuideSnapshotProjector().Project(parsed, [channel]);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();

        // Act
        await repository.ImportGuideAsync(projected, TimeSpan.FromDays(30), TestContext.Current.CancellationToken);
        var stored = await repository.GetGuideSnapshotAsync(cancellationToken: TestContext.Current.CancellationToken);
        var content = new LineupXmltvWriter().Write(stored, [channel], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        var document = XDocument.Parse(Encoding.UTF8.GetString(content));

        // Assert
        Assert.Equal("Provider", (string?)document.Root!.Attribute("source-info-name"));
        Assert.Equal("Lineup", (string?)document.Root.Attribute("generator-info-name"));
        Assert.Equal("https://example.test/channel", document.Root.Element("channel")?.Element("url")?.Value);
        var programme = Assert.Single(document.Root.Elements("programme"));
        Assert.Equal("Director", programme.Element("credits")?.Element("director")?.Value);
        Assert.Equal("0.1.0/1", programme.Elements("episode-num").Single().Value);
        Assert.Equal("TV-14", programme.Element("rating")?.Element("value")?.Value);
    }

    /// <summary>
    /// Verifies persisted supplemental programme metadata is projected into the structured read model.
    /// </summary>
    [Fact]
    public async Task GetProgramsAsync_OfficialSupplementalMetadata_ReturnsStructuredMetadata()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="station"><display-name>Guide Name</display-name><lcn>7.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station"
                         pdc-start="20260914215900 +0000" vps-start="20260914215800 +0000"
                         showview="123" videoplus="456" clumpidx="0/2">
                <title lang="en">Primary Show</title>
                <title lang="es">Programa Principal</title>
                <sub-title lang="en">Pilot</sub-title>
                <desc lang="en">Description</desc>
                <credits>
                  <director>Director Name</director>
                  <actor role="Lead" guest="yes">Actor Name</actor>
                  <writer>Writer Name</writer>
                </credits>
                <category lang="en">Drama</category>
                <keyword lang="en">mystery</keyword>
                <language lang="en">English</language>
                <orig-language>French</orig-language>
                <length units="minutes">30</length>
                <icon src="https://example.test/primary.png" />
                <icon src="https://example.test/alternate.png" width="320" height="180" />
                <url system="imdb">https://example.test/program</url>
                <country>US</country>
                <episode-num system="onscreen">S01E02</episode-num>
                <episode-num system="xmltv_ns">0.1.0/1</episode-num>
                <video><present>yes</present><colour>yes</colour><aspect>16:9</aspect><quality>HDTV</quality></video>
                <audio><present>yes</present><stereo>Dolby Digital</stereo></audio>
                <previously-shown start="20250914220000 +0000" channel="7.1" />
                <premiere lang="en">Series premiere</premiere>
                <last-chance lang="en">Final showing</last-chance>
                <subtitles type="onscreen"><language lang="es">Spanish</language></subtitles>
                <rating system="MPAA"><value>TV-14</value><icon src="https://example.test/rating.png" width="64" height="32" /></rating>
                <star-rating system="IMDB"><value>8/10</value></star-rating>
                <review type="text" source="Example" reviewer="Reviewer" lang="en">Review text</review>
                <image type="poster" size="3" orient="P" system="example" id="poster-1">https://example.test/poster.jpg</image>
                <new />
                <live />
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();
        var snapshot = parser.Parse(Encoding.UTF8.GetBytes(xml));
        var sourceSupplementalXml = Assert.Single(Assert.Single(snapshot.Segments).Guide).SupplementalXml;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.ImportGuideAsync(snapshot, TimeSpan.FromDays(36500), TestContext.Current.CancellationToken);

        // Act
        var programme = Assert.Single(await repository.GetProgramsAsync());

        // Assert
        Assert.Equal(sourceSupplementalXml, programme.SupplementalXml);
        var metadata = Assert.IsType<XmltvProgrammeMetadata>(programme.Metadata);
        Assert.Equal("en", metadata.PrimaryTitleLanguage);
        Assert.Equal("Programa Principal", Assert.Single(metadata.AlternateTitles).Value);
        Assert.Equal(["Director Name", "Actor Name", "Writer Name"], metadata.Credits.Select(credit => credit.Name));
        var actor = metadata.Credits.Single(credit => credit.Type == "actor");
        Assert.Equal("Lead", actor.Role);
        Assert.Equal("yes", actor.Guest);
        Assert.Equal(["onscreen", "xmltv_ns"], metadata.EpisodeNumbers.Select(number => number.System));
        Assert.Equal("TV-14", Assert.Single(metadata.Ratings).Value);
        Assert.Equal("64", Assert.Single(Assert.Single(metadata.Ratings).Icons).Width);
        Assert.Equal("8/10", Assert.Single(metadata.StarRatings).Value);
        Assert.Equal("English", Assert.Single(metadata.Languages).Value);
        Assert.Equal("French", Assert.Single(metadata.OriginalLanguages).Value);
        Assert.Equal("US", Assert.Single(metadata.Countries).Value);
        Assert.Equal("mystery", Assert.Single(metadata.Keywords).Value);
        Assert.Equal("30", metadata.Length?.Value);
        Assert.Equal("HDTV", metadata.Video?.Quality);
        Assert.Equal("Dolby Digital", metadata.Audio?.Stereo);
        Assert.Equal("Spanish", Assert.Single(metadata.Subtitles).Language?.Value);
        Assert.Equal("7.1", metadata.PreviouslyShown?.Channel);
        Assert.Equal("Series premiere", metadata.Premiere?.Value);
        Assert.Equal("Final showing", metadata.LastChance?.Value);
        Assert.True(metadata.IsNew);
        Assert.True(metadata.IsLive);
        Assert.Equal("https://example.test/program", Assert.Single(metadata.Urls).Value);
        Assert.Equal("Review text", Assert.Single(metadata.Reviews).Value);
        Assert.Equal("poster-1", Assert.Single(metadata.Images).Id);
        Assert.Equal("https://example.test/alternate.png", Assert.Single(metadata.AdditionalIcons).Source);
        Assert.Equal("20260914215900 +0000", metadata.PdcStart);
        Assert.Equal("20260914215800 +0000", metadata.VpsStart);
        Assert.Equal("123", metadata.ShowView);
        Assert.Equal("456", metadata.VideoPlus);
        Assert.Equal("0/2", metadata.ClumpIndex);
    }

    /// <summary>
    /// Verifies malformed persisted supplemental metadata fails programme materialization explicitly.
    /// </summary>
    [Fact]
    public async Task GetProgramsAsync_InvalidSupplementalXml_ThrowsInvalidDataException()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "7.1",
                GuideName = "Guide Name",
                Guide =
                [
                    new HDHomeRunProgram
                    {
                        Title = "Show",
                        StartTime = 100,
                        EndTime = 200,
                        SupplementalXml = "<invalid"
                    }
                ]
            }
        ]);

        // Act
        var action = () => repository.GetProgramsAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(action);
    }

    /// <summary>
    /// Verifies provider-only extensions remain lossless without creating an empty official metadata model.
    /// </summary>
    [Fact]
    public async Task GetProgramsAsync_ProviderExtensionOnly_PreservesXmlWithoutStructuredMetadata()
    {
        // Arrange
        const string supplementalXml = """<s:supplemental xmlns:s="urn:lineup:xmltv-supplemental:v1"><s:elements><provider-extension value="kept" /></s:elements></s:supplemental>""";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "7.1",
                GuideName = "Guide Name",
                Guide =
                [
                    new HDHomeRunProgram
                    {
                        Title = "Show",
                        StartTime = 100,
                        EndTime = 200,
                        SupplementalXml = supplementalXml
                    }
                ]
            }
        ]);

        // Act
        var programme = Assert.Single(await repository.GetProgramsAsync());

        // Assert
        Assert.Equal(supplementalXml, programme.SupplementalXml);
        Assert.Null(programme.Metadata);
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"PRAGMA table_info('{table}')";
        await using var reader = await schemaCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static HDHomeRunProgram CreateProgram(string title, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Title = title,
        StartTime = start.ToUnixTimeSeconds(),
        EndTime = end.ToUnixTimeSeconds()
    };
}

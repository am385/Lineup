using Bunit;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Components;
using Xunit;

namespace Lineup.Web.Tests.Components;

/// <summary>
/// Verifies structured XMLTV metadata presentation.
/// </summary>
public sealed class ProgrammeMetadataDetailsTests
{
    /// <summary>
    /// Verifies common programme metadata is immediately visible.
    /// </summary>
    [Fact]
    public void Metadata_CommonFields_RendersSummarySections()
    {
        // Arrange
        using var context = new BunitContext();
        var metadata = new XmltvProgrammeMetadata
        {
            Ratings = [new XmltvRating("TV-14", "MPAA", [])],
            StarRatings = [new XmltvRating("8/10", "IMDB", [])],
            Credits =
            [
                new XmltvCredit("director", "Director Name", null, null),
                new XmltvCredit("actor", "Actor Name", "Lead", "yes")
            ],
            Languages = [new XmltvLocalizedValue("English", "en")],
            Countries = [new XmltvLocalizedValue("US", null)],
            EpisodeNumbers =
            [
                new XmltvEpisodeNumber("S01E02", "onscreen"),
                new XmltvEpisodeNumber("0.1.0/1", "xmltv_ns")
            ],
            Premiere = new XmltvLocalizedValue("Series premiere", "en"),
            IsNew = true,
            IsLive = true
        };

        // Act
        var component = context.Render<ProgrammeMetadataDetails>(parameters => parameters.Add(item => item.Metadata, metadata));

        // Assert
        Assert.Contains("MPAA: TV-14", component.Markup);
        Assert.Contains("IMDB: 8/10", component.Markup);
        Assert.Contains("Director Name", component.Markup);
        Assert.Contains("Actor Name as Lead (guest)", component.Markup);
        Assert.Contains("English (en)", component.Markup);
        Assert.Contains("xmltv_ns", component.Markup);
        Assert.DoesNotContain("S01E02", component.Markup);
        Assert.Contains(">New<", component.Markup);
        Assert.Contains(">Live<", component.Markup);
        Assert.Contains("Premiere: Series premiere (en)", component.Markup);
    }

    /// <summary>
    /// Verifies less common metadata is grouped inside the advanced disclosure.
    /// </summary>
    [Fact]
    public void Metadata_AdvancedFields_RendersCollapsedDetails()
    {
        // Arrange
        using var context = new BunitContext();
        var metadata = new XmltvProgrammeMetadata
        {
            Credits = [new XmltvCredit("writer", "Writer Name", null, null)],
            Keywords = [new XmltvLocalizedValue("mystery", "en")],
            Length = new XmltvLength("30", "minutes"),
            Video = new XmltvVideoMetadata("yes", "yes", "16:9", "HDTV"),
            Audio = new XmltvAudioMetadata("yes", "Dolby Digital"),
            Subtitles = [new XmltvSubtitleMetadata("onscreen", new XmltvLocalizedValue("Spanish", "es"))],
            Reviews = [new XmltvReview("Review text", "text", "Example", "Reviewer", "en")],
            Urls = [new XmltvUrl("https://example.test/program", "imdb")],
            PdcStart = "20260914215900 +0000"
        };

        // Act
        var component = context.Render<ProgrammeMetadataDetails>(parameters => parameters.Add(item => item.Metadata, metadata));

        // Assert
        var details = component.Find("details.programme-metadata-advanced");
        Assert.False(details.HasAttribute("open"));
        Assert.Equal("More details", details.QuerySelector("summary")?.TextContent.Trim());
        Assert.Contains("Writer: Writer Name", details.TextContent);
        Assert.Contains("mystery (en)", details.TextContent);
        Assert.Contains("30 minutes", details.TextContent);
        Assert.Contains("Quality: HDTV", details.TextContent);
        Assert.Contains("Stereo: Dolby Digital", details.TextContent);
        Assert.Contains("onscreen: Spanish (es)", details.TextContent);
        Assert.Contains("Review text (Example, Reviewer)", details.TextContent);
        Assert.Contains("https://example.test/program (imdb)", details.TextContent);
        Assert.Contains("PDC: 20260914215900 +0000", details.TextContent);
    }

    /// <summary>
    /// Verifies empty metadata renders nothing and provider text is encoded when present.
    /// </summary>
    [Fact]
    public void Metadata_EmptyAndProviderText_HidesEmptyStateAndEncodesText()
    {
        // Arrange
        using var context = new BunitContext();
        var emptyComponent = context.Render<ProgrammeMetadataDetails>(parameters => parameters.Add(item => item.Metadata, new XmltvProgrammeMetadata()));
        var metadata = new XmltvProgrammeMetadata
        {
            Reviews = [new XmltvReview("<script>alert('x')</script>", "text", null, null, null)]
        };

        // Act
        var populatedComponent = context.Render<ProgrammeMetadataDetails>(parameters => parameters.Add(item => item.Metadata, metadata));

        // Assert
        Assert.Equal(string.Empty, emptyComponent.Markup);
        Assert.DoesNotContain("<script>", populatedComponent.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", populatedComponent.Markup);
        Assert.Contains("<script>alert('x')</script>", populatedComponent.Find("section").TextContent);
    }
}

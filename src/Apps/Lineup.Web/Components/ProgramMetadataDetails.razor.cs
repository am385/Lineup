using Microsoft.AspNetCore.Components;

namespace Lineup.Web.Components;

/// <summary>
/// Displays structured XMLTV program metadata.
/// </summary>
public partial class ProgramMetadataDetails
{
    /// <summary>
    /// Gets or sets the structured program metadata to display.
    /// </summary>
    [Parameter]
    public XmltvProgramMetadata? Metadata { get; set; }

    private static bool HasVisibleMetadata(XmltvProgramMetadata metadata) =>
        metadata.Ratings.Count > 0 ||
        metadata.StarRatings.Count > 0 ||
        CommonCredits(metadata).Count > 0 ||
        metadata.Languages.Count > 0 ||
        metadata.Countries.Count > 0 ||
        AlternateEpisodeNumbers(metadata).Count > 0 ||
        HasProgramState(metadata) ||
        HasAdvancedMetadata(metadata);

    private static bool HasProgramState(XmltvProgramMetadata metadata) =>
        metadata.IsNew || metadata.IsLive || metadata.Premiere != null || metadata.PreviouslyShown != null;

    private static bool HasAdvancedMetadata(XmltvProgramMetadata metadata) =>
        AdditionalCredits(metadata).Count > 0 ||
        metadata.OriginalLanguages.Count > 0 ||
        metadata.Keywords.Count > 0 ||
        metadata.AlternateTitles.Count > 0 ||
        metadata.AlternateSubTitles.Count > 0 ||
        metadata.AlternateDescriptions.Count > 0 ||
        LocalizedCategories(metadata).Count > 0 ||
        metadata.Length != null ||
        metadata.Video != null ||
        metadata.Audio != null ||
        metadata.Subtitles.Count > 0 ||
        metadata.LastChance != null ||
        metadata.Reviews.Count > 0 ||
        metadata.Urls.Count > 0 ||
        metadata.Images.Count > 0 ||
        metadata.AdditionalIcons.Count > 0 ||
        HasLanguageMetadata(metadata) ||
        HasSchedulingMetadata(metadata);

    private static IReadOnlyList<XmltvCredit> CommonCredits(XmltvProgramMetadata metadata) =>
        metadata.Credits.Where(credit => credit.Type is "director" or "actor").ToArray();

    private static IReadOnlyList<XmltvCredit> AdditionalCredits(XmltvProgramMetadata metadata) =>
        metadata.Credits.Where(credit => credit.Type is not ("director" or "actor")).ToArray();

    private static IReadOnlyList<XmltvEpisodeNumber> AlternateEpisodeNumbers(XmltvProgramMetadata metadata) =>
        metadata.EpisodeNumbers.Where(number => !string.Equals(number.System, "onscreen", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static IReadOnlyList<XmltvLocalizedValue> LocalizedCategories(XmltvProgramMetadata metadata) =>
        metadata.Categories.Where(category => !string.IsNullOrWhiteSpace(category.Language)).ToArray();

    private static string CreditLabel(string type) => type switch
    {
        "director" => "Director",
        "actor" => "Cast",
        "writer" => "Writer",
        "adapter" => "Adapter",
        "producer" => "Producer",
        "composer" => "Composer",
        "editor" => "Editor",
        "presenter" => "Presenter",
        "commentator" => "Commentator",
        "guest" => "Guest",
        _ => type
    };

    private static string FormatCredit(XmltvCredit credit)
    {
        var role = string.IsNullOrWhiteSpace(credit.Role) ? string.Empty : $" as {credit.Role}";
        var guest = string.Equals(credit.Guest, "yes", StringComparison.OrdinalIgnoreCase) ? " (guest)" : string.Empty;
        return $"{credit.Name}{role}{guest}";
    }

    private static string FormatRating(XmltvRating rating) =>
        string.IsNullOrWhiteSpace(rating.System) ? rating.Value : $"{rating.System}: {rating.Value}";

    private static string JoinLocalized(IEnumerable<XmltvLocalizedValue> values) =>
        string.Join(", ", values.Select(FormatLocalized));

    private static string FormatLocalized(XmltvLocalizedValue value) =>
        string.IsNullOrWhiteSpace(value.Language) ? value.Value : $"{value.Value} ({value.Language})";

    private static string FormatFlag(string label, XmltvLocalizedValue value) =>
        string.IsNullOrWhiteSpace(value.Value) ? label : $"{label}: {FormatLocalized(value)}";

    private static string FormatPreviousShowing(XmltvPreviousShowing showing)
    {
        var details = new[] { showing.Start, showing.Channel }.Where(value => !string.IsNullOrWhiteSpace(value));
        var suffix = string.Join(", ", details);
        return suffix.Length == 0 ? "Previously shown" : $"Previously shown: {suffix}";
    }

    private static string FormatLength(XmltvLength length) =>
        string.IsNullOrWhiteSpace(length.Units) ? length.Value : $"{length.Value} {length.Units}";

    private static string FormatVideo(XmltvVideoMetadata video) =>
        JoinPresentValues(
            ("Present", video.Present),
            ("Color", video.Color),
            ("Aspect", video.Aspect),
            ("Quality", video.Quality));

    private static string FormatAudio(XmltvAudioMetadata audio) =>
        JoinPresentValues(("Present", audio.Present), ("Stereo", audio.Stereo));

    private static string FormatSubtitles(XmltvSubtitleMetadata subtitles)
    {
        var type = string.IsNullOrWhiteSpace(subtitles.Type) ? "Subtitles" : subtitles.Type;
        return subtitles.Language == null ? type : $"{type}: {FormatLocalized(subtitles.Language)}";
    }

    private static string FormatReview(XmltvReview review)
    {
        var attribution = new[] { review.Source, review.Reviewer }.Where(value => !string.IsNullOrWhiteSpace(value));
        var suffix = string.Join(", ", attribution);
        return suffix.Length == 0 ? review.Value : $"{review.Value} ({suffix})";
    }

    private static string FormatUrl(XmltvUrl url) =>
        string.IsNullOrWhiteSpace(url.System) ? url.Value : $"{url.Value} ({url.System})";

    private static string FormatImage(XmltvImage image) =>
        string.IsNullOrWhiteSpace(image.Type) ? image.Value : $"{image.Value} ({image.Type})";

    private static bool HasLanguageMetadata(XmltvProgramMetadata metadata) =>
        metadata.PrimaryTitleLanguage != null || metadata.PrimarySubTitleLanguage != null || metadata.PrimaryDescriptionLanguage != null;

    private static string FormatPrimaryLanguages(XmltvProgramMetadata metadata) =>
        JoinPresentValues(
            ("Title", metadata.PrimaryTitleLanguage),
            ("Episode title", metadata.PrimarySubTitleLanguage),
            ("Description", metadata.PrimaryDescriptionLanguage));

    private static bool HasSchedulingMetadata(XmltvProgramMetadata metadata) =>
        metadata.PdcStart != null || metadata.VpsStart != null || metadata.ShowView != null || metadata.VideoPlus != null || metadata.ClumpIndex != null;

    private static string FormatSchedulingMetadata(XmltvProgramMetadata metadata) =>
        JoinPresentValues(
            ("PDC", metadata.PdcStart),
            ("VPS", metadata.VpsStart),
            ("ShowView", metadata.ShowView),
            ("VideoPlus", metadata.VideoPlus),
            ("Clump", metadata.ClumpIndex));

    private static string JoinPresentValues(params (string Label, string? Value)[] values) =>
        string.Join(", ", values.Where(item => !string.IsNullOrWhiteSpace(item.Value)).Select(item => $"{item.Label}: {item.Value}"));
}

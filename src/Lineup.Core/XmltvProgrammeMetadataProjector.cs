using System.Xml.Linq;
using Lineup.HDHomeRun.Api.Models;

namespace Lineup.Core;

/// <summary>
/// Projects persisted supplemental XMLTV programme metadata into a structured read model.
/// </summary>
internal static class XmltvProgrammeMetadataProjector
{
    private static readonly HashSet<string> CreditNames =
    [
        "director",
        "actor",
        "writer",
        "adapter",
        "producer",
        "composer",
        "editor",
        "presenter",
        "commentator",
        "guest"
    ];

    /// <summary>
    /// Projects a persisted supplemental envelope, returning <see langword="null"/> when it contains no recognized official programme metadata.
    /// </summary>
    public static XmltvProgrammeMetadata? Project(string? content)
    {
        var supplemental = XmltvSupplementalMetadata.Parse(content);
        if (supplemental == null)
        {
            return null;
        }

        var elements = supplemental.Elements.Where(IsUnqualified).ToArray();
        var credits = Elements(elements, "credits")
            .SelectMany(element => element.Elements().Where(child => IsUnqualified(child) && CreditNames.Contains(child.Name.LocalName)))
            .Select(element => new XmltvCredit(
                element.Name.LocalName,
                element.Value.Trim(),
                AttributeValue(element, "role"),
                AttributeValue(element, "guest")))
            .ToArray();
        var episodeNumbers = Elements(elements, "episode-num")
            .Select(element => new XmltvEpisodeNumber(element.Value.Trim(), AttributeValue(element, "system")))
            .ToArray();
        var ratings = Elements(elements, "rating").Select(CreateRating).ToArray();
        var starRatings = Elements(elements, "star-rating").Select(CreateRating).ToArray();
        var languages = CreateLocalizedValues(elements, "language");
        var originalLanguages = CreateLocalizedValues(elements, "orig-language");
        var countries = CreateLocalizedValues(elements, "country");
        var keywords = CreateLocalizedValues(elements, "keyword");
        var alternateTitles = CreateLocalizedValues(elements, "title");
        var alternateSubTitles = CreateLocalizedValues(elements, "sub-title");
        var alternateDescriptions = CreateLocalizedValues(elements, "desc");
        var categories = CreateLocalizedValues(elements, "category");
        var urls = Elements(elements, "url")
            .Select(element => new XmltvUrl(element.Value.Trim(), AttributeValue(element, "system")))
            .ToArray();
        var reviews = Elements(elements, "review")
            .Select(element => new XmltvReview(
                element.Value.Trim(),
                AttributeValue(element, "type"),
                AttributeValue(element, "source"),
                AttributeValue(element, "reviewer"),
                Language(element)))
            .ToArray();
        var images = Elements(elements, "image")
            .Select(element => new XmltvImage(
                element.Value.Trim(),
                AttributeValue(element, "type"),
                AttributeValue(element, "size"),
                AttributeValue(element, "orient"),
                AttributeValue(element, "system"),
                AttributeValue(element, "id")))
            .ToArray();
        var additionalIcons = Elements(elements, "icon").Select(CreateIcon).ToArray();
        var lengthElement = Elements(elements, "length").FirstOrDefault();
        var length = lengthElement == null ? null : new XmltvLength(lengthElement.Value.Trim(), AttributeValue(lengthElement, "units"));
        var video = CreateVideo(Elements(elements, "video").FirstOrDefault());
        var audio = CreateAudio(Elements(elements, "audio").FirstOrDefault());
        var subtitles = Elements(elements, "subtitles").Select(CreateSubtitles).ToArray();
        var previouslyShownElement = Elements(elements, "previously-shown").FirstOrDefault();
        var previouslyShown = previouslyShownElement == null
            ? null
            : new XmltvPreviousShowing(AttributeValue(previouslyShownElement, "start"), AttributeValue(previouslyShownElement, "channel"));
        var premiere = CreateLocalizedValue(Elements(elements, "premiere").FirstOrDefault());
        var lastChance = CreateLocalizedValue(Elements(elements, "last-chance").FirstOrDefault());
        var primaryTitleLanguage = OwnedLanguage(supplemental, "title");
        var primarySubTitleLanguage = OwnedLanguage(supplemental, "sub-title");
        var primaryDescriptionLanguage = OwnedLanguage(supplemental, "desc");
        var pdcStart = AttributeValue(supplemental.Attributes, "pdc-start");
        var vpsStart = AttributeValue(supplemental.Attributes, "vps-start");
        var showView = AttributeValue(supplemental.Attributes, "showview");
        var videoPlus = AttributeValue(supplemental.Attributes, "videoplus");
        var clumpIndex = AttributeValue(supplemental.Attributes, "clumpidx");
        var isNew = Elements(elements, "new").Any();
        var isLive = Elements(elements, "live").Any();

        if (credits.Length == 0 &&
            episodeNumbers.Length == 0 &&
            ratings.Length == 0 &&
            starRatings.Length == 0 &&
            languages.Count == 0 &&
            originalLanguages.Count == 0 &&
            countries.Count == 0 &&
            keywords.Count == 0 &&
            alternateTitles.Count == 0 &&
            alternateSubTitles.Count == 0 &&
            alternateDescriptions.Count == 0 &&
            categories.Count == 0 &&
            urls.Length == 0 &&
            reviews.Length == 0 &&
            images.Length == 0 &&
            additionalIcons.Length == 0 &&
            length == null &&
            video == null &&
            audio == null &&
            subtitles.Length == 0 &&
            previouslyShown == null &&
            premiere == null &&
            lastChance == null &&
            !isNew &&
            !isLive &&
            primaryTitleLanguage == null &&
            primarySubTitleLanguage == null &&
            primaryDescriptionLanguage == null &&
            pdcStart == null &&
            vpsStart == null &&
            showView == null &&
            videoPlus == null &&
            clumpIndex == null)
        {
            return null;
        }

        return new XmltvProgrammeMetadata
        {
            Credits = credits,
            EpisodeNumbers = episodeNumbers,
            Ratings = ratings,
            StarRatings = starRatings,
            Languages = languages,
            OriginalLanguages = originalLanguages,
            Countries = countries,
            Keywords = keywords,
            AlternateTitles = alternateTitles,
            AlternateSubTitles = alternateSubTitles,
            AlternateDescriptions = alternateDescriptions,
            Categories = categories,
            Urls = urls,
            Reviews = reviews,
            Images = images,
            AdditionalIcons = additionalIcons,
            Length = length,
            Video = video,
            Audio = audio,
            Subtitles = subtitles,
            PreviouslyShown = previouslyShown,
            Premiere = premiere,
            LastChance = lastChance,
            IsNew = isNew,
            IsLive = isLive,
            PrimaryTitleLanguage = primaryTitleLanguage,
            PrimarySubTitleLanguage = primarySubTitleLanguage,
            PrimaryDescriptionLanguage = primaryDescriptionLanguage,
            PdcStart = pdcStart,
            VpsStart = vpsStart,
            ShowView = showView,
            VideoPlus = videoPlus,
            ClumpIndex = clumpIndex
        };
    }

    private static IEnumerable<XElement> Elements(IEnumerable<XElement> elements, string name) =>
        elements.Where(element => string.Equals(element.Name.LocalName, name, StringComparison.Ordinal));

    private static IReadOnlyList<XmltvLocalizedValue> CreateLocalizedValues(IEnumerable<XElement> elements, string name) =>
        Elements(elements, name).Select(element => new XmltvLocalizedValue(element.Value.Trim(), Language(element))).ToArray();

    private static XmltvLocalizedValue? CreateLocalizedValue(XElement? element) =>
        element == null ? null : new XmltvLocalizedValue(element.Value.Trim(), Language(element));

    private static XmltvRating CreateRating(XElement element)
    {
        var value = element.Elements().FirstOrDefault(child => IsUnqualified(child) && child.Name.LocalName == "value")?.Value.Trim() ?? string.Empty;
        var icons = element.Elements().Where(child => IsUnqualified(child) && child.Name.LocalName == "icon").Select(CreateIcon).ToArray();
        return new XmltvRating(value, AttributeValue(element, "system"), icons);
    }

    private static XmltvMetadataIcon CreateIcon(XElement element) =>
        new(AttributeValue(element, "src") ?? string.Empty, AttributeValue(element, "width"), AttributeValue(element, "height"));

    private static XmltvVideoMetadata? CreateVideo(XElement? element)
    {
        if (element == null)
        {
            return null;
        }
        return new XmltvVideoMetadata(
            ChildValue(element, "present"),
            ChildValue(element, "colour"),
            ChildValue(element, "aspect"),
            ChildValue(element, "quality"));
    }

    private static XmltvAudioMetadata? CreateAudio(XElement? element)
    {
        if (element == null)
        {
            return null;
        }
        return new XmltvAudioMetadata(ChildValue(element, "present"), ChildValue(element, "stereo"));
    }

    private static XmltvSubtitleMetadata CreateSubtitles(XElement element)
    {
        var language = element.Elements().FirstOrDefault(child => IsUnqualified(child) && child.Name.LocalName == "language");
        return new XmltvSubtitleMetadata(AttributeValue(element, "type"), CreateLocalizedValue(language));
    }

    private static string? ChildValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => IsUnqualified(child) && child.Name.LocalName == name)?.Value.Trim();

    private static string? OwnedLanguage(XmltvSupplementalMetadata supplemental, string key)
    {
        var element = supplemental.GetOwnedElement(key);
        return element == null ? null : Language(element);
    }

    private static string? Language(XElement element) =>
        element.Attribute(XNamespace.Xml + "lang")?.Value ?? AttributeValue(element, "lang");

    private static string? AttributeValue(XElement element, string name) =>
        AttributeValue(element.Attributes(), name);

    private static string? AttributeValue(IEnumerable<XAttribute> attributes, string name) =>
        attributes.FirstOrDefault(attribute => attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName == name)?.Value;

    private static bool IsUnqualified(XElement element) => element.Name.NamespaceName.Length == 0;
}

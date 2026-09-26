namespace Lineup.HDHomeRun.Api.Models;

/// <summary>
/// Represents structured official XMLTV metadata derived from a programme's supplemental metadata.
/// </summary>
public sealed record XmltvProgrammeMetadata
{
    /// <summary>
    /// Gets programme credits in source order.
    /// </summary>
    public IReadOnlyList<XmltvCredit> Credits { get; init; } = [];

    /// <summary>
    /// Gets episode identifiers in source order.
    /// </summary>
    public IReadOnlyList<XmltvEpisodeNumber> EpisodeNumbers { get; init; } = [];

    /// <summary>
    /// Gets content ratings in source order.
    /// </summary>
    public IReadOnlyList<XmltvRating> Ratings { get; init; } = [];

    /// <summary>
    /// Gets star ratings in source order.
    /// </summary>
    public IReadOnlyList<XmltvRating> StarRatings { get; init; } = [];

    /// <summary>
    /// Gets programme languages.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> Languages { get; init; } = [];

    /// <summary>
    /// Gets original programme languages.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> OriginalLanguages { get; init; } = [];

    /// <summary>
    /// Gets countries of origin.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> Countries { get; init; } = [];

    /// <summary>
    /// Gets programme keywords.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> Keywords { get; init; } = [];

    /// <summary>
    /// Gets alternate programme titles.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateTitles { get; init; } = [];

    /// <summary>
    /// Gets alternate episode titles.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateSubTitles { get; init; } = [];

    /// <summary>
    /// Gets alternate programme descriptions.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateDescriptions { get; init; } = [];

    /// <summary>
    /// Gets categorized programme values, including localization metadata.
    /// </summary>
    public IReadOnlyList<XmltvLocalizedValue> Categories { get; init; } = [];

    /// <summary>
    /// Gets programme URLs.
    /// </summary>
    public IReadOnlyList<XmltvUrl> Urls { get; init; } = [];

    /// <summary>
    /// Gets programme reviews.
    /// </summary>
    public IReadOnlyList<XmltvReview> Reviews { get; init; } = [];

    /// <summary>
    /// Gets programme images not represented by the primary image property.
    /// </summary>
    public IReadOnlyList<XmltvImage> Images { get; init; } = [];

    /// <summary>
    /// Gets additional programme icons.
    /// </summary>
    public IReadOnlyList<XmltvMetadataIcon> AdditionalIcons { get; init; } = [];

    /// <summary>
    /// Gets the declared programme length.
    /// </summary>
    public XmltvLength? Length { get; init; }

    /// <summary>
    /// Gets video metadata.
    /// </summary>
    public XmltvVideoMetadata? Video { get; init; }

    /// <summary>
    /// Gets audio metadata.
    /// </summary>
    public XmltvAudioMetadata? Audio { get; init; }

    /// <summary>
    /// Gets subtitle metadata.
    /// </summary>
    public IReadOnlyList<XmltvSubtitleMetadata> Subtitles { get; init; } = [];

    /// <summary>
    /// Gets information about a previous showing.
    /// </summary>
    public XmltvPreviousShowing? PreviouslyShown { get; init; }

    /// <summary>
    /// Gets premiere text and localization metadata.
    /// </summary>
    public XmltvLocalizedValue? Premiere { get; init; }

    /// <summary>
    /// Gets last-chance text and localization metadata.
    /// </summary>
    public XmltvLocalizedValue? LastChance { get; init; }

    /// <summary>
    /// Gets whether the programme is marked as new.
    /// </summary>
    public bool IsNew { get; init; }

    /// <summary>
    /// Gets whether the programme is marked as live.
    /// </summary>
    public bool IsLive { get; init; }

    /// <summary>
    /// Gets the language of the primary title.
    /// </summary>
    public string? PrimaryTitleLanguage { get; init; }

    /// <summary>
    /// Gets the language of the primary episode title.
    /// </summary>
    public string? PrimarySubTitleLanguage { get; init; }

    /// <summary>
    /// Gets the language of the primary description.
    /// </summary>
    public string? PrimaryDescriptionLanguage { get; init; }

    /// <summary>
    /// Gets the programme's PDC start value.
    /// </summary>
    public string? PdcStart { get; init; }

    /// <summary>
    /// Gets the programme's VPS start value.
    /// </summary>
    public string? VpsStart { get; init; }

    /// <summary>
    /// Gets the programme's ShowView identifier.
    /// </summary>
    public string? ShowView { get; init; }

    /// <summary>
    /// Gets the programme's VideoPlus identifier.
    /// </summary>
    public string? VideoPlus { get; init; }

    /// <summary>
    /// Gets the programme's clump index.
    /// </summary>
    public string? ClumpIndex { get; init; }
}

/// <summary>
/// Represents a credited person and their XMLTV credit type.
/// </summary>
/// <param name="Type">The XMLTV credit element name.</param>
/// <param name="Name">The credited person's name.</param>
/// <param name="Role">The actor role, when supplied.</param>
/// <param name="Guest">The actor guest marker, when supplied.</param>
public sealed record XmltvCredit(string Type, string Name, string? Role, string? Guest);

/// <summary>
/// Represents an XMLTV value with optional language metadata.
/// </summary>
/// <param name="Value">The value.</param>
/// <param name="Language">The XML language code, when supplied.</param>
public sealed record XmltvLocalizedValue(string Value, string? Language);

/// <summary>
/// Represents an XMLTV episode identifier.
/// </summary>
/// <param name="Value">The identifier.</param>
/// <param name="System">The identifier system, when supplied.</param>
public sealed record XmltvEpisodeNumber(string Value, string? System);

/// <summary>
/// Represents an XMLTV content or star rating.
/// </summary>
/// <param name="Value">The rating value.</param>
/// <param name="System">The rating system, when supplied.</param>
/// <param name="Icons">The rating icons.</param>
public sealed record XmltvRating(string Value, string? System, IReadOnlyList<XmltvMetadataIcon> Icons);

/// <summary>
/// Represents an XMLTV icon.
/// </summary>
/// <param name="Source">The icon source.</param>
/// <param name="Width">The provider-supplied width.</param>
/// <param name="Height">The provider-supplied height.</param>
public sealed record XmltvMetadataIcon(string Source, string? Width, string? Height);

/// <summary>
/// Represents an XMLTV URL.
/// </summary>
/// <param name="Value">The URL text.</param>
/// <param name="System">The URL system, when supplied.</param>
public sealed record XmltvUrl(string Value, string? System);

/// <summary>
/// Represents an XMLTV review.
/// </summary>
/// <param name="Value">The review text or URL.</param>
/// <param name="Type">The review type, when supplied.</param>
/// <param name="Source">The review source, when supplied.</param>
/// <param name="Reviewer">The reviewer, when supplied.</param>
/// <param name="Language">The review language, when supplied.</param>
public sealed record XmltvReview(string Value, string? Type, string? Source, string? Reviewer, string? Language);

/// <summary>
/// Represents an XMLTV image.
/// </summary>
/// <param name="Value">The image location.</param>
/// <param name="Type">The image type, when supplied.</param>
/// <param name="Size">The image size, when supplied.</param>
/// <param name="Orientation">The image orientation, when supplied.</param>
/// <param name="System">The image system, when supplied.</param>
/// <param name="Id">The provider image identifier, when supplied.</param>
public sealed record XmltvImage(string Value, string? Type, string? Size, string? Orientation, string? System, string? Id);

/// <summary>
/// Represents an XMLTV programme length.
/// </summary>
/// <param name="Value">The provider-supplied length.</param>
/// <param name="Units">The provider-supplied units.</param>
public sealed record XmltvLength(string Value, string? Units);

/// <summary>
/// Represents XMLTV video metadata.
/// </summary>
/// <param name="Present">The provider-supplied present value.</param>
/// <param name="Colour">The provider-supplied colour value.</param>
/// <param name="Aspect">The provider-supplied aspect ratio.</param>
/// <param name="Quality">The provider-supplied quality.</param>
public sealed record XmltvVideoMetadata(string? Present, string? Colour, string? Aspect, string? Quality);

/// <summary>
/// Represents XMLTV audio metadata.
/// </summary>
/// <param name="Present">The provider-supplied present value.</param>
/// <param name="Stereo">The provider-supplied stereo value.</param>
public sealed record XmltvAudioMetadata(string? Present, string? Stereo);

/// <summary>
/// Represents XMLTV subtitle metadata.
/// </summary>
/// <param name="Type">The subtitle type, when supplied.</param>
/// <param name="Language">The subtitle language, when supplied.</param>
public sealed record XmltvSubtitleMetadata(string? Type, XmltvLocalizedValue? Language);

/// <summary>
/// Represents a previous XMLTV showing.
/// </summary>
/// <param name="Start">The provider-supplied start value.</param>
/// <param name="Channel">The provider-supplied channel value.</param>
public sealed record XmltvPreviousShowing(string? Start, string? Channel);

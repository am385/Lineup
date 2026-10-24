namespace Lineup.Common.Guides;

/// <summary>
/// Represents structured official XMLTV metadata derived from a program's supplemental metadata.
/// </summary>
public sealed record XmltvProgramMetadata
{
    /// <summary>Gets program credits in source order.</summary>
    public IReadOnlyList<XmltvCredit> Credits { get; init; } = [];

    /// <summary>Gets episode identifiers in source order.</summary>
    public IReadOnlyList<XmltvEpisodeNumber> EpisodeNumbers { get; init; } = [];

    /// <summary>Gets content ratings in source order.</summary>
    public IReadOnlyList<XmltvRating> Ratings { get; init; } = [];

    /// <summary>Gets star ratings in source order.</summary>
    public IReadOnlyList<XmltvRating> StarRatings { get; init; } = [];

    /// <summary>Gets program languages.</summary>
    public IReadOnlyList<XmltvLocalizedValue> Languages { get; init; } = [];

    /// <summary>Gets original program languages.</summary>
    public IReadOnlyList<XmltvLocalizedValue> OriginalLanguages { get; init; } = [];

    /// <summary>Gets countries of origin.</summary>
    public IReadOnlyList<XmltvLocalizedValue> Countries { get; init; } = [];

    /// <summary>Gets program keywords.</summary>
    public IReadOnlyList<XmltvLocalizedValue> Keywords { get; init; } = [];

    /// <summary>Gets alternate program titles.</summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateTitles { get; init; } = [];

    /// <summary>Gets alternate episode titles.</summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateSubTitles { get; init; } = [];

    /// <summary>Gets alternate program descriptions.</summary>
    public IReadOnlyList<XmltvLocalizedValue> AlternateDescriptions { get; init; } = [];

    /// <summary>Gets categorized program values.</summary>
    public IReadOnlyList<XmltvLocalizedValue> Categories { get; init; } = [];

    /// <summary>Gets program URLs.</summary>
    public IReadOnlyList<XmltvUrl> Urls { get; init; } = [];

    /// <summary>Gets program reviews.</summary>
    public IReadOnlyList<XmltvReview> Reviews { get; init; } = [];

    /// <summary>Gets program images not represented by the primary image property.</summary>
    public IReadOnlyList<XmltvImage> Images { get; init; } = [];

    /// <summary>Gets additional program icons.</summary>
    public IReadOnlyList<XmltvMetadataIcon> AdditionalIcons { get; init; } = [];

    /// <summary>Gets the declared program length.</summary>
    public XmltvLength? Length { get; init; }

    /// <summary>Gets video metadata.</summary>
    public XmltvVideoMetadata? Video { get; init; }

    /// <summary>Gets audio metadata.</summary>
    public XmltvAudioMetadata? Audio { get; init; }

    /// <summary>Gets subtitle metadata.</summary>
    public IReadOnlyList<XmltvSubtitleMetadata> Subtitles { get; init; } = [];

    /// <summary>Gets information about a previous showing.</summary>
    public XmltvPreviousShowing? PreviouslyShown { get; init; }

    /// <summary>Gets premiere text and localization metadata.</summary>
    public XmltvLocalizedValue? Premiere { get; init; }

    /// <summary>Gets last-chance text and localization metadata.</summary>
    public XmltvLocalizedValue? LastChance { get; init; }

    /// <summary>Gets whether the program is marked as new.</summary>
    public bool IsNew { get; init; }

    /// <summary>Gets whether the program is marked as live.</summary>
    public bool IsLive { get; init; }

    /// <summary>Gets the language of the primary title.</summary>
    public string? PrimaryTitleLanguage { get; init; }

    /// <summary>Gets the language of the primary episode title.</summary>
    public string? PrimarySubTitleLanguage { get; init; }

    /// <summary>Gets the language of the primary description.</summary>
    public string? PrimaryDescriptionLanguage { get; init; }

    /// <summary>Gets the program's PDC start value.</summary>
    public string? PdcStart { get; init; }

    /// <summary>Gets the program's VPS start value.</summary>
    public string? VpsStart { get; init; }

    /// <summary>Gets the program's ShowView identifier.</summary>
    public string? ShowView { get; init; }

    /// <summary>Gets the program's VideoPlus identifier.</summary>
    public string? VideoPlus { get; init; }

    /// <summary>Gets the program's clump index.</summary>
    public string? ClumpIndex { get; init; }
}

/// <summary>Represents a credited person and XMLTV credit type.</summary>
public sealed record XmltvCredit(string Type, string Name, string? Role, string? Guest);

/// <summary>Represents an XMLTV value with optional language metadata.</summary>
public sealed record XmltvLocalizedValue(string Value, string? Language);

/// <summary>Represents an XMLTV episode identifier.</summary>
public sealed record XmltvEpisodeNumber(string Value, string? System);

/// <summary>Represents an XMLTV content or star rating.</summary>
public sealed record XmltvRating(string Value, string? System, IReadOnlyList<XmltvMetadataIcon> Icons);

/// <summary>Represents an XMLTV icon.</summary>
public sealed record XmltvMetadataIcon(string Source, string? Width, string? Height);

/// <summary>Represents an XMLTV URL.</summary>
public sealed record XmltvUrl(string Value, string? System);

/// <summary>Represents an XMLTV review.</summary>
public sealed record XmltvReview(string Value, string? Type, string? Source, string? Reviewer, string? Language);

/// <summary>Represents an XMLTV image.</summary>
public sealed record XmltvImage(string Value, string? Type, string? Size, string? Orientation, string? System, string? Id);

/// <summary>Represents an XMLTV program length.</summary>
public sealed record XmltvLength(string Value, string? Units);

/// <summary>Represents XMLTV video metadata.</summary>
public sealed record XmltvVideoMetadata(string? Present, string? Color, string? Aspect, string? Quality);

/// <summary>Represents XMLTV audio metadata.</summary>
public sealed record XmltvAudioMetadata(string? Present, string? Stereo);

/// <summary>Represents XMLTV subtitle metadata.</summary>
public sealed record XmltvSubtitleMetadata(string? Type, XmltvLocalizedValue? Language);

/// <summary>Represents a previous XMLTV showing.</summary>
public sealed record XmltvPreviousShowing(string? Start, string? Channel);

namespace Lineup.Web.Components.Channels;

/// <summary>Identifies an available physical-device filter option.</summary>
/// <param name="Value">The serialized physical-device identifier.</param>
/// <param name="Label">The device display name.</param>
public sealed record ChannelDeviceFilterOption(string Value, string Label);

/// <summary>Filters channels by enabled state.</summary>
public enum ChannelStateFilter
{
    /// <summary>Includes every channel.</summary>
    All,

    /// <summary>Includes enabled channels.</summary>
    Enabled,

    /// <summary>Includes disabled channels.</summary>
    Disabled
}

/// <summary>Filters channels by provider flags.</summary>
public enum ChannelFlagFilter
{
    /// <summary>Includes every channel.</summary>
    All,

    /// <summary>Includes favorite channels.</summary>
    Favorite,

    /// <summary>Includes DRM-protected channels.</summary>
    Drm,

    /// <summary>Includes HD channels.</summary>
    Hd
}

/// <summary>Filters channels by guide-binding state.</summary>
public enum GuideBindingFilter
{
    /// <summary>Includes every guide-binding state.</summary>
    All,

    /// <summary>Includes channels with authoritative guide bindings.</summary>
    Bound,

    /// <summary>Includes channels without guide bindings.</summary>
    Unbound,

    /// <summary>Includes channels with ambiguous automatic matches.</summary>
    Ambiguous,

    /// <summary>Includes channels without an automatic match.</summary>
    NoMatch
}

/// <summary>Identifies a sortable channel table column.</summary>
public enum ChannelSortColumn
{
    /// <summary>Sorts by enabled state.</summary>
    Enabled,
    /// <summary>Sorts by physical device.</summary>
    Device,
    /// <summary>Sorts by provider identity.</summary>
    ProviderIdentity,
    /// <summary>Sorts by global channel number.</summary>
    GlobalNumber,
    /// <summary>Sorts by global channel name.</summary>
    GlobalName,
    /// <summary>Sorts by equivalence group.</summary>
    EquivalenceGroup,
    /// <summary>Sorts by authoritative guide.</summary>
    Guide,
    /// <summary>Sorts by provider flags.</summary>
    Flags,
    /// <summary>Sorts by video codec.</summary>
    Video,
    /// <summary>Sorts by audio codec.</summary>
    Audio,
    /// <summary>Sorts by signal strength.</summary>
    Signal,
    /// <summary>Sorts by signal quality.</summary>
    Quality
}

/// <summary>Identifies a channel table sort direction.</summary>
public enum ChannelSortDirection
{
    /// <summary>Sorts in ascending order.</summary>
    Ascending,
    /// <summary>Sorts in descending order.</summary>
    Descending
}

/// <summary>Stores editable global identity values for one physical channel.</summary>
public sealed class ChannelIdentityEdit
{
    /// <summary>Gets or sets the global channel number override.</summary>
    public string NumberOverride { get; set; } = string.Empty;

    /// <summary>Gets or sets the global channel name override.</summary>
    public string NameOverride { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional equivalence-group identifier.</summary>
    public string EquivalenceGroup { get; set; } = string.Empty;
}

/// <summary>Requests an enabled-state change for one channel.</summary>
/// <param name="ChannelId">The physical source channel identifier.</param>
/// <param name="Enabled">Whether the channel should be enabled.</param>
public sealed record ChannelEnabledChange(SourceChannelId ChannelId, bool Enabled);

/// <summary>Requests an authoritative guide-binding change for one channel.</summary>
/// <param name="ChannelId">The physical source channel identifier.</param>
/// <param name="GuideIdentity">The serialized guide channel identity, or an empty string for automatic matching.</param>
public sealed record ChannelGuideBindingChange(SourceChannelId ChannelId, string GuideIdentity);

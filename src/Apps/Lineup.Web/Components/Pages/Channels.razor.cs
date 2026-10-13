using Lineup.Web.Services;
using Lineup.Web.Components.Channels;
using Microsoft.AspNetCore.Components;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays and refreshes the persisted physical HDHomeRun channel lineup.
/// </summary>
public partial class Channels
{
    [Inject]
    private StreamingTopologyStore TopologyStore { get; set; } = default!;

    [Inject]
    private DeviceGuideStore GuideStore { get; set; } = default!;

    [Inject]
    private IStreamingAdapterRegistry Adapters { get; set; } = default!;

    [Inject]
    private IPhysicalLineupService LineupRefresh { get; set; } = default!;

    [Inject]
    private ITopologyGuideService Guides { get; set; } = default!;

    [Inject]
    private ILineupSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private IWebSettingsService WebSettingsService => Services.GetService<IWebSettingsService>() ??
        WebSettingsServiceFallback.Get(SettingsService);

    [Inject]
    private IXmltvPublicationStore Publications { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    private IReadOnlyList<PhysicalChannelIdentity> _topologyChannels = [];
    private IReadOnlyDictionary<SourceChannelId, SourceGuideBindingState> _guideBindings = new Dictionary<SourceChannelId, SourceGuideBindingState>();
    private readonly Dictionary<SourceChannelId, ChannelIdentityEdit> _channelEdits = [];
    private string _search = string.Empty;
    private string _deviceFilter = string.Empty;
    private ChannelStateFilter _stateFilter;
    private ChannelFlagFilter _flagFilter;
    private GuideBindingFilter _guideFilter;
    private ChannelSortColumn _sortColumn = ChannelSortColumn.Device;
    private ChannelSortDirection _sortDirection = ChannelSortDirection.Ascending;
    private bool _isLoading = true;
    private bool _isRefreshing;
    private readonly HashSet<SourceChannelId> _updatingChannels = [];

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (!WebSettingsService.Settings.IsSetupComplete)
        {
            Navigation.NavigateTo("/settings#physical-devices", replace: true);
            return;
        }

        try
        {
            await LoadTopologyAsync();
        }

        catch (Exception ex)
        {
            Notifications.ShowError(Text["Channels_ErrorLoadingSavedLineup", ex.Message]);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadTopologyAsync()
    {
        _topologyChannels = await TopologyStore.ReadActiveAsync();
        _guideBindings = (await GuideStore.ReadBindingStatesAsync(activeGuideScopes: GetActiveGuideScopes())).ToDictionary(binding => binding.SourceChannelId);
        _channelEdits.Clear();
        foreach (var channel in _topologyChannels)
        {
            _channelEdits[channel.Source.Id] = new ChannelIdentityEdit
            {
                NumberOverride = channel.NumberOverride ?? string.Empty,
                NameOverride = channel.NameOverride ?? string.Empty,
                EquivalenceGroup = channel.EquivalenceGroupId?.ToString("D") ?? string.Empty
            };
        }
    }

    private async Task SaveIdentity(PhysicalChannelIdentity channel)
    {
        var edit = _channelEdits[channel.Source.Id];
        Guid? equivalenceGroup = null;
        if (!string.IsNullOrWhiteSpace(edit.EquivalenceGroup))
        {
            if (!Guid.TryParse(edit.EquivalenceGroup, out var parsedGroup))
            {
                Notifications.ShowError(Text["Channels_InvalidEquivalenceGroup"]);
                return;
            }
            equivalenceGroup = parsedGroup;
        }

        try
        {
            await TopologyStore.SetIdentityAsync(channel.Source.Id, edit.NumberOverride, edit.NameOverride, equivalenceGroup);
            await LoadTopologyAsync();
            Notifications.ShowSuccess(Text["Channels_SavedGlobalIdentity", channel.Source.ProviderNumber]);
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Channels_ErrorUpdatingIdentity", ex.Message]);
        }
    }

    private async Task SetGuideBinding(SourceChannelId sourceChannelId, string guideIdentity)
    {
        try
        {
            if (string.IsNullOrEmpty(guideIdentity))
            {
                await GuideStore.ClearBindingAsync(sourceChannelId);
            }
            else
            {
                var state = _guideBindings[sourceChannelId];
                var candidate = state.MatchingCandidates.Single(channel => GetGuideIdentity(channel.Id) == guideIdentity);
                await GuideStore.SetBindingAsync(sourceChannelId, candidate.Id, activeGuideScopes: GetActiveGuideScopes());
            }
            await LoadTopologyAsync();
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Channels_ErrorUpdatingGuideBinding", ex.Message]);
        }
    }

    private async Task RefreshChannels()
    {
        _isRefreshing = true;

        try
        {
            var results = await LineupRefresh.RefreshAsync(SettingsService.Settings.Streaming);
            await LoadTopologyAsync();
            var failed = results.Count(result => !result.Succeeded);
            var channels = results.Where(result => result.Succeeded).Sum(result => result.ChannelCount);
            if (failed == 0)
            {
                Notifications.ShowSuccess(Text["Channels_RefreshedChannels", channels]);
            }
            else
            {
                Notifications.ShowError(Text["Channels_RefreshedChannelsWithFailures", channels, failed]);
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Channels_ErrorRefreshingChannels", ex.Message]);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task SetChannelEnabled(SourceChannelId channelId, bool enabled)
    {
        if (!_updatingChannels.Add(channelId))
        {
            return;
        }

        try
        {
            await TopologyStore.SetEnabledAsync(channelId, enabled);
            await LoadTopologyAsync();
            if (Publications.Exists(SettingsService.Settings.XmltvOutputPath))
            {
                try
                {
                    await Guides.PublishAsync();
                }
                catch (Exception ex)
                {
                    Notifications.ShowError(Text["Channels_SavedButXmltvUpdateFailed", ex.Message]);
                }
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Channels_ErrorUpdatingChannel", channelId.ProviderKey, ex.Message]);
        }
        finally
        {
            _updatingChannels.Remove(channelId);
        }
    }

    private string GetDeviceName(PhysicalDeviceId deviceId) =>
        SettingsService.Settings.Streaming.PhysicalDevices.SingleOrDefault(device => device.Id == deviceId)?.Name ?? deviceId.ToString();

    private IReadOnlyList<PhysicalChannelIdentity> GetVisibleChannels()
    {
        var channels = _topologyChannels.Where(MatchesFilters);
        return ApplySort(channels).ToArray();
    }

    private bool MatchesFilters(PhysicalChannelIdentity channel)
    {
        if (!string.IsNullOrEmpty(_deviceFilter) &&
            !string.Equals(channel.Source.Id.PhysicalDeviceId.ToString(), _deviceFilter, StringComparison.Ordinal))
        {
            return false;
        }
        if (_stateFilter == ChannelStateFilter.Enabled && !channel.IsEnabled ||
            _stateFilter == ChannelStateFilter.Disabled && channel.IsEnabled)
        {
            return false;
        }
        if (!MatchesFlagFilter(channel) || !MatchesGuideFilter(channel))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(_search))
        {
            return true;
        }

        var search = _search.Trim();
        _guideBindings.TryGetValue(channel.Source.Id, out var binding);
        return Contains(GetDeviceName(channel.Source.Id.PhysicalDeviceId), search) ||
            Contains(channel.Source.Id.ProviderKey, search) ||
            Contains(channel.Source.ProviderNumber, search) ||
            Contains(channel.Source.ProviderName, search) ||
            Contains(channel.EffectiveNumber, search) ||
            Contains(channel.EffectiveName, search) ||
            Contains(channel.EquivalenceGroupId?.ToString("D"), search) ||
            Contains(GetMetadata(channel, "videoCodec"), search) ||
            Contains(GetMetadata(channel, "audioCodec"), search) ||
            binding?.MatchingCandidates.Any(candidate =>
                Contains(candidate.Number, search) ||
                Contains(candidate.Name, search) ||
                Contains(candidate.Id.ProviderKey, search)) == true;
    }

    private bool MatchesFlagFilter(PhysicalChannelIdentity channel) => _flagFilter switch
    {
        ChannelFlagFilter.Favorite => GetBooleanMetadata(channel, "favorite"),
        ChannelFlagFilter.Drm => GetBooleanMetadata(channel, "drm"),
        ChannelFlagFilter.Hd => GetBooleanMetadata(channel, "hd"),
        _ => true
    };

    private bool MatchesGuideFilter(PhysicalChannelIdentity channel)
    {
        _guideBindings.TryGetValue(channel.Source.Id, out var binding);
        return _guideFilter switch
        {
            GuideBindingFilter.Bound => binding?.State is GuideBindingState.Automatic or GuideBindingState.Manual,
            GuideBindingFilter.Ambiguous => binding?.IsAmbiguous == true,
            GuideBindingFilter.Unbound => binding?.State == GuideBindingState.ManualUnavailable,
            GuideBindingFilter.NoMatch => binding is null || binding.State == GuideBindingState.Unmatched,
            _ => true
        };
    }

    private IEnumerable<PhysicalChannelIdentity> ApplySort(IEnumerable<PhysicalChannelIdentity> channels) => _sortColumn switch
    {
        ChannelSortColumn.Enabled => Sort(channels, channel => channel.IsEnabled),
        ChannelSortColumn.Device => Sort(channels, channel => GetDeviceName(channel.Source.Id.PhysicalDeviceId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.EffectiveNumber, ChannelNumberComparer.Instance),
        ChannelSortColumn.ProviderIdentity => Sort(channels, channel => channel.Source.ProviderNumber, ChannelNumberComparer.Instance)
            .ThenBy(channel => channel.Source.ProviderName, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.GlobalNumber => Sort(channels, channel => channel.EffectiveNumber, ChannelNumberComparer.Instance),
        ChannelSortColumn.GlobalName => Sort(channels, channel => channel.EffectiveName, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.EquivalenceGroup => Sort(channels, channel => channel.EquivalenceGroupId?.ToString("D") ?? string.Empty, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.Guide => Sort(channels, GetGuideSortValue, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.Flags => Sort(channels, GetFlagsSortValue, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.Video => Sort(channels, channel => GetMetadata(channel, "videoCodec") ?? string.Empty, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.Audio => Sort(channels, channel => GetMetadata(channel, "audioCodec") ?? string.Empty, StringComparer.OrdinalIgnoreCase),
        ChannelSortColumn.Signal => SortNullableNumber(channels, channel => GetIntegerMetadata(channel, "signalStrength")),
        ChannelSortColumn.Quality => SortNullableNumber(channels, channel => GetIntegerMetadata(channel, "signalQuality")),
        _ => channels
    };

    private IOrderedEnumerable<PhysicalChannelIdentity> Sort<TKey>(
        IEnumerable<PhysicalChannelIdentity> channels,
        Func<PhysicalChannelIdentity, TKey> selector,
        IComparer<TKey>? comparer = null) =>
        _sortDirection == ChannelSortDirection.Ascending
            ? channels.OrderBy(selector, comparer)
            : channels.OrderByDescending(selector, comparer);

    private IOrderedEnumerable<PhysicalChannelIdentity> SortNullableNumber(
        IEnumerable<PhysicalChannelIdentity> channels,
        Func<PhysicalChannelIdentity, int?> selector) =>
        _sortDirection == ChannelSortDirection.Ascending
            ? channels.OrderBy(channel => selector(channel).HasValue ? 0 : 1).ThenBy(channel => selector(channel))
            : channels.OrderBy(channel => selector(channel).HasValue ? 0 : 1).ThenByDescending(channel => selector(channel));

    private string GetGuideSortValue(PhysicalChannelIdentity channel)
    {
        if (!_guideBindings.TryGetValue(channel.Source.Id, out var binding))
        {
            return string.Empty;
        }
        var selected = binding.SelectedGuideChannel;
        return selected is null ? string.Empty : $"{selected.Number} {selected.Name}";
    }

    private static string GetGuideIdentity(GuideChannelId channelId) =>
        $"{channelId.GuideSourceId.Value:D}|{channelId.ProviderKey}";

    private IReadOnlyList<GuideSourceScope> GetActiveGuideScopes() =>
        SettingsService.Settings.Streaming.GuideSources
            .Where(source => source.Enabled)
            .Select(source => GuideSourceSubscriptionResolver.Resolve(
                source,
                SettingsService.Settings.Streaming.PhysicalDevices,
                Adapters.GetGuide(source.Adapter.Kind)))
            .Where(scope => scope.PhysicalDeviceIds.Count > 0)
            .ToArray();

    private static string GetFlagsSortValue(PhysicalChannelIdentity channel) =>
        $"{(GetBooleanMetadata(channel, "favorite") ? 1 : 0)}{(GetBooleanMetadata(channel, "drm") ? 1 : 0)}{(GetBooleanMetadata(channel, "hd") ? 1 : 0)}";

    private void SetSort(ChannelSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == ChannelSortDirection.Ascending ? ChannelSortDirection.Descending : ChannelSortDirection.Ascending;
            return;
        }
        _sortColumn = column;
        _sortDirection = ChannelSortDirection.Ascending;
    }

    private bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(_search) ||
        !string.IsNullOrEmpty(_deviceFilter) ||
        _stateFilter != ChannelStateFilter.All ||
        _flagFilter != ChannelFlagFilter.All ||
        _guideFilter != GuideBindingFilter.All;

    private IReadOnlyList<ChannelDeviceFilterOption> GetDeviceFilterOptions() =>
        _topologyChannels
            .Select(channel => channel.Source.Id.PhysicalDeviceId)
            .Distinct()
            .OrderBy(GetDeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(deviceId => new ChannelDeviceFilterOption(deviceId.ToString(), GetDeviceName(deviceId)))
            .ToArray();

    private void ClearFilters()
    {
        _search = string.Empty;
        _deviceFilter = string.Empty;
        _stateFilter = ChannelStateFilter.All;
        _flagFilter = ChannelFlagFilter.All;
        _guideFilter = GuideBindingFilter.All;
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static bool GetBooleanMetadata(PhysicalChannelIdentity channel, string key) =>
        channel.Source.Metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string? GetMetadata(PhysicalChannelIdentity channel, string key) =>
        channel.Source.Metadata.TryGetValue(key, out var value) ? value : null;

    private static int? GetIntegerMetadata(PhysicalChannelIdentity channel, string key) =>
        channel.Source.Metadata.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;

    private static string FormatValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatPercentage(int? value) =>
        value is null ? "—" : $"{value}%";

}

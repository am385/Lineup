using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays and refreshes the persisted physical HDHomeRun channel lineup.
/// </summary>
public partial class Channels
{
    [Inject]
    private ChannelLineupStore ChannelLineupStore { get; set; } = default!;

    [Inject]
    private ChannelLineupRefreshService ChannelLineupRefresh { get; set; } = default!;

    [Inject]
    private EpgOrchestrator Orchestrator { get; set; } = default!;

    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IXmltvPublicationStore Publications { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    private ChannelLineupSnapshot? _channelLineup;
    private bool _isLoading = true;
    private bool _isRefreshing;
    private readonly HashSet<string> _updatingChannels = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (!SettingsService.Settings.IsSetupComplete)
        {
            Navigation.NavigateTo("/settings#device", replace: true);
            return;
        }

        try
        {
            _channelLineup = await ChannelLineupStore.ReadAsync();
        }
        catch (Exception ex)
        {
            Notifications.ShowError($"Error loading the saved channel lineup: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task RefreshChannels()
    {
        _isRefreshing = true;

        try
        {
            _channelLineup = await ChannelLineupRefresh.RefreshAsync();
            Notifications.ShowSuccess($"Refreshed {_channelLineup.Channels.Count} tuner channels. The saved lineup will be applied during the next guide fetch.");
        }
        catch (Exception ex)
        {
            Notifications.ShowError($"Error refreshing channels: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task SetChannelEnabled(string guideNumber, bool enabled)
    {
        if (!_updatingChannels.Add(guideNumber))
        {
            return;
        }

        try
        {
            _channelLineup = await ChannelLineupStore.SetChannelEnabledAsync(guideNumber, enabled);
            if (Publications.Exists(SettingsService.Settings.XmltvOutputPath))
            {
                try
                {
                    await Orchestrator.GenerateEpgFromCacheAsync(SettingsService.Settings.TargetDays, SettingsService.Settings.XmltvOutputPath);
                }
                catch (Exception ex)
                {
                    Notifications.ShowError($"The channel setting was saved, but the published XMLTV guide could not be updated: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError($"Error updating channel {guideNumber}: {ex.Message}");
        }
        finally
        {
            _updatingChannels.Remove(guideNumber);
        }
    }

    private static string FormatValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatPercentage(int? value) =>
        value is null ? "—" : $"{value}%";
}

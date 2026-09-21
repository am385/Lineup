using Lineup.Core;
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
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private ChannelLineupSnapshot? _channelLineup;
    private bool _isLoading = true;
    private bool _isRefreshing;
    private readonly HashSet<string> _updatingChannels = new(StringComparer.OrdinalIgnoreCase);
    private string _statusMessage = string.Empty;
    private bool _isError;

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
            _statusMessage = $"Error loading the saved channel lineup: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task RefreshChannels()
    {
        _isRefreshing = true;
        _statusMessage = string.Empty;

        try
        {
            _channelLineup = await ChannelLineupRefresh.RefreshAsync();
            _statusMessage = $"Refreshed {_channelLineup.Channels.Count} tuner channels. The saved lineup will be applied during the next guide fetch.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error refreshing channels: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ClearStatus()
    {
        _statusMessage = string.Empty;
    }

    private async Task SetChannelEnabled(string guideNumber, bool enabled)
    {
        if (!_updatingChannels.Add(guideNumber))
        {
            return;
        }

        _statusMessage = string.Empty;
        try
        {
            _channelLineup = await ChannelLineupStore.SetChannelEnabledAsync(guideNumber, enabled);
            _isError = false;
            if (File.Exists(SettingsService.Settings.XmltvOutputPath))
            {
                try
                {
                    await Orchestrator.GenerateEpgFromCacheAsync(SettingsService.Settings.TargetDays, SettingsService.Settings.XmltvOutputPath);
                }
                catch (Exception ex)
                {
                    _statusMessage = $"The channel setting was saved, but the published XMLTV guide could not be updated: {ex.Message}";
                    _isError = true;
                }
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error updating channel {guideNumber}: {ex.Message}";
            _isError = true;
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

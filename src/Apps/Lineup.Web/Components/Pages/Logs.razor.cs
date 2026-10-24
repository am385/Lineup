using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Serilog.Events;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays recent in-memory events and retained application log files.
/// </summary>
public partial class Logs : IDisposable
{
    private static readonly LogEventLevel[] DisplayLevels = [LogEventLevel.Verbose, LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error, LogEventLevel.Fatal];
    private readonly Timer _timer;
    private LogEventLevel _minimumLevel = LogEventLevel.Information;
    private string _category = "";
    private string _search = "";
    private int _pageSize = 100;
    private bool _autoRefresh = true;
    private IReadOnlyList<LineupLogEvent> _events = [];
    private IReadOnlyList<LogFileInfo> _files = [];

    [Inject]
    private ILogEventStore EventStore { get; set; } = default!;

    [Inject]
    private LogFileService LogFiles { get; set; } = default!;

    [Inject]
    private LoggingRuntimeState RuntimeState { get; set; } = default!;

    /// <summary>
    /// Initializes the Logs page refresh timer.
    /// </summary>
    public Logs()
    {
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Refresh();
        _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void OnTimer(object? state)
    {
        if (_autoRefresh)
        {
            _ = InvokeAsync(() =>
            {
                Refresh();
                StateHasChanged();
            });
        }
    }

    private void ToggleAutoRefresh()
    {
        _autoRefresh = !_autoRefresh;
        if (_autoRefresh)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        _events = EventStore.GetEvents(_minimumLevel, _category, _search, _pageSize);
        _files = LogFiles.GetFiles();
    }

    private void ClearLogs()
    {
        EventStore.Clear();
        Refresh();
    }

    private void ChangeMinimumLevel(LogEventLevel level)
    {
        _minimumLevel = level;
        Refresh();
    }

    private void ChangeCategory(string category)
    {
        _category = category;
        Refresh();
    }

    private void ChangeSearch(string search)
    {
        _search = search;
        Refresh();
    }

    private void ChangePageSize(int pageSize)
    {
        _pageSize = pageSize;
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
    }
}

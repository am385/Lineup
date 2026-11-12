using Lineup.App.Web.Services;
using Lineup.Core.Common.Guides;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Globalization;

namespace Lineup.App.Web.Components.Pages;

/// <summary>
/// Displays a time-windowed guide with elapsed-time navigation and independently cancellable reads.
/// </summary>
public partial class Guide : IAsyncDisposable
{
    [Inject]
    private ILineupGuideReader GuideReader { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private TimeProvider Clock { get; set; } = default!;

    [Inject]
    private ILogger<Guide> Logger { get; set; } = default!;

    private const int SlotWidthPx = 120; // Width of each 30-minute time slot in pixels
    private const int ChannelColumnWidth = 100; // Width of channel column
    private const int MinSlots = 2; // Minimum 2 slots = 1 hour
    private const int MaxSlots = 24; // Maximum 24 slots = 12 hours

    private DotNetObjectReference<Guide>? _dotNetRef;

    private List<GuideChannelSegment> _channels = [];
    private List<GuideCacheProgram> _programs = [];
    private IReadOnlyList<GuideSourceDiagnostic> _sourceDiagnostics = [];
    private List<DateTimeOffset> _timeSlots = [];
    private DateTime _selectedDate;
    private DateTimeOffset _windowStartUtc;
    private TimeZoneInfo _displayTimeZone = TimeZoneInfo.Utc;
    private int _slotsToShow = 8; // Number of 30-minute slots to show
    private int _containerWidth = 0; // Start at 0 to indicate not yet measured
    private bool _isLoading = true;
    private bool _loadFailed;
    private bool _disposed;
    private long _loadVersion;
    private CancellationTokenSource? _activeLoad;
    private GuideCacheProgram? _selectedProgram;

    private TimeSpan WindowDuration => TimeSpan.FromMinutes(_slotsToShow * 30);
    private DateTime Today => TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), _displayTimeZone).Date;
    private DateTimeOffset DayStartUtc => GuideTimeline.ResolveLocalTime(_selectedDate, _displayTimeZone);
    private DateTimeOffset LastWindowStartUtc => DayEndUtc - WindowDuration;
    private DateTimeOffset DayEndUtc => GuideTimeline.ResolveLocalTime(_selectedDate.AddDays(1), _displayTimeZone);

    /// <summary>
    /// Initializes the selected local date without loading an unmeasured guide window.
    /// </summary>
    protected override void OnInitialized()
    {
        SelectCurrentWindow();
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    /// <summary>
    /// Performs the on after render operation.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }
        var initialVersion = _loadVersion;
        int width;
        try
        {
            width = await JS.GetGuideViewportWidthAsync();
        }
        catch (JSException exception)
        {
            Logger.LogWarning(exception, "Unable to measure the guide viewport; using the default width");
            width = 800;
        }
        if (_disposed)
        {
            return;
        }
        if (_containerWidth == 0)
        {
            _containerWidth = width;
        }
        _slotsToShow = GetSlotCount(_containerWidth);
        if (_loadVersion == initialVersion)
        {
            SelectCurrentWindow();
        }
        else
        {
            ClampWindowStart();
        }
        await LoadDataAsync();
        if (_disposed)
        {
            return;
        }
        try
        {
            await JS.SetupGuideResizeObserverAsync(_dotNetRef!);
        }
        catch (JSException exception)
        {
            Logger.LogWarning(exception, "Unable to observe guide viewport changes");
        }
        if (!_disposed)
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Performs the on container resize operation.
    /// </summary>
    [JSInvokable]
    public async Task OnContainerResize(int width)
    {
        if (!_disposed && width > 0 && width != _containerWidth)
        {
            _containerWidth = width;
            var slots = GetSlotCount(width);
            if (slots != _slotsToShow)
            {
                _slotsToShow = slots;
                ClampWindowStart();
                await LoadDataAsync();
                if (!_disposed)
                {
                    StateHasChanged();
                }
            }
        }
    }

    private static int GetSlotCount(int width) => Math.Clamp((width - ChannelColumnWidth - 20) / SlotWidthPx, MinSlots, MaxSlots);

    private async Task LoadDataAsync()
    {
        if (_disposed)
        {
            return;
        }
        var previous = _activeLoad;
        using var cancellation = new CancellationTokenSource();
        _activeLoad = cancellation;
        var version = ++_loadVersion;
        previous?.Cancel();
        var startUtc = _windowStartUtc;
        var slots = _slotsToShow;
        _isLoading = true;
        _loadFailed = false;
        StateHasChanged();

        try
        {
            var channels = await GuideReader.GetChannelDirectoryAsync(cancellation.Token);
            if (!IsCurrentLoad(version))
            {
                return;
            }
            var programs = await GuideReader.GetProgramsAsync(startUtc, startUtc.AddMinutes(slots * 30), cancellation.Token);
            if (!IsCurrentLoad(version))
            {
                return;
            }
            var diagnostics = GuideReader.GetSourceDiagnostics();
            _channels = SortChannels(channels);
            _programs = programs.ToList();
            _sourceDiagnostics = diagnostics;
            _timeSlots = Enumerable.Range(0, slots).Select(index => startUtc.AddMinutes(index * 30)).ToList();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Unable to load the program guide");
            if (IsCurrentLoad(version))
            {
                _loadFailed = true;
            }
        }
        finally
        {
            if (IsCurrentLoad(version))
            {
                _isLoading = false;
                _activeLoad = null;
            }
        }
    }

    private bool IsCurrentLoad(long version) => !_disposed && version == _loadVersion;

    private static List<GuideChannelSegment> SortChannels(IEnumerable<GuideChannelSegment> channels) =>
        channels
            .OrderBy(channel => GetChannelSortValue(channel.GuideNumber))
            .ThenBy(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static decimal GetChannelSortValue(string? channelNumber) =>
        decimal.TryParse(channelNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : decimal.MaxValue;

    private string FormatTimeRange() =>
        $"{GuideTimeline.FormatTime(_windowStartUtc, _displayTimeZone)} - {GuideTimeline.FormatTime(_windowStartUtc + WindowDuration, _displayTimeZone)}";

    private async Task SelectDate(DateTime date)
    {
        _displayTimeZone = Tz.TimeZone;
        _selectedDate = date.Date;
        if (_selectedDate == Today)
        {
            SelectCurrentWindow();
        }
        else
        {
            _windowStartUtc = GuideTimeline.ResolveLocalTime(_selectedDate.AddHours(6), _displayTimeZone);
            ClampWindowStart();
        }
        await LoadDataAsync();
    }

    private async Task PreviousTimeBlock()
    {
        _windowStartUtc -= WindowDuration;
        ClampWindowStart();
        await LoadDataAsync();
    }

    private async Task NextTimeBlock()
    {
        _windowStartUtc += WindowDuration;
        ClampWindowStart();
        await LoadDataAsync();
    }

    private async Task JumpToNow()
    {
        SelectCurrentWindow();
        await LoadDataAsync();
    }

    private bool IsShowingCurrentTime()
    {
        var now = Clock.GetUtcNow();
        return now >= _windowStartUtc && now < _windowStartUtc + WindowDuration;
    }

    private void SelectCurrentWindow()
    {
        _displayTimeZone = Tz.TimeZone;
        var now = Clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, _displayTimeZone);
        _selectedDate = local.Date;
        _windowStartUtc = GuideTimeline.ResolveLocalTime(_selectedDate.AddHours(local.Hour / 2 * 2), _displayTimeZone);
        if (now >= _windowStartUtc + WindowDuration)
        {
            _windowStartUtc = now.AddTicks(-(local.TimeOfDay.Ticks % TimeSpan.FromMinutes(30).Ticks));
        }
        ClampWindowStart();
    }

    private void ClampWindowStart()
    {
        var first = DayStartUtc;
        var last = LastWindowStartUtc;
        _windowStartUtc = _windowStartUtc < first ? first : _windowStartUtc > last ? last : _windowStartUtc;
    }

    private void ShowProgramDetails(GuideCacheProgram program)
    {
        _selectedProgram = program;
    }

    private void CloseModal()
    {
        _selectedProgram = null;
    }

    /// <summary>
    /// Performs the dispose operation.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var active = _activeLoad;
        _activeLoad = null;
        active?.Cancel();
        try
        {
            await JS.DisposeGuideResizeObserverAsync();
        }
        catch (Exception exception) when (exception is JSException or ObjectDisposedException)
        {
            Logger.LogDebug(exception, "Guide resize observation ended during disposal");
        }

        _dotNetRef?.Dispose();
    }
}

using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Globalization;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Represents guide.
/// </summary>
public partial class Guide : IAsyncDisposable
{
    [Inject]
    private ILineupGuideReader GuideReader { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    private const int SlotWidthPx = 120; // Width of each 30-minute time slot in pixels
    private const int ChannelColumnWidth = 100; // Width of channel column
    private const int MinSlots = 2; // Minimum 2 slots = 1 hour
    private const int MaxSlots = 24; // Maximum 24 slots = 12 hours

    private DotNetObjectReference<Guide>? _dotNetRef;

    private List<GuideChannelSegment> _channels = [];
    private List<GuideCacheProgram> _programs = [];
    private List<DateTime> _timeSlots = [];
    private DateTime _selectedDate;
    private int _startHour;
    private int _slotsToShow = 8; // Number of 30-minute slots to show
    private int _containerWidth = 0; // Start at 0 to indicate not yet measured
    private bool _isLoading = true;
    private bool _isInitialized = false;
    private GuideCacheProgram? _selectedProgram;

    // Calculate hours from slots for navigation
    private int _hoursToShow => Math.Max(1, _slotsToShow / 2);


    /// <summary>
    /// Performs the on initialized operation.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        _selectedDate = Tz.Today;
        _startHour = (Tz.Now.Hour / 2) * 2;
        _dotNetRef = DotNetObjectReference.Create(this);
        // Load channels but defer full data load until we know the container width
        _channels = SortChannels(await GuideReader.GetChannelsAsync());
    }

    /// <summary>
    /// Performs the on after render operation.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                // Get initial width from the main content area (not the guide container which can grow)
                _containerWidth = await JS.GetGuideViewportWidthAsync();

                // Calculate slots based on actual width
                await CalculateSlotsToShowAndLoad();
                _isInitialized = true;

                // Set up resize observer that calls back to this instance
                await JS.SetupGuideResizeObserverAsync(_dotNetRef!);

                StateHasChanged();
            }
            catch
            {
                // Fallback if JS fails - use reasonable default
                _containerWidth = 800;
                await CalculateSlotsToShowAndLoad();
                _isInitialized = true;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Performs the on container resize operation.
    /// </summary>
    [JSInvokable]
    public async Task OnContainerResize(int width)
    {
        if (width > 0 && width != _containerWidth)
        {
            _containerWidth = width;
            await CalculateSlotsToShow();
            StateHasChanged();
        }
    }

    /// <summary>
    /// Calculate slots and load data - used on initial render
    /// </summary>
    private async Task CalculateSlotsToShowAndLoad()
    {
        // Calculate how many 30-minute slots fit based on container width
        var availableWidth = _containerWidth - ChannelColumnWidth - 20;
        var possibleSlots = (int)(availableWidth / SlotWidthPx);
        _slotsToShow = Math.Clamp(possibleSlots, MinSlots, MaxSlots);

        // Now load the data with the correct slot count
        await LoadDataAsync();
    }

    /// <summary>
    /// Calculate slots and update data - used on resize
    /// </summary>
    private async Task CalculateSlotsToShow()
    {
        // Calculate how many 30-minute slots fit based on container width
        var availableWidth = _containerWidth - ChannelColumnWidth - 20; // Some padding
        var possibleSlots = (int)(availableWidth / SlotWidthPx);

        // Clamp to min/max
        var newSlotsToShow = Math.Clamp(possibleSlots, MinSlots, MaxSlots);


        if (newSlotsToShow != _slotsToShow)
        {
            _slotsToShow = newSlotsToShow;

            // Update time slots
            var startTime = _selectedDate.Date.AddHours(_startHour);
            _timeSlots = [];
            for (int i = 0; i < _slotsToShow; i++)
            {
                _timeSlots.Add(startTime.AddMinutes(i * 30));
            }

            // Only reload data if we need more programs for expanded time range
            if (_isInitialized)
            {
                var endTime = startTime.AddMinutes(_slotsToShow * 30);
                _programs = (await GuideReader.GetProgramsAsync(
                    Tz.ConvertToUtc(startTime),
                    Tz.ConvertToUtc(endTime))).ToList();
            }
        }
    }

    private async Task LoadDataAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            _channels = SortChannels(await GuideReader.GetChannelsAsync());

            var startTime = _selectedDate.Date.AddHours(_startHour);
            var endTime = startTime.AddMinutes(_slotsToShow * 30);

            _programs = (await GuideReader.GetProgramsAsync(
                Tz.ConvertToUtc(startTime),
                Tz.ConvertToUtc(endTime))).ToList();

            // Generate time slots (every 30 minutes)
            _timeSlots = [];
            for (int i = 0; i < _slotsToShow; i++)
            {
                _timeSlots.Add(startTime.AddMinutes(i * 30));
            }

        }
        finally
        {
            _isLoading = false;
        }
    }

    private static List<GuideChannelSegment> SortChannels(IEnumerable<GuideChannelSegment> channels) =>
        channels
            .OrderBy(channel => GetChannelSortValue(channel.GuideNumber))
            .ThenBy(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static decimal GetChannelSortValue(string? channelNumber) =>
        decimal.TryParse(channelNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : decimal.MaxValue;

    private string FormatTimeRange()
    {
        var startTime = _selectedDate.Date.AddHours(_startHour);
        var endTime = startTime.AddMinutes(_slotsToShow * 30);
        return $"{startTime:h:mm tt} - {endTime:h:mm tt}";
    }

    private async Task SelectDate(DateTime date)
    {
        _selectedDate = date;
        if (date == Tz.Today)
        {
            _startHour = (Tz.Now.Hour / 2) * 2;
        }
        else
        {
            _startHour = 6; // Start at 6 AM for future dates
        }
        await LoadDataAsync();
    }

    private async Task PreviousTimeBlock()
    {
        // Move back by the number of hours currently shown
        _startHour = Math.Max(0, _startHour - _hoursToShow);
        await LoadDataAsync();
    }

    private async Task NextTimeBlock()
    {
        // Move forward by the number of hours currently shown
        _startHour = Math.Min(24 - _hoursToShow, _startHour + _hoursToShow);
        await LoadDataAsync();
    }

    private async Task JumpToNow()
    {
        _selectedDate = Tz.Today;
        _startHour = (Tz.Now.Hour / 2) * 2; // Round to nearest even hour
        await LoadDataAsync();
    }

    private bool IsShowingCurrentTime()
    {
        var now = Tz.Now;
        var windowStart = _selectedDate.Date.AddHours(_startHour);
        var windowEnd = windowStart.AddMinutes(_slotsToShow * 30);
        return now >= windowStart && now < windowEnd;
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
        try
        {
            await JS.DisposeGuideResizeObserverAsync();
        }
        catch
        {
            // Ignore disposal errors
        }

        _dotNetRef?.Dispose();
    }
}

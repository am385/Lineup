using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

public partial class Guide : IAsyncDisposable
{
    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    private const int SlotWidthPx = 120; // Width of each 30-minute time slot in pixels
    private const int ChannelColumnWidth = 100; // Width of channel column
    private const int MinSlots = 2; // Minimum 2 slots = 1 hour
    private const int MaxSlots = 24; // Maximum 24 slots = 12 hours
    private const double PixelsPerMinute = SlotWidthPx / 30.0;

    private ElementReference _guideContainer;
    private DotNetObjectReference<Guide>? _dotNetRef;

    private List<HDHomeRunChannelEpgSegment> _channels = [];
    private List<HDHomeRunProgram> _programs = [];
    private List<DateTime> _timeSlots = [];
    private DateTime _selectedDate;
    private int _startHour;
    private int _slotsToShow = 8; // Number of 30-minute slots to show
    private int _containerWidth = 0; // Start at 0 to indicate not yet measured
    private bool _isLoading = true;
    private bool _isInitialized = false;
    private HDHomeRunProgram? _selectedProgram;

    // Calculate hours from slots for navigation
    private int _hoursToShow => Math.Max(1, _slotsToShow / 2);

    private int _programsWidth => _timeSlots.Count * SlotWidthPx;

    protected override async Task OnInitializedAsync()
    {
        _selectedDate = Tz.Today;
        _startHour = (Tz.Now.Hour / 2) * 2;
        _dotNetRef = DotNetObjectReference.Create(this);
        // Load channels but defer full data load until we know the container width
        _channels = await Repository.GetChannelsAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                // Get initial width from the main content area (not the guide container which can grow)
                _containerWidth = await JS.InvokeAsync<int>("eval",
                    "(document.querySelector('main') || document.querySelector('article') || document.body).clientWidth - 40");

                // Calculate slots based on actual width
                await CalculateSlotsToShowAndLoad();
                _isInitialized = true;

                // Set up resize observer that calls back to this instance
                await JS.InvokeVoidAsync("setupGuideResizeObserver", _dotNetRef);

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
                _programs = await Repository.GetProgramsAsync(Tz.ConvertToUtc(startTime), Tz.ConvertToUtc(endTime));
            }
        }
    }

    private async Task LoadDataAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            _channels = await Repository.GetChannelsAsync();

            var startTime = _selectedDate.Date.AddHours(_startHour);
            var endTime = startTime.AddMinutes(_slotsToShow * 30);

            _programs = await Repository.GetProgramsAsync(Tz.ConvertToUtc(startTime), Tz.ConvertToUtc(endTime));

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

    private List<HDHomeRunProgram> GetProgramsForChannel(string? guideNumber)
    {
        if (string.IsNullOrEmpty(guideNumber)) return [];

        return _programs
            .Where(p => p.GuideNumber == guideNumber)
            .OrderBy(p => p.StartTime)
            .ToList();
    }

    private (string Style, double WidthPx) GetProgramLayout(HDHomeRunProgram program)
    {
        var windowStart = _selectedDate.Date.AddHours(_startHour);
        var windowEnd = windowStart.AddMinutes(_slotsToShow * 30);

        var programStart = Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(program.StartTime).UtcDateTime);
        var programEnd = Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(program.EndTime).UtcDateTime);

        // Clamp program times to window
        var displayStart = programStart < windowStart ? windowStart : programStart;
        var displayEnd = programEnd > windowEnd ? windowEnd : programEnd;

        // Calculate position and width
        var offsetMinutes = (displayStart - windowStart).TotalMinutes;
        var durationMinutes = (displayEnd - displayStart).TotalMinutes;

        var left = offsetMinutes * PixelsPerMinute;
        var width = Math.Max(durationMinutes * PixelsPerMinute - 4, 20);

        return ($"left: {left:F0}px; width: {width:F0}px;", width);
    }

    private static int EstimateMaxChars(double widthPx, double charWidthPx)
    {
        const double padding = 13; // 0.4rem * 2 horizontal padding
        return Math.Max(3, (int)((widthPx - padding) / charWidthPx));
    }

    private bool IsCurrentlyAiring(HDHomeRunProgram program)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return program.StartTime <= now && program.EndTime > now;
    }

    private string GetProgramTooltip(HDHomeRunProgram program)
    {
        var start = Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(program.StartTime).UtcDateTime);
        var end = Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(program.EndTime).UtcDateTime);
        var duration = (end - start).TotalMinutes;

        var tooltip = $"{program.Title}\n{start:h:mm tt} - {end:h:mm tt} ({duration:0} min)";

        if (!string.IsNullOrEmpty(program.EpisodeTitle))
        {
            tooltip += $"\n{program.EpisodeTitle}";
        }

        return tooltip;
    }

    private void ShowProgramDetails(HDHomeRunProgram program)
    {
        _selectedProgram = program;
    }

    private void CloseModal()
    {
        _selectedProgram = null;
    }

    private string FormatProgramTime(long unixTime)
    {
        return Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime).ToString("h:mm tt");
    }

    private string FormatOriginalAirdate(long unixTime)
    {
        return Tz.ConvertFromUtc(DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime).ToString("yyyy-MM-dd");
    }


    private static int GetDuration(HDHomeRunProgram program)
    {
        return (int)((program.EndTime - program.StartTime) / 60);
    }

    private static string TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "\u2026";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("disposeGuideResizeObserver");
        }
        catch
        {
            // Ignore disposal errors
        }

        _dotNetRef?.Dispose();
    }
}

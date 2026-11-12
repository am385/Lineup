namespace Lineup.App.Web.Services;

/// <summary>Bounds one live playback recovery episode using monotonic time and a stable-playback reset.</summary>
internal sealed class PlaybackReconnectPolicy(TimeProvider clock)
{
    private long? _started;
    private long? _playingSince;
    private int _attempts;

    /// <summary>Gets the remaining wall-clock recovery budget, including backoff and startup.</summary>
    internal TimeSpan Remaining => _started is { } started ? TimeSpan.FromSeconds(60) - clock.GetElapsedTime(started) : TimeSpan.FromSeconds(60);

    /// <summary>Records successfully initialized playback without immediately replenishing a flapping session's budget.</summary>
    internal void MarkPlaying() => _playingSince ??= clock.GetTimestamp();

    /// <summary>Interrupts the healthy interval when playback pauses or waits for media.</summary>
    internal void MarkUnstable()
    {
        if (_playingSince is { } playing && clock.GetElapsedTime(playing) >= TimeSpan.FromSeconds(60))
        {
            Reset();
        }
        _playingSince = null;
    }

    /// <summary>Begins a failure episode, replenishing its budget only after sixty healthy seconds.</summary>
    internal void MarkFailed()
    {
        MarkUnstable();
        _started ??= clock.GetTimestamp();
    }

    /// <summary>Reserves at most three attempts with one, two, and four second delays inside the deadline.</summary>
    internal bool TryNextAttempt(out TimeSpan delay)
    {
        delay = TimeSpan.FromSeconds(1 << _attempts);
        if (_attempts >= 3 || Remaining <= delay)
        {
            return false;
        }
        _attempts++;
        return true;
    }

    /// <summary>Resets recovery ownership for an explicit new tune or stop.</summary>
    internal void Reset()
    {
        _started = null;
        _playingSince = null;
        _attempts = 0;
    }
}

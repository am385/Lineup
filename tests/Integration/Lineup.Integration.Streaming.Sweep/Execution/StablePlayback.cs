namespace Lineup.Integration.Streaming.Sweep;

/// <summary>One browser sample used to measure uninterrupted media progression.</summary>
internal sealed record PlaybackSample(double MediaTime, bool Ready, bool Paused, bool Error, int Discontinuities, string Session);

/// <summary>Monotonic wall-clock stability policy with resets on stalls, recovery and replacement.</summary>
internal sealed class StablePlayback(double stableSeconds, double mediaAdvanceSeconds)
{
    private double? _started;
    private double _initialTime;
    private PlaybackSample? _previous;
    /// <summary>Gets the measured continuous interval at the last observation.</summary>
    public double StableDurationSeconds { get; private set; }
    /// <summary>Gets media advancement within that interval.</summary>
    public double MediaAdvanceSeconds { get; private set; }

    /// <summary>Records a sample and returns whether both independent thresholds have been met.</summary>
    public bool Observe(double elapsedSeconds, PlaybackSample sample)
    {
        if (!sample.Ready || sample.Paused || sample.Error || (_previous is not null
            && (sample.Discontinuities != _previous.Discontinuities || sample.Session != _previous.Session || sample.MediaTime <= _previous.MediaTime)))
        {
            _started = null;
        }
        else if (_started is null)
        {
            _started = elapsedSeconds;
            _initialTime = sample.MediaTime;
        }
        _previous = sample;
        StableDurationSeconds = _started is double start ? elapsedSeconds - start : 0;
        MediaAdvanceSeconds = _started is not null ? sample.MediaTime - _initialTime : 0;
        return StableDurationSeconds >= stableSeconds && MediaAdvanceSeconds >= mediaAdvanceSeconds;
    }
}

/// <summary>Samples a complete fixed watch window without ending early or extending it to recover stability.</summary>
internal sealed class FixedWatch
{
    private readonly double _seconds;
    private readonly StablePlayback _stability;

    /// <summary>Seeds stability with the ready sample taken immediately before the watch clock starts.</summary>
    /// <param name="seconds">The fixed wall-clock watch interval.</param>
    /// <param name="stability">The independent stability and progression policy.</param>
    /// <param name="initialSample">The ready playback sample at the start of the interval.</param>
    public FixedWatch(double seconds, StablePlayback stability, PlaybackSample initialSample)
    {
        _seconds = seconds;
        _stability = stability;
        _stability.Observe(0, initialSample);
    }

    /// <summary>Gets whether the final boundary sample has been observed.</summary>
    public bool Complete { get; private set; }

    /// <summary>Gets whether the most recent sample satisfies stability and media progression.</summary>
    public bool Passed { get; private set; }

    /// <summary>Limits the next half-second sampling delay to the remaining watch interval.</summary>
    /// <param name="elapsedSeconds">The monotonic elapsed watch time.</param>
    /// <returns>The delay before the next sample.</returns>
    public TimeSpan NextDelay(double elapsedSeconds) => TimeSpan.FromSeconds(Math.Clamp(_seconds - elapsedSeconds, 0, 0.5));

    /// <summary>Records the final boundary sample even when the required stable interval equals the watch length.</summary>
    /// <param name="elapsedSeconds">The monotonic elapsed watch time.</param>
    /// <param name="sample">The decoded playback sample.</param>
    public void Observe(double elapsedSeconds, PlaybackSample sample)
    {
        Passed = _stability.Observe(elapsedSeconds, sample);
        Complete = elapsedSeconds >= _seconds;
    }
}

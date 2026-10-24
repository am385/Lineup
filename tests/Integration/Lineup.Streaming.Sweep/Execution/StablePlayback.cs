namespace Lineup.Streaming.Sweep;

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

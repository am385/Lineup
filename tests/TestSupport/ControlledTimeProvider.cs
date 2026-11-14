using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Lineup.TestSupport;

/// <summary>Provides deterministic elapsed time and an explicit barrier for timer registration.</summary>
internal sealed class ControlledTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _registrations = Channel.CreateUnbounded<TimeSpan>();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _registrations.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Waits until a timer with the requested delay has actually been registered.</summary>
    /// <param name="dueTime">The timer delay whose registration must be observed.</param>
    /// <param name="cancellationToken">The test lifetime cancellation token.</param>
    internal async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken cancellationToken)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        while (await _registrations.Reader.ReadAsync(watchdog.Token) != dueTime)
        {
        }
    }
}

using Microsoft.Extensions.Logging;

namespace Lineup.Core.Streaming;

/// <summary>Shares an in-flight refresh while retaining independent caller cancellation.</summary>
internal sealed class CoalescedRefresh<T>(ILogger logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Work> _pending = [];

    /// <summary>Joins equivalent work; the last departing caller cancels an unfinished operation.</summary>
    internal async Task<T> RunAsync(string key, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool waitForCancellationCleanup = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Work work;
        var start = false;
        lock (_sync)
        {
            if (!_pending.TryGetValue(key, out work!))
            {
                work = new Work();
                _pending.Add(key, work);
                start = true;
            }
            work.Waiters++;
        }
        if (start)
        {
            _ = ExecuteAsync(key, work, operation);
        }
        var detached = false;
        try
        {
            return await work.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (waitForCancellationCleanup && cancellationToken.IsCancellationRequested)
        {
            detached = true;
            Release(key, work);
            return await work.Completion.Task;
        }
        finally
        {
            if (!detached)
            {
                Release(key, work);
            }
        }
    }

    private void Release(string key, Work work)
    {
        var cancel = false;
        lock (_sync)
        {
            work.Waiters--;
            if (work.Waiters == 0)
            {
                if (work.Finished)
                {
                    work.Cancellation.Dispose();
                }
                else
                {
                    Remove(key, work);
                    work.Canceling = true;
                    cancel = true;
                }
            }
        }
        if (cancel)
        {
            try
            {
                work.Cancellation.Cancel();
            }
            finally
            {
                lock (_sync)
                {
                    work.Canceling = false;
                    if (work.Finished)
                    {
                        work.Cancellation.Dispose();
                    }
                }
            }
        }
    }

    private async Task ExecuteAsync(string key, Work work, Func<CancellationToken, Task<T>> operation)
    {
        try
        {
            work.Completion.TrySetResult(await operation(work.Cancellation.Token));
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        {
            work.Completion.TrySetCanceled(work.Cancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Physical-device refresh failed");
            work.Completion.TrySetException(exception);
            _ = work.Completion.Task.Exception;
        }
        finally
        {
            lock (_sync)
            {
                Remove(key, work);
                work.Finished = true;
                if (work.Waiters == 0 && !work.Canceling)
                {
                    work.Cancellation.Dispose();
                }
            }
        }
    }

    private void Remove(string key, Work work)
    {
        if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, work))
        {
            _pending.Remove(key);
        }
    }

    private sealed class Work
    {
        /// <summary>Gets the shared completion.</summary>
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets cancellation owned collectively by current callers.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Gets or sets the attached caller count.</summary>
        public int Waiters { get; set; }

        /// <summary>Gets or sets whether execution has released its cancellation token.</summary>
        public bool Finished { get; set; }

        /// <summary>Gets or sets whether cancellation callbacks still own the token source.</summary>
        public bool Canceling { get; set; }
    }
}

using Microsoft.Extensions.Configuration;

namespace Lineup.Core.Streaming;

/// <summary>Bounds independent catalog, lineup, status and information network operations.</summary>
public sealed class PhysicalDeviceRefreshScheduler
{
    private readonly SemaphoreSlim _admission;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes shared refresh admission; deadlines require adapters to honor cancellation.</summary>
    public PhysicalDeviceRefreshScheduler(int maximumConcurrentOperations = 4, int operationTimeoutSeconds = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentOperations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentOperations, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(operationTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationTimeoutSeconds, 300);
        MaximumConcurrentOperations = maximumConcurrentOperations;
        _admission = new SemaphoreSlim(maximumConcurrentOperations, maximumConcurrentOperations);
        _timeout = TimeSpan.FromSeconds(operationTimeoutSeconds);
    }

    /// <summary>Gets the maximum number of admitted network operations.</summary>
    public int MaximumConcurrentOperations { get; }

    /// <summary>Loads the host's refresh policy, independently of editable streaming topology.</summary>
    public static PhysicalDeviceRefreshScheduler Load(IConfiguration? configuration)
    {
        int Read(string name, int fallback) => configuration?[$"PhysicalDeviceRefresh:{name}"] is { } value
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
        return new PhysicalDeviceRefreshScheduler(Read("MaximumConcurrentOperations", 4), Read("OperationTimeoutSeconds", 10));
    }

    /// <summary>Runs one operation with its own deadline after bounded admission.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, Func<T, ValueTask>? releaseLateResult = null)
    {
        await _admission.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                var result = await operation(deadline.Token);
                if (deadline.IsCancellationRequested)
                {
                    if (releaseLateResult is not null)
                    {
                        await releaseLateResult(result);
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                }
                return result;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("The physical-device operation exceeded its refresh deadline.", exception);
            }
        }
        finally
        {
            _admission.Release();
        }
    }
}

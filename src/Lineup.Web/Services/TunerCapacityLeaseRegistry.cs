using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Services;

/// <summary>
/// Represents an acquired receiver lease on a shared physical tuner source.
/// </summary>
public interface ITunerCapacityLease : IDisposable, IAsyncDisposable
{
    /// <summary>Gets the normalized physical profile key.</summary>
    string ProfileKey { get; }

    /// <summary>Gets the normalized upstream source key.</summary>
    string SourceKey { get; }
}

/// <summary>
/// Immutable diagnostics for one leased upstream source.
/// </summary>
/// <param name="SourceKey">Normalized upstream source.</param>
/// <param name="ReceiverCount">Number of receivers sharing the source.</param>
public sealed record TunerSourceLeaseDiagnostics(string SourceKey, int ReceiverCount);

/// <summary>
/// Immutable tuner-capacity diagnostics for one physical profile.
/// </summary>
/// <param name="ProfileKey">Normalized physical profile identifier.</param>
/// <param name="Capacity">Most recently applied effective tuner capacity.</param>
/// <param name="ActiveSourceCount">Number of unique upstream sources consuming tuner slots.</param>
/// <param name="ReceiverCount">Total receivers across the shared sources.</param>
/// <param name="Sources">Immutable source-level diagnostics.</param>
public sealed record TunerProfileLeaseDiagnostics(string ProfileKey, int Capacity, int ActiveSourceCount, int ReceiverCount, IReadOnlyList<TunerSourceLeaseDiagnostics> Sources);

/// <summary>
/// Atomically leases physical tuner capacity while sharing exact upstream sources.
/// </summary>
public interface ITunerCapacityLeaseRegistry
{
    /// <summary>
    /// Tries to acquire a receiver lease.
    /// </summary>
    /// <param name="physicalProfile">Physical profile URI used to isolate tuner capacity.</param>
    /// <param name="source">Effective upstream source URI.</param>
    /// <param name="capacity">Effective tuner count after applying the profile cap.</param>
    /// <param name="cancellationToken">Cancels acquisition before registry state changes.</param>
    /// <returns>A disposable lease, or <see langword="null"/> when every tuner slot is occupied.</returns>
    ValueTask<ITunerCapacityLease?> TryAcquireAsync(Uri physicalProfile, Uri source, int capacity, CancellationToken cancellationToken = default);

    /// <summary>Gets an immutable snapshot of all active physical tuner leases.</summary>
    IReadOnlyList<TunerProfileLeaseDiagnostics> GetDiagnostics();
}

/// <summary>
/// Thread-safe in-memory physical tuner capacity registry.
/// </summary>
public sealed class TunerCapacityLeaseRegistry : ITunerCapacityLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProfileState> _profiles = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ITunerCapacityLease?> TryAcquireAsync(Uri physicalProfile, Uri source, int capacity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileKey = NormalizeProfile(physicalProfile);
        var sourceKey = NormalizeSource(source);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_profiles.TryGetValue(profileKey, out var profile))
            {
                profile = new ProfileState();
                _profiles.Add(profileKey, profile);
            }

            profile.Capacity = Math.Max(0, capacity);
            if (profile.Sources.TryGetValue(sourceKey, out var receiverCount))
            {
                profile.Sources[sourceKey] = receiverCount + 1;
                return ValueTask.FromResult<ITunerCapacityLease?>(new Lease(this, profileKey, sourceKey));
            }

            if (profile.Sources.Count >= profile.Capacity)
            {
                if (profile.Sources.Count == 0)
                {
                    _profiles.Remove(profileKey);
                }

                return ValueTask.FromResult<ITunerCapacityLease?>(null);
            }

            profile.Sources.Add(sourceKey, 1);
            return ValueTask.FromResult<ITunerCapacityLease?>(new Lease(this, profileKey, sourceKey));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TunerProfileLeaseDiagnostics> GetDiagnostics()
    {
        lock (_gate)
        {
            var diagnostics = new List<TunerProfileLeaseDiagnostics>(_profiles.Count);
            foreach (var entry in _profiles.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var sources = entry.Value.Sources
                    .OrderBy(source => source.Key, StringComparer.Ordinal)
                    .Select(source => new TunerSourceLeaseDiagnostics(source.Key, source.Value))
                    .ToList()
                    .AsReadOnly();
                diagnostics.Add(new TunerProfileLeaseDiagnostics(
                    entry.Key,
                    entry.Value.Capacity,
                    sources.Count,
                    sources.Sum(source => source.ReceiverCount),
                    sources));
            }

            return diagnostics.AsReadOnly();
        }
    }

    /// <summary>
    /// Normalizes an upstream URI so equivalent source/channel requests share one tuner slot.
    /// </summary>
    /// <param name="source">Effective physical tuner source.</param>
    /// <returns>A stable normalized source key.</returns>
    public static string NormalizeSource(Uri source)
    {
        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme.ToLowerInvariant(),
            Host = source.Host.ToLowerInvariant(),
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeProfile(Uri physicalProfile)
    {
        var builder = new UriBuilder(physicalProfile)
        {
            Scheme = physicalProfile.Scheme.ToLowerInvariant(),
            Host = physicalProfile.Host.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private void Release(string profileKey, string sourceKey)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileKey, out var profile) ||
                !profile.Sources.TryGetValue(sourceKey, out var receiverCount))
            {
                return;
            }

            if (receiverCount > 1)
            {
                profile.Sources[sourceKey] = receiverCount - 1;
                return;
            }

            profile.Sources.Remove(sourceKey);
            if (profile.Sources.Count == 0)
            {
                _profiles.Remove(profileKey);
            }
        }
    }

    private sealed class ProfileState
    {
        /// <summary>
        /// Gets or sets capacity.
        /// </summary>
        public int Capacity { get; set; }

        /// <summary>
        /// Gets sources.
        /// </summary>
        public Dictionary<string, int> Sources { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Lease : ITunerCapacityLease
    {
        private TunerCapacityLeaseRegistry? _owner;

        /// <summary>
        /// Initializes a new instance of the <see cref="Lease"/> class.
        /// </summary>
        public Lease(TunerCapacityLeaseRegistry owner, string profileKey, string sourceKey)
        {
            _owner = owner;
            ProfileKey = profileKey;
            SourceKey = sourceKey;
        }

        /// <summary>
        /// Gets profile key.
        /// </summary>
        public string ProfileKey { get; }

        /// <summary>
        /// Gets source key.
        /// </summary>
        public string SourceKey { get; }

        /// <summary>
        /// Releases resources used by this instance.
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(ProfileKey, SourceKey);
        }

        /// <summary>
        /// Performs the dispose operation.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Creates HDHomeRun-compatible HTTP capacity errors.
/// </summary>
public static class HdHomeRunStreamError
{
    /// <summary>HDHomeRun error text returned when every tuner is occupied.</summary>
    public const string NoTunerAvailable = "805 All Tuners In Use";

    /// <summary>
    /// Applies the HDHomeRun error header and creates a 503 response.
    /// </summary>
    /// <param name="response">HTTP response receiving the compatibility header.</param>
    /// <returns>A 503 object result.</returns>
    public static ObjectResult CreateNoTunerAvailableResult(HttpResponse response)
    {
        response.Headers["X-HDHomeRun-Error"] = NoTunerAvailable;
        return new ObjectResult(new { error = NoTunerAvailable })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    }
}

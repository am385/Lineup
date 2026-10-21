namespace Lineup.Streaming.Sweep;

/// <summary>Validated, persisted execution policy. Durations are seconds.</summary>
internal sealed record SweepOptions
{
    /// <summary>Gets whether the deployment belongs to somebody else.</summary>
    public bool ExistingInstance { get; init; }
    /// <summary>Gets the management origin.</summary>
    public string BaseUrl { get; init; } = "http://localhost:8080";
    /// <summary>Gets channel numbers to include.</summary>
    public string[] Include { get; init; } = [];
    /// <summary>Gets channel numbers to exclude.</summary>
    public string[] Exclude { get; init; } = [];
    /// <summary>Gets selected matrix identifiers.</summary>
    public string[] Matrices { get; init; } = [];
    /// <summary>Gets the maximum selected channels per target.</summary>
    public int MaximumChannels { get; init; } = int.MaxValue;
    /// <summary>Gets the startup deadline.</summary>
    public double StartupSeconds { get; init; } = 60;
    /// <summary>Gets the continuous stable interval.</summary>
    public double StableSeconds { get; init; } = 30;
    /// <summary>Gets required media progression within the stable interval.</summary>
    public double MediaAdvanceSeconds { get; init; } = 15;
    /// <summary>Gets the independent observation deadline.</summary>
    public double ObservationSeconds { get; init; } = 120;
    /// <summary>Gets the cleanup deadline, including layered warm expiry.</summary>
    public double CleanupSeconds { get; init; } = 120;
    /// <summary>Gets the maximum number of attempts.</summary>
    public int MaximumAttempts { get; init; } = 3;
    /// <summary>Gets the delay between attempts.</summary>
    public double RetryDelaySeconds { get; init; } = 2;
    /// <summary>Gets the maximum aligned transport sequence difference.</summary>
    public int MaximumSequenceDelta { get; init; } = 1;
    /// <summary>Gets whether synthetic compatibility setup was explicitly requested.</summary>
    public bool SetupCompatibility { get; init; }
    /// <summary>Gets whether owned Compose recovery was explicitly permitted.</summary>
    public bool AllowMatrixRecovery { get; init; }
    /// <summary>Gets an optional product failure limit; lifecycle invariants cannot be waived.</summary>
    public int? MaximumProductFailures { get; init; }
    /// <summary>Gets a configured browser executable.</summary>
    public string? BrowserExecutable { get; init; }
    /// <summary>Gets a persistent browser profile directory.</summary>
    public string? BrowserProfile { get; init; }
    /// <summary>Gets whether only fresh synchronization observations are requested.</summary>
    public bool SyncOnly { get; init; }

    /// <summary>Rejects ambiguous and unsafe policies before deployment operations.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
        {
            throw new ArgumentException("Base URL must be an HTTP(S) origin without credentials, path, query or fragment.");
        }
        if (MaximumChannels < 1 || MaximumAttempts < 1 || MaximumAttempts > 20 || MaximumSequenceDelta < 0 || MaximumProductFailures < 0)
        {
            throw new ArgumentException("Channel/attempt counts and thresholds are out of bounds.");
        }
        double[] durations = [StartupSeconds, StableSeconds, MediaAdvanceSeconds, ObservationSeconds, CleanupSeconds, RetryDelaySeconds];
        if (durations.Any(value => !double.IsFinite(value) || value < 0 || value > 86400)
            || StartupSeconds == 0 || StableSeconds == 0 || MediaAdvanceSeconds == 0 || CleanupSeconds == 0
            || ObservationSeconds < StableSeconds || ObservationSeconds < MediaAdvanceSeconds)
        {
            throw new ArgumentException("Durations must be finite and positive; observation must allow stability and media advancement.");
        }
        if (ExistingInstance && (AllowMatrixRecovery || Matrices.Length > 0))
        {
            throw new ArgumentException("Existing-instance mode cannot select Docker matrices or permit deployment recovery.");
        }
        if (Include.Intersect(Exclude, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new ArgumentException("A channel cannot be both included and excluded.");
        }
    }

    /// <summary>Freezes a nonempty, unique projected inventory and rejects missing requested numbers.</summary>
    public IReadOnlyList<ChannelDefinition> Select(IReadOnlyList<ChannelDefinition> channels)
    {
        if (channels.Select(channel => $"{channel.VirtualDeviceId}|{channel.Number}").Distinct(StringComparer.OrdinalIgnoreCase).Count() != channels.Count)
        {
            throw new InvalidDataException("Projected channel keys are not unique.");
        }
        var missing = Include.Except(channels.Select(channel => channel.Number), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Requested channels are not projected: {string.Join(", ", missing)}.");
        }
        var selected = channels.Where(channel => (Include.Length == 0 || Include.Contains(channel.Number, StringComparer.OrdinalIgnoreCase))
            && !Exclude.Contains(channel.Number, StringComparer.OrdinalIgnoreCase)).Take(MaximumChannels).ToArray();
        return selected.Length > 0 ? selected : throw new InvalidDataException("Channel filters matched no projected channels.");
    }
}

namespace Lineup.Streaming.Sweep;

/// <summary>Stable machine-readable verdicts, independent of legacy report labels.</summary>
internal enum SweepVerdict
{
    /// <summary>Continuous decoded playback met the configured limits.</summary>
    StablePlayback,
    /// <summary>A structured source-data failure occurred.</summary>
    SourceUnavailable,
    /// <summary>The server reported startup expiry.</summary>
    StartupTimeout,
    /// <summary>The initial server operation failed.</summary>
    InitialServerFailure,
    /// <summary>A later startup failed after a successful source start.</summary>
    FallbackStartFailure,
    /// <summary>The server explicitly identified protected content.</summary>
    ConfirmedDrm,
    /// <summary>Protection was inferred, not established by the server.</summary>
    InferredDrm,
    /// <summary>A terminal decoder error occurred.</summary>
    DecoderFailure,
    /// <summary>Capacity cannot be treated as an ordinary channel result.</summary>
    CapacityContamination,
    /// <summary>Fresh active transport sequences exceeded the permitted delta.</summary>
    SynchronizationMisalignment,
    /// <summary>The observation deadline expired.</summary>
    ObservationTimeout,
    /// <summary>The harness could not establish mandatory evidence.</summary>
    InfrastructureFailure,
    /// <summary>Protection was reported without evidence establishing whether it was inferred.</summary>
    UnconfirmedDrm,
    /// <summary>The decoded transport is a generated disabled-channel slate, not a broadcast.</summary>
    DisabledSlate,
    /// <summary>Generated media was observed but its reason is not established.</summary>
    UnknownSynthetic
}

/// <summary>One frozen target projection.</summary>
internal sealed record SelectedChannel(string Key, string Target, ChannelDefinition Channel);

/// <summary>Every observed startup exchange, including fallback failures.</summary>
internal sealed record StartupExchange(string Id, StartupOutcome Outcome, string? SessionId, string? StopUrl, string? DashUrl, string? HlsUrl, string? Source);

/// <summary>One startup request, including operations interrupted before a response.</summary>
internal sealed record StartupRequestEvidence(string Id, string Endpoint, DateTimeOffset StartedUtc, string? SafeOptions)
{
    /// <summary>Gets its response exchange reference when one arrived.</summary>
    public string? ExchangeId { get; set; }
    /// <summary>Gets a bounded transport error or cancellation description.</summary>
    public string? Failure { get; set; }
}

/// <summary>One owned viewer identifier qualified by listener and runtime identity.</summary>
internal sealed record OwnedSession(string Origin, string Generation, string SessionId, string StopUrl, string InventoryUrl);

/// <summary>Independent cleanup evidence; emergency intervention is never a pass.</summary>
internal sealed record CleanupEvidence(bool NormalStopAttempted, bool Forced, bool VerifiedAbsent, IReadOnlyList<OwnedSession> Sessions, IReadOnlyList<string> Errors);

/// <summary>Fresh synchronization evidence; this is not an audible lip-sync measurement.</summary>
internal sealed record SynchronizationEvidence(string State, string? VideoRepresentation, string? AudioRepresentation, long? VideoHead, long? AudioHead, long? Delta, string Detail);

/// <summary>Immutable identity and evidence for one attempt, including unfinished work.</summary>
internal sealed record SweepAttempt
{
    /// <summary>Gets the globally unique attempt identity.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the frozen target/channel key.</summary>
    public required string Key { get; init; }
    /// <summary>Gets the observation purpose.</summary>
    public string Purpose { get; init; } = "playback";
    /// <summary>Gets the start timestamp.</summary>
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the terminal timestamp, if finished.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }
    /// <summary>Gets all startup exchanges in observation order.</summary>
    public List<StartupExchange> Starts { get; init; } = [];
    /// <summary>Gets all requests, including unfinished starts.</summary>
    public List<StartupRequestEvidence> Requests { get; init; } = [];
    /// <summary>Gets ownership checkpointed before observation continues.</summary>
    public List<OwnedSession> Ownership { get; init; } = [];
    /// <summary>Gets playback evidence.</summary>
    public ChannelAttemptResult? Result { get; set; }
    /// <summary>Gets the normalized verdict.</summary>
    public SweepVerdict Verdict { get; set; } = SweepVerdict.InfrastructureFailure;
    /// <summary>Gets cleanup evidence.</summary>
    public CleanupEvidence? Cleanup { get; set; }
    /// <summary>Gets synchronization evidence.</summary>
    public SynchronizationEvidence? Synchronization { get; set; }
    /// <summary>Gets the current owned public manifest used solely for representation metadata, including during HLS playback.</summary>
    public string? SynchronizationManifestUrl { get; set; }
    /// <summary>Gets the artifact directory relative to the run.</summary>
    public string? Artifacts { get; set; }
    /// <summary>Gets owned active-stream diagnostics, including observed source and codecs.</summary>
    public JsonNode? ActiveStatus { get; set; }
    /// <summary>Gets the passive pre-existing viewer/capacity baseline.</summary>
    public JsonNode? Baseline { get; set; }
    /// <summary>Gets the measured uninterrupted stable wall-clock interval.</summary>
    public double StableDurationSeconds { get; set; }
    /// <summary>Gets media progression within that uninterrupted interval.</summary>
    public double StableMediaAdvanceSeconds { get; set; }
    /// <summary>Gets browser crashes or uncaught script failures; these are infrastructure invariants.</summary>
    public List<string> BrowserFailures { get; init; } = [];
    /// <summary>Gets failures collecting mandatory startup/ownership evidence.</summary>
    public List<string> EvidenceFailures { get; init; } = [];
    /// <summary>Gets whether diagnostics explicitly identified synthetic rather than broadcast media.</summary>
    public bool? SyntheticPlayback { get; set; }
    /// <summary>Gets the synthetic source label from the current owned flow, never inferred from successful decoding.</summary>
    public string? SyntheticReason { get; set; }
    /// <summary>Gets the correlated current flow evidence, including the source stage.</summary>
    public JsonNode? ActiveFlow { get; set; }
    /// <summary>Gets whether interruption left playback unresolved rather than a fabricated accepted outcome.</summary>
    public bool Interrupted { get; set; }
}

/// <summary>Versioned authoritative run data. Reports contain no independently maintained facts.</summary>
internal sealed record SweepRun
{
    /// <summary>Gets the only supported canonical schema version.</summary>
    public const int CurrentSchema = 1;
    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchema;
    /// <summary>Gets the run identity.</summary>
    public required string Id { get; init; }
    /// <summary>Gets effective options.</summary>
    public required SweepOptions Options { get; init; }
    /// <summary>Gets the run state.</summary>
    public string State { get; set; } = "running";
    /// <summary>Gets the start time.</summary>
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the end time.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }
    /// <summary>Gets environment metadata.</summary>
    public Dictionary<string, string> Environment { get; init; } = [];
    /// <summary>Gets target fingerprints used to reject incompatible resumes.</summary>
    public Dictionary<string, string> Provenance { get; init; } = [];
    /// <summary>Gets the frozen inventory.</summary>
    public List<SelectedChannel> Selected { get; init; } = [];
    /// <summary>Gets every attempt, including abandoned and contaminated attempts.</summary>
    public List<SweepAttempt> Attempts { get; init; } = [];
    /// <summary>Gets separately identified synthetic compatibility operations and ownership.</summary>
    public List<SweepAttempt> Compatibility { get; init; } = [];
    /// <summary>Gets the accepted playback attempt per selected key.</summary>
    public Dictionary<string, string> Accepted { get; init; } = [];
    /// <summary>Gets independent strongest-caption attempt references without rewriting playback provenance.</summary>
    public Dictionary<string, string> CaptionAccepted { get; init; } = [];
    /// <summary>Gets permanent lifecycle and integrity violations.</summary>
    public List<string> Violations { get; init; } = [];
    /// <summary>Gets unavailable or restricted evidence.</summary>
    public List<string> Limitations { get; init; } = [];
    /// <summary>Gets exact derived aggregates, never manually incremented counters.</summary>
    public SweepAggregates Aggregates => new(Selected.Count, Accepted.Count, Selected.Count - Accepted.Count, Attempts.Count,
        Accepted.Values.Select(id => Attempts.SingleOrDefault(attempt => attempt.Id == id))
            .Count(attempt => attempt is { Verdict: SweepVerdict.StablePlayback, SyntheticPlayback: not true }),
        Attempts.Count(attempt => attempt.Cleanup?.Forced == true), Attempts.Count(attempt => attempt.Verdict == SweepVerdict.CapacityContamination));
}

/// <summary>Derived selected, accepted, pending, attempt, playback, forced-cleanup and contamination counts.</summary>
internal sealed record SweepAggregates(int Selected, int Accepted, int Pending, int Attempts, int PlaybackPassed, int ForcedCleanup, int ContaminatedAttempts);

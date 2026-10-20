namespace Lineup.Streaming.Sweep;

internal sealed record MatrixDefinition(
    string Id,
    int SourceManagementPort,
    int? OutputPort,
    int? ConsumerManagementPort,
    int? ConsumerOutputPort,
    HostingMode Mode,
    bool Remote)
{
    public string SourceUrl => $"http://127.0.0.1:{SourceManagementPort}";
    public string WatchUrl => Remote ? $"http://127.0.0.1:{ConsumerManagementPort}" : SourceUrl;
}

internal enum HostingMode
{
    Management,
    ManagedProcess,
    ExternalProcess
}

internal sealed record ChannelDefinition(string Number, string Name, string ProviderKey, string PhysicalDeviceId);
internal sealed record ChannelAttemptResult(
    string Matrix,
    string Channel,
    string Name,
    int Attempt,
    bool AvPassed,
    string CaptionResult,
    double DurationSeconds,
    string? Failure,
    PlayerMetrics? Metrics,
    StartupOutcome? Startup,
    int AttemptsExecuted,
    string? FailurePhase,
    string Category);
internal sealed record StartupOutcome(
    string Outcome,
    int? HttpStatus,
    string? Code,
    string? Stage,
    string? Message,
    DateTimeOffset RequestStartedUtc,
    DateTimeOffset ResponseReceivedUtc,
    long DurationMilliseconds,
    string? Endpoint,
    bool Succeeded,
    bool Retryable);
internal sealed record PlayerMetrics(
    double InitialTime,
    double FinalTime,
    int ReadyState,
    int VideoWidth,
    int VideoHeight,
    long? VideoBytesDelta,
    long? AudioBytesDelta,
    long? DecodedFramesDelta,
    long? DroppedFrames,
    int WaitingEvents,
    int StalledEvents,
    IReadOnlyList<string> MediaErrors,
    int? AudioPeak,
    int AudioSamples,
    string? AudioError,
    IReadOnlyList<TextTrackMetric> TextTracks);
internal sealed record TextTrackMetric(string Kind, string Label, string Language, string Mode, int ActiveCueCount, int CueCount);
internal sealed record DedicatedFailureResult(
    string? Matrix,
    bool FetchSucceeded,
    int? Status,
    string? CorsOrigin,
    JsonNode? Body,
    string? Error,
    IReadOnlyList<string> RequestFailures);
internal sealed record Issue4FailureCase(string Channel, int Status, string Code, string Error);
internal sealed record Issue4FailureResult(string Matrix, string Channel, string ExpectedCode, DedicatedFailureResult Response);
internal sealed record Issue6ErrorResult(string Matrix, double DurationSeconds, int SourceRequestCount, DedicatedFailureResult Response);
internal sealed record Issue7TeardownResult(string Matrix, string Teardown, double CleanupSeconds, int SourceActiveStreams, int ActiveBrokerStreams);
internal sealed record Issue8PresentationResult(
    string Matrix,
    string BrokerVideoCodec,
    string BrokerAudioCodec,
    string PresentationVideoCodec,
    string PresentationAudioCodec,
    string ManifestVideoCodec,
    string ManifestAudioCodec,
    int BrowserTextTracks,
    int StatusTextTracks,
    int SourceActiveStreams,
    int ActiveBrokerStreams);
internal sealed record Issue9IntegrationResult(
    string Matrix,
    bool AvPassed,
    string CaptionResult,
    int? FailureStatus,
    string? FailureCode,
    string? CorsOrigin,
    int SourceActiveStreams,
    int ActiveBrokerStreams);
internal sealed record Issue10AudioPlan(string SourceCodec, string OutputCodec);
internal sealed record Issue10IntegrationResult(
    string Matrix,
    int Attempt,
    bool AvPassed,
    long? AudioBytesDelta,
    int? AudioPeak,
    string SourceAudioCodec,
    string OutputAudioCodec,
    int SourceActiveStreams,
    int ActiveBrokerStreams);
internal sealed record ProcessResult(int ExitCode, string Output, string Error);

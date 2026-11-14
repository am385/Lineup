namespace Lineup.Integration.Streaming.Sweep;

/// <summary>Represents the matrix definition.</summary>
/// <param name="Id">The ID value.</param>
/// <param name="SourceManagementPort">The source management port value.</param>
/// <param name="OutputPort">The output port value.</param>
/// <param name="ConsumerManagementPort">The consumer management port value.</param>
/// <param name="ConsumerOutputPort">The consumer output port value.</param>
/// <param name="Mode">The mode value.</param>
/// <param name="Remote">The remote value.</param>
/// <param name="ExistingUrl">The existing URL value.</param>
internal sealed record MatrixDefinition(string Id, int SourceManagementPort, int? OutputPort, int? ConsumerManagementPort, int? ConsumerOutputPort, HostingMode Mode, bool Remote, string? ExistingUrl = null)
{
    /// <summary>Gets the source URL.</summary>
    public string SourceUrl => ExistingUrl ?? $"http://127.0.0.1:{SourceManagementPort}";
    /// <summary>Gets the watch URL.</summary>
    public string WatchUrl => ExistingUrl ?? (Remote ? $"http://127.0.0.1:{ConsumerManagementPort}" : SourceUrl);
}

/// <summary>Represents the hosting mode.</summary>
internal enum HostingMode
{
    /// <summary>Identifies the management option.</summary>
    Management,
    /// <summary>Identifies the managed process option.</summary>
    ManagedProcess,
    /// <summary>Identifies the external process option.</summary>
    ExternalProcess
}

/// <summary>Represents the channel definition.</summary>
/// <param name="Number">The number value.</param>
/// <param name="Name">The name value.</param>
/// <param name="ProviderKey">The provider key value.</param>
/// <param name="PhysicalDeviceId">The physical device ID value.</param>
internal sealed record ChannelDefinition(string Number, string Name, string ProviderKey, string PhysicalDeviceId)
{
    /// <summary>Gets the projected Web Player identity.</summary>
    public string VirtualDeviceId { get; init; } = string.Empty;
    /// <summary>Gets ordered source candidates, not just the first physical source.</summary>
    public IReadOnlyList<SourceCandidate> Sources { get; init; } = [];
}
/// <summary>One ordered physical source candidate in a virtual projection.</summary>
internal sealed record SourceCandidate(string PhysicalDeviceId, string ProviderKey);
/// <summary>Represents the channel attempt result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="Channel">The channel value.</param>
/// <param name="Name">The name value.</param>
/// <param name="Attempt">The attempt value.</param>
/// <param name="AvPassed">The av passed value.</param>
/// <param name="CaptionResult">The caption result value.</param>
/// <param name="DurationSeconds">The duration seconds value.</param>
/// <param name="Failure">The failure value.</param>
/// <param name="Metrics">The metrics value.</param>
/// <param name="Startup">The startup value.</param>
/// <param name="AttemptsExecuted">The attempts executed value.</param>
/// <param name="FailurePhase">The failure phase value.</param>
/// <param name="Category">The category value.</param>
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
    string Category
)
{
    /// <summary>Gets the immutable evidence identity used for this attempt's artifact directory.</summary>
    public string? EvidenceId { get; init; }
}
/// <summary>Represents the startup outcome.</summary>
/// <param name="Outcome">The outcome value.</param>
/// <param name="HttpStatus">The HTTP status value.</param>
/// <param name="Code">The code value.</param>
/// <param name="Stage">The stage value.</param>
/// <param name="Message">The message value.</param>
/// <param name="RequestStartedUtc">The request started UTC value.</param>
/// <param name="ResponseReceivedUtc">The response received UTC value.</param>
/// <param name="DurationMilliseconds">The duration milliseconds value.</param>
/// <param name="Endpoint">The endpoint value.</param>
/// <param name="Succeeded">The succeeded value.</param>
/// <param name="Retryable">The retryable value.</param>
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
    bool Retryable
)
{
    /// <summary>Gets explicit product inference evidence; absent evidence is not confirmation.</summary>
    public bool? Inferred { get; init; }
    /// <summary>Gets the product's protection evidence source, independently of request options.</summary>
    public string? Source { get; init; }
}
/// <summary>Represents the player metrics.</summary>
/// <param name="InitialTime">The initial time value.</param>
/// <param name="FinalTime">The final time value.</param>
/// <param name="ReadyState">The ready state value.</param>
/// <param name="VideoWidth">The video width value.</param>
/// <param name="VideoHeight">The video height value.</param>
/// <param name="VideoBytesDelta">The video bytes delta value.</param>
/// <param name="AudioBytesDelta">The audio bytes delta value.</param>
/// <param name="DecodedFramesDelta">The decoded frames delta value.</param>
/// <param name="DroppedFrames">The dropped frames value.</param>
/// <param name="WaitingEvents">The waiting events value.</param>
/// <param name="StalledEvents">The stalled events value.</param>
/// <param name="MediaErrors">The media errors value.</param>
/// <param name="AudioPeak">The audio peak value.</param>
/// <param name="AudioSamples">The audio samples value.</param>
/// <param name="AudioError">The audio error value.</param>
/// <param name="TextTracks">The text tracks value.</param>
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
    IReadOnlyList<TextTrackMetric> TextTracks
);
/// <summary>Represents the text track metric.</summary>
/// <param name="Kind">The kind value.</param>
/// <param name="Label">The label value.</param>
/// <param name="Language">The language value.</param>
/// <param name="Mode">The mode value.</param>
/// <param name="ActiveCueCount">The active cue count value.</param>
/// <param name="CueCount">The cue count value.</param>
internal sealed record TextTrackMetric(string Kind, string Label, string Language, string Mode, int ActiveCueCount, int CueCount);
/// <summary>Represents the dedicated failure result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="FetchSucceeded">The fetch succeeded value.</param>
/// <param name="Status">The status value.</param>
/// <param name="CorsOrigin">The cors origin value.</param>
/// <param name="Body">The body value.</param>
/// <param name="Error">The error value.</param>
/// <param name="RequestFailures">The request failures value.</param>
internal sealed record DedicatedFailureResult(string? Matrix, bool FetchSucceeded, int? Status, string? CorsOrigin, JsonNode? Body, string? Error, IReadOnlyList<string> RequestFailures);
/// <summary>Represents the issue4 failure case.</summary>
/// <param name="Channel">The channel value.</param>
/// <param name="Status">The status value.</param>
/// <param name="Code">The code value.</param>
/// <param name="Error">The error value.</param>
internal sealed record Issue4FailureCase(string Channel, int Status, string Code, string Error);
/// <summary>Represents the issue4 failure result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="Channel">The channel value.</param>
/// <param name="ExpectedCode">The expected code value.</param>
/// <param name="Response">The response value.</param>
internal sealed record Issue4FailureResult(string Matrix, string Channel, string ExpectedCode, DedicatedFailureResult Response);
/// <summary>Represents the issue6 error result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="DurationSeconds">The duration seconds value.</param>
/// <param name="SourceRequestCount">The source request count value.</param>
/// <param name="Response">The response value.</param>
internal sealed record Issue6ErrorResult(string Matrix, double DurationSeconds, int SourceRequestCount, DedicatedFailureResult Response);
/// <summary>Represents the issue7 teardown result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="Teardown">The teardown value.</param>
/// <param name="CleanupSeconds">The cleanup seconds value.</param>
/// <param name="SourceActiveStreams">The source active streams value.</param>
/// <param name="ActiveBrokerStreams">The active broker streams value.</param>
internal sealed record Issue7TeardownResult(string Matrix, string Teardown, double CleanupSeconds, int SourceActiveStreams, int ActiveBrokerStreams);
/// <summary>Represents the issue8 presentation result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="BrokerVideoCodec">The broker video codec value.</param>
/// <param name="BrokerAudioCodec">The broker audio codec value.</param>
/// <param name="PresentationVideoCodec">The presentation video codec value.</param>
/// <param name="PresentationAudioCodec">The presentation audio codec value.</param>
/// <param name="ManifestVideoCodec">The manifest video codec value.</param>
/// <param name="ManifestAudioCodec">The manifest audio codec value.</param>
/// <param name="BrowserTextTracks">The browser text tracks value.</param>
/// <param name="StatusTextTracks">The status text tracks value.</param>
/// <param name="SourceActiveStreams">The source active streams value.</param>
/// <param name="ActiveBrokerStreams">The active broker streams value.</param>
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
    int ActiveBrokerStreams
);
/// <summary>Represents the issue9 integration result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="AvPassed">The av passed value.</param>
/// <param name="CaptionResult">The caption result value.</param>
/// <param name="FailureStatus">The failure status value.</param>
/// <param name="FailureCode">The failure code value.</param>
/// <param name="CorsOrigin">The cors origin value.</param>
/// <param name="SourceActiveStreams">The source active streams value.</param>
/// <param name="ActiveBrokerStreams">The active broker streams value.</param>
internal sealed record Issue9IntegrationResult(string Matrix, bool AvPassed, string CaptionResult, int? FailureStatus, string? FailureCode, string? CorsOrigin, int SourceActiveStreams, int ActiveBrokerStreams);
/// <summary>Represents the issue10 audio plan.</summary>
/// <param name="SourceCodec">The source codec value.</param>
/// <param name="OutputCodec">The output codec value.</param>
internal sealed record Issue10AudioPlan(string SourceCodec, string OutputCodec);
/// <summary>Represents the issue10 integration result.</summary>
/// <param name="Matrix">The matrix value.</param>
/// <param name="Attempt">The attempt value.</param>
/// <param name="AvPassed">The av passed value.</param>
/// <param name="AudioBytesDelta">The audio bytes delta value.</param>
/// <param name="AudioPeak">The audio peak value.</param>
/// <param name="SourceAudioCodec">The source audio codec value.</param>
/// <param name="OutputAudioCodec">The output audio codec value.</param>
/// <param name="SourceActiveStreams">The source active streams value.</param>
/// <param name="ActiveBrokerStreams">The active broker streams value.</param>
internal sealed record Issue10IntegrationResult(
    string Matrix,
    int Attempt,
    bool AvPassed,
    long? AudioBytesDelta,
    int? AudioPeak,
    string SourceAudioCodec,
    string OutputAudioCodec,
    int SourceActiveStreams,
    int ActiveBrokerStreams
);
/// <summary>Represents the process result.</summary>
/// <param name="ExitCode">The exit code value.</param>
/// <param name="Output">The output value.</param>
/// <param name="Error">The error value.</param>
internal sealed record ProcessResult(int ExitCode, string Output, string Error);

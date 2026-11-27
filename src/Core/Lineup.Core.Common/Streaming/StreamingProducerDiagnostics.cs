namespace Lineup.Core.Common.Streaming;

/// <summary>Summarizes reusable outputs owned by one streaming producer.</summary>
public sealed record StreamingProducerDiagnostics(int ActiveProducerCount, int WarmProducerCount, int ReusableOutputCount)
{
    /// <summary>Gets individually identified producers, including retained outputs without viewers.</summary>
    public IReadOnlyList<StreamingProducerOutputDiagnostics> Outputs { get; init; } = [];
}

/// <summary>Describes one reusable output without retaining any consumer or process ownership.</summary>
public sealed record StreamingProducerOutputDiagnostics(StreamSessionId OutputId, int ReceiverCount, bool IsWarm)
{
    /// <summary>Gets the producer's last available source and processing attribution.</summary>
    public StreamingProducerContext? Context { get; init; }
}

/// <summary>Provides passive attribution for a producer independently of its viewers.</summary>
public sealed record StreamingProducerContext(ActiveStreamFlowContext? Flow, string Channel)
{
    /// <summary>Gets effective codec and transport operations retained by this producer.</summary>
    public StreamProcessingSnapshot? Processing { get; init; }

    /// <summary>Gets the actual encoder reported by the producer.</summary>
    public VideoEncodingSnapshot? VideoEncoding { get; init; }

    /// <summary>Gets the producer's process resource identities.</summary>
    public IReadOnlyList<string> ResourceIds { get; init; } = [];
}

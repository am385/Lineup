namespace Lineup.Common.Streaming;

/// <summary>Summarizes reusable outputs owned by one streaming producer.</summary>
public sealed record StreamingProducerDiagnostics(int ActiveProducerCount, int WarmProducerCount, int ReusableOutputCount);

namespace Lineup.Core.Common.Streaming;

/// <summary>
/// Describes one publishable virtual channel and its ordered physical candidates.
/// </summary>
/// <param name="Number">Effective published channel number.</param>
/// <param name="Name">Effective published channel name.</param>
/// <param name="Sources">Ordered physical source candidates.</param>
public sealed record ResolvedVirtualChannel(string Number, string Name, IReadOnlyList<SourceChannelId> Sources)
{
    /// <summary>Gets preferred-source metadata with protection aggregated across the enabled, subscribed equivalent sources.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

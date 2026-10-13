namespace Lineup.Common.Streaming;

/// <summary>
/// Describes one publishable virtual channel and its ordered physical candidates.
/// </summary>
/// <param name="Number">Effective published channel number.</param>
/// <param name="Name">Effective published channel name.</param>
/// <param name="Sources">Ordered physical source candidates.</param>
public sealed record ResolvedVirtualChannel(string Number, string Name, IReadOnlyList<SourceChannelId> Sources)
{
    /// <summary>Gets provider-neutral metadata from the preferred physical source.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

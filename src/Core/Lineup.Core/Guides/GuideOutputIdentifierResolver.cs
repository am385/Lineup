using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.Core.Guides;

/// <summary>
/// Resolves enabled custom guide outputs by stable identity or code-safe key.
/// </summary>
public static class GuideOutputIdentifierResolver
{
    /// <summary>Resolves one enabled custom output from an API route identifier.</summary>
    public static GuideOutputDefinition? Resolve(
        IEnumerable<GuideOutputDefinition> outputs,
        string identifier)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var customOutputs = outputs.Where(output => output.Enabled && !output.IsBuiltIn);
        if (Guid.TryParse(identifier, out var parsedId))
        {
            return customOutputs.SingleOrDefault(output => output.Id == new GuideOutputId(parsedId));
        }
        return customOutputs.SingleOrDefault(
            output => string.Equals(output.Key, identifier, StringComparison.OrdinalIgnoreCase));
    }
}

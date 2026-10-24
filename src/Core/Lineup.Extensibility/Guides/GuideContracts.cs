using System.Globalization;
using System.Text;
using Lineup.Common.Guides;
using Lineup.Common.Streaming;

namespace Lineup.Extensibility.Guides;

/// <summary>
/// Creates and validates stable code-safe guide-output keys.
/// </summary>
public static class GuideOutputKey
{
    /// <summary>Maximum persisted key length.</summary>
    public const int MaximumLength = 64;

    /// <summary>Creates a lowercase ASCII key from a display name.</summary>
    public static string Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new StringBuilder();
        var separatorPending = false;
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separatorPending && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = builder.Length > 0;
            }
            if (builder.Length >= MaximumLength)
            {
                break;
            }
        }

        var key = builder.ToString().TrimEnd('_');
        if (key.Length == 0)
        {
            key = "guide_output";
        }
        if (key[0] is < 'a' or > 'z')
        {
            key = $"output_{key}";
        }
        return key[..Math.Min(key.Length, MaximumLength)].TrimEnd('_');
    }

    /// <summary>Creates a unique key using a numeric suffix when necessary.</summary>
    public static string CreateUnique(string name, IEnumerable<string> existingKeys)
    {
        ArgumentNullException.ThrowIfNull(existingKeys);
        var used = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseKey = Create(name);
        if (!used.Contains(baseKey))
        {
            return baseKey;
        }

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $"_{suffix}";
            var prefixLength = Math.Min(baseKey.Length, MaximumLength - suffixText.Length);
            var candidate = $"{baseKey[..prefixLength].TrimEnd('_')}{suffixText}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Gets whether a key is a lowercase ASCII identifier safe for paths and URL segments.</summary>
    public static bool IsValid(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumLength || key[0] is < 'a' or > 'z')
        {
            return false;
        }
        return key.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }
}

/// <summary>
/// Runtime instance of one configured guide source.
/// </summary>
public interface IGuideSource
{
    /// <summary>Gets the source identity.</summary>
    GuideSourceId Id { get; }

    /// <summary>Refreshes a complete atomic import.</summary>
    Task<GuideImport> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates guide sources for one adapter kind.
/// </summary>
public interface IGuideSourceDriver
{
    /// <summary>Gets the stable adapter kind.</summary>
    string Kind { get; }

    /// <summary>Gets the user-facing guide-source type name.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether users can create additional sources of this type.</summary>
    bool SupportsUserCreation { get; }

    /// <summary>Creates the default adapter configuration for a new guide source.</summary>
    AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId);

    /// <summary>Gets whether this guide adapter can consume one physical adapter kind.</summary>
    bool SupportsPhysicalAdapter(string physicalAdapterKind);

    /// <summary>Validates adapter configuration and returns all errors.</summary>
    IReadOnlyList<string> Validate(AdapterConfiguration configuration);

    /// <summary>Creates a guide source subscribed to the resolved active physical devices.</summary>
    ValueTask<IGuideSource> CreateAsync(
        GuideSourceId sourceId,
        AdapterConfiguration configuration,
        IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configures one guide-output adapter kind.
/// </summary>
public interface IGuideOutputDriver
{
    /// <summary>Gets the stable adapter kind.</summary>
    string Kind { get; }

    /// <summary>Gets the user-facing output type name.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether users can create additional outputs of this type.</summary>
    bool SupportsUserCreation { get; }

    /// <summary>Gets the guide-publisher kind used to serialize this output.</summary>
    string PublisherKind { get; }

    /// <summary>Creates the default adapter configuration for a new output.</summary>
    AdapterConfiguration CreateDefaultConfiguration(GuideOutputId outputId, GuideOutputCreationContext context);

    /// <summary>Validates adapter configuration and returns all errors.</summary>
    IReadOnlyList<string> Validate(AdapterConfiguration configuration);

    /// <summary>Resolves the configured publication destination.</summary>
    GuideOutputDestination ResolveDestination(AdapterConfiguration configuration);
}

/// <summary>
/// Publishes one resolved virtual guide snapshot.
/// </summary>
public interface IGuidePublisher
{
    /// <summary>Gets the stable publisher kind.</summary>
    string Kind { get; }

    /// <summary>Publishes the supplied virtual guide snapshot.</summary>
    Task PublishAsync(VirtualGuideSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default);
}

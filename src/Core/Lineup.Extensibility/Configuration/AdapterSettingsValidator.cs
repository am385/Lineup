using Lineup.Common.Configuration;

namespace Lineup.Extensibility.Configuration;

/// <summary>
/// Validates frontend-neutral adapter settings descriptors and snapshots.
/// </summary>
public static class AdapterSettingsValidator
{
    /// <summary>
    /// Validates a plugin settings descriptor.
    /// </summary>
    /// <param name="descriptor">Descriptor to validate.</param>
    /// <returns>Validation errors. An empty collection indicates a valid descriptor.</returns>
    public static IReadOnlyList<string> ValidateDescriptor(AdapterSettingsDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(descriptor.AdapterKind))
        {
            errors.Add("Adapter kind is required.");
        }

        var fieldsByKey = new Dictionary<string, AdapterSettingDescriptor>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                errors.Add("Every field must have a non-empty key.");
                continue;
            }

            if (!fieldsByKey.TryAdd(field.Key, field))
            {
                errors.Add($"Field key '{field.Key}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(field.Label))
            {
                errors.Add($"Field '{field.Key}' must have a non-empty label.");
            }

            ValidateBounds(field, errors);
            ValidateChoices(field, errors);
        }

        foreach (var field in descriptor.Fields)
        {
            foreach (var condition in field.VisibleWhen)
            {
                if (string.IsNullOrWhiteSpace(condition.FieldKey) || !fieldsByKey.ContainsKey(condition.FieldKey))
                {
                    errors.Add($"Field '{field.Key}' has a visibility condition referencing unknown field '{condition.FieldKey}'.");
                }
                else if (string.Equals(field.Key, condition.FieldKey, StringComparison.Ordinal))
                {
                    errors.Add($"Field '{field.Key}' cannot control its own visibility.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates a settings snapshot against its descriptor.
    /// </summary>
    /// <param name="descriptor">Descriptor defining the expected snapshot.</param>
    /// <param name="snapshot">Snapshot to validate.</param>
    /// <returns>Validation errors. An empty collection indicates a valid snapshot.</returns>
    public static IReadOnlyList<string> ValidateSnapshot(AdapterSettingsDescriptor descriptor, AdapterSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);

        var errors = new List<string>();
        if (!string.Equals(descriptor.AdapterKind, snapshot.AdapterKind, StringComparison.Ordinal))
        {
            errors.Add($"Snapshot adapter kind '{snapshot.AdapterKind}' does not match descriptor kind '{descriptor.AdapterKind}'.");
        }

        var fieldsByKey = descriptor.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .GroupBy(field => field.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var snapshotKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in snapshot.Values)
        {
            if (!snapshotKeys.Add(value.Key))
            {
                errors.Add($"Snapshot key '{value.Key}' is duplicated.");
                continue;
            }

            if (!fieldsByKey.TryGetValue(value.Key, out var field))
            {
                errors.Add($"Snapshot key '{value.Key}' is not defined by the descriptor.");
                continue;
            }

            if (field.Kind is AdapterSettingKind.Secret or AdapterSettingKind.GeneratedSecret && value.Value.HasValue)
            {
                errors.Add($"Secret field '{field.Key}' must not expose its persisted value.");
            }
        }

        foreach (var fieldKey in fieldsByKey.Keys)
        {
            if (!snapshotKeys.Contains(fieldKey))
            {
                errors.Add($"Snapshot is missing field '{fieldKey}'.");
            }
        }

        return errors;
    }

    private static void ValidateBounds(AdapterSettingDescriptor field, ICollection<string> errors)
    {
        if ((field.Minimum.HasValue || field.Maximum.HasValue) && field.Kind != AdapterSettingKind.Integer)
        {
            errors.Add($"Field '{field.Key}' defines integer bounds but is not an integer field.");
        }

        if (field.Minimum.HasValue && field.Maximum.HasValue && field.Minimum.Value > field.Maximum.Value)
        {
            errors.Add($"Field '{field.Key}' has a minimum greater than its maximum.");
        }
    }

    private static void ValidateChoices(AdapterSettingDescriptor field, ICollection<string> errors)
    {
        if (field.Kind != AdapterSettingKind.Choice)
        {
            if (field.Choices.Count > 0)
            {
                errors.Add($"Field '{field.Key}' defines choices but is not a choice field.");
            }

            return;
        }

        if (field.Choices.Count == 0)
        {
            errors.Add($"Choice field '{field.Key}' must define at least one choice.");
            return;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in field.Choices)
        {
            if (string.IsNullOrWhiteSpace(choice.Value))
            {
                errors.Add($"Choice field '{field.Key}' contains an empty value.");
            }
            else if (!values.Add(choice.Value))
            {
                errors.Add($"Choice field '{field.Key}' contains duplicate value '{choice.Value}'.");
            }

            if (string.IsNullOrWhiteSpace(choice.Label))
            {
                errors.Add($"Choice field '{field.Key}' contains an empty label.");
            }
        }
    }
}

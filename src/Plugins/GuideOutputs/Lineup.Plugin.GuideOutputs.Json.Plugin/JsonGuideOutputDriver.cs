using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;

namespace Lineup.Plugin.GuideOutputs.Json.Plugin;

/// <summary>Configures JSON guide publication.</summary>
public sealed class JsonGuideOutputDriver : IGuideOutputDriver, IAdapterSettingsProvider
{
    /// <summary>Stable adapter and publisher kind.</summary>
    public const string AdapterKind = "json";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Gets the kind.</summary>
    public string Kind => AdapterKind;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => "JSON";

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation => true;

    /// <summary>Gets the publisher kind.</summary>
    public string PublisherKind => AdapterKind;

    /// <summary>Gets the settings descriptor.</summary>
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = AdapterKind,
        Fields =
        [
            new AdapterSettingDescriptor
            {
                Key = "outputDirectoryPath",
                Label = "Output directory",
                Description = "Directory where Lineup publishes this JSON guide using its output key.",
                Kind = AdapterSettingKind.DirectoryPath,
                IsRequired = true,
                CanClear = false
            }
        ]
    };

    /// <summary>Creates default configuration.</summary>
    /// <param name="outputId">The output ID value.</param>
    /// <param name="context">Context used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(GuideOutputId outputId, GuideOutputCreationContext context)
    {
        if (outputId.Value == Guid.Empty)
        {
            throw new ArgumentException("A JSON guide-output identifier is required.", nameof(outputId));
        }
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.DefaultOutputDirectoryPath))
        {
            throw new ArgumentException("A default guide-output directory is required.", nameof(context));
        }
        if (!GuideOutputKey.IsValid(context.OutputKey))
        {
            throw new ArgumentException("A valid guide-output key is required.", nameof(context));
        }

        return CreateConfiguration(Path.Combine(context.DefaultOutputDirectoryPath, $"lineup-{context.OutputKey}.json"));
    }

    /// <summary>Gets settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration)
    {
        var typed = Deserialize(configuration);
        var directory = string.IsNullOrWhiteSpace(typed.OutputPath)
            ? string.Empty
            : Path.GetDirectoryName(Path.GetFullPath(typed.OutputPath)) ?? string.Empty;
        return new AdapterSettingsSnapshot
        {
            AdapterKind = Kind,
            Values =
            [
                new AdapterSettingSnapshot
                {
                    Key = "outputDirectoryPath",
                    Value = JsonSerializer.SerializeToElement(directory),
                    HasValue = !string.IsNullOrWhiteSpace(directory)
                }
            ]
        };
    }

    /// <summary>Applies settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="edits">The edits value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsApplyResult ApplySettings(AdapterConfiguration configuration, IReadOnlyList<AdapterSettingEdit> edits)
    {
        var outputPath = Deserialize(configuration).OutputPath;
        var outputFileName = Path.GetFileName(outputPath);
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!string.Equals(edit.Key, "outputDirectoryPath", StringComparison.Ordinal))
            {
                errors[edit.Key] = ["Unknown setting."];
                continue;
            }
            if (edit.Action == AdapterSettingEditAction.Preserve)
            {
                continue;
            }
            if (edit.Action != AdapterSettingEditAction.Set ||
                edit.Value is not JsonElement value ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                errors[edit.Key] = ["Output directory is required."];
                continue;
            }
            outputPath = Path.Combine(value.GetString()!.Trim(), outputFileName);
        }

        if (errors.Count > 0)
        {
            return new AdapterSettingsApplyResult { Errors = errors };
        }

        var updated = CreateConfiguration(outputPath);
        var validation = Validate(updated);
        return validation.Count == 0
            ? new AdapterSettingsApplyResult { Configuration = updated }
            : new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["outputDirectoryPath"] = validation
                }
            };
    }

    /// <summary>Validates the JSON guide output driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            return [$"Expected adapter kind '{Kind}'."];
        }

        JsonGuideOutputConfiguration typed;
        try
        {
            typed = Deserialize(configuration);
        }
        catch (JsonException)
        {
            return ["JSON guide-output configuration is invalid."];
        }

        if (string.IsNullOrWhiteSpace(typed.OutputPath))
        {
            return ["JSON output path is required."];
        }
        try
        {
            _ = Path.GetFullPath(typed.OutputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ["JSON output path is invalid."];
        }
        return [];
    }

    /// <summary>Resolves destination.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public GuideOutputDestination ResolveDestination(AdapterConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        var path = Path.GetFullPath(Deserialize(configuration).OutputPath);
        return new GuideOutputDestination
        {
            Path = path,
            ContentType = "application/json",
            FileName = Path.GetFileName(path)
        };
    }

    private static AdapterConfiguration CreateConfiguration(string outputPath) =>
        new(AdapterKind, JsonSerializer.SerializeToElement(new JsonGuideOutputConfiguration
        {
            OutputPath = outputPath
        }));

    private static JsonGuideOutputConfiguration Deserialize(AdapterConfiguration configuration) =>
        configuration.Configuration.Deserialize<JsonGuideOutputConfiguration>(_serializerOptions)
            ?? new JsonGuideOutputConfiguration();
}

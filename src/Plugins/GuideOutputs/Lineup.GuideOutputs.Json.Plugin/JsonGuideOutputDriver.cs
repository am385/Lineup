using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.GuideOutputs.Json.Plugin;

/// <summary>Configures JSON guide publication.</summary>
public sealed class JsonGuideOutputDriver : IGuideOutputDriver, IAdapterSettingsProvider
{
    /// <summary>Stable adapter and publisher kind.</summary>
    public const string AdapterKind = "json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public string Kind => AdapterKind;

    /// <inheritdoc />
    public string DisplayName => "JSON";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public string PublisherKind => AdapterKind;

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
        configuration.Configuration.Deserialize<JsonGuideOutputConfiguration>(SerializerOptions)
            ?? new JsonGuideOutputConfiguration();
}

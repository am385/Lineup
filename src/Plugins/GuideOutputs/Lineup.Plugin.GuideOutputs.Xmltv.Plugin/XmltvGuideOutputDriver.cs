using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Plugin.GuideOutputs.Xmltv.Plugin;

/// <summary>
/// Configures XMLTV guide publication and owns the built-in global output invariant.
/// </summary>
public sealed class XmltvGuideOutputDriver :
    IGuideOutputDriver,
    IAdapterSettingsProvider,
    IStreamingConfigurationNormalizer
{
    /// <summary>Stable adapter kind.</summary>
    public const string AdapterKind = "xmltv";

    /// <summary>Fixed name for the built-in global output.</summary>
    public const string BuiltInName = "Global XMLTV";

    /// <summary>Stable built-in global output identity.</summary>
    public static readonly GuideOutputId BuiltInId = new(new Guid("4e08bd0d-15c4-442d-bf9f-0f6b4502fc61"));

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Gets the kind.</summary>
    public string Kind => AdapterKind;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => "XMLTV";

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
                Description = "Directory where Lineup publishes this XMLTV document using its output key.",
                Kind = AdapterSettingKind.DirectoryPath,
                IsRequired = true,
                CanClear = false
            },
            new AdapterSettingDescriptor
            {
                Key = "includeUnavailablePrograms",
                Label = "Include unavailable program placeholders",
                Description = "Publish a Not Available program for channels without guide data. Disabled by default to keep placeholders out of Jellyfin's On Now listings.",
                Kind = AdapterSettingKind.Boolean,
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
            throw new ArgumentException("An XMLTV guide-output identifier is required.", nameof(outputId));
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

        return CreateConfiguration(Path.Combine(context.DefaultOutputDirectoryPath, $"lineup-{context.OutputKey}.xml"));
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
                },
                new AdapterSettingSnapshot
                {
                    Key = "includeUnavailablePrograms",
                    Value = JsonSerializer.SerializeToElement(typed.IncludeUnavailablePrograms),
                    HasValue = true
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
        var current = Deserialize(configuration);
        var outputPath = current.OutputPath;
        var includeUnavailablePrograms = current.IncludeUnavailablePrograms;
        var outputFileName = Path.GetFileName(outputPath);
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (string.Equals(edit.Key, "includeUnavailablePrograms", StringComparison.Ordinal))
            {
                if (edit.Action == AdapterSettingEditAction.Preserve)
                {
                    continue;
                }
                if (edit.Action != AdapterSettingEditAction.Set ||
                    edit.Value is not JsonElement boolean ||
                    boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors[edit.Key] = ["Include unavailable programs must be true or false."];
                    continue;
                }
                includeUnavailablePrograms = boolean.GetBoolean();
                continue;
            }
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

        var updated = new AdapterConfiguration(Kind, JsonSerializer.SerializeToElement(current with
        {
            OutputPath = outputPath,
            IncludeUnavailablePrograms = includeUnavailablePrograms
        }));
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

    /// <summary>Validates the XMLTV guide output driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Expected adapter kind '{Kind}'.");
            return errors;
        }

        XmltvGuideOutputConfiguration typed;
        try
        {
            typed = Deserialize(configuration);
        }
        catch (JsonException)
        {
            return ["XMLTV guide-output configuration is invalid."];
        }

        if (string.IsNullOrWhiteSpace(typed.OutputPath))
        {
            errors.Add("XMLTV output path is required.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(typed.OutputPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add("XMLTV output path is invalid.");
            }
        }
        return errors;
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
            ContentType = "application/xml",
            FileName = Path.GetFileName(path)
        };
    }

    /// <summary>Executes normalize.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="context">Context used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public StreamingConfiguration Normalize(StreamingConfiguration configuration, StreamingConfigurationInitializationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        if (configuration.GuideOutputs.Any(IsBuiltIn))
        {
            return configuration;
        }

        return configuration with
        {
            SchemaVersion = StreamingConfiguration.CurrentSchemaVersion,
            GuideOutputs =
            [
                CreateBuiltIn(context.DefaultXmltvOutputPath),
                .. configuration.GuideOutputs
            ]
        };
    }

    /// <summary>Creates the built-in global XMLTV output.</summary>
    public static GuideOutputDefinition CreateBuiltIn(string outputPath) => new()
    {
        Id = BuiltInId,
        Name = BuiltInName,
        Key = "global",
        IsBuiltIn = true,
        Adapter = CreateConfiguration(outputPath)
    };

    private static bool IsBuiltIn(GuideOutputDefinition output) =>
        output.Id == BuiltInId ||
        output.IsBuiltIn && string.Equals(output.Adapter.Kind, AdapterKind, StringComparison.OrdinalIgnoreCase);

    private static AdapterConfiguration CreateConfiguration(string outputPath) =>
        new(AdapterKind, JsonSerializer.SerializeToElement(new XmltvGuideOutputConfiguration
        {
            OutputPath = outputPath
        }));

    private static XmltvGuideOutputConfiguration Deserialize(AdapterConfiguration configuration) =>
        configuration.Configuration.Deserialize<XmltvGuideOutputConfiguration>(_serializerOptions)
            ?? new XmltvGuideOutputConfiguration();
}

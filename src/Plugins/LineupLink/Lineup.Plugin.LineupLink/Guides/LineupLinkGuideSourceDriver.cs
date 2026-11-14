using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.LineupLink.Physical;
using Lineup.Plugin.LineupLink.Protocol;

namespace Lineup.Plugin.LineupLink.Guides;

/// <summary>Creates guide sources backed by active Lineup Link physical devices.</summary>
public sealed class LineupLinkGuideSourceDriver : IGuideSourceDriver, IAdapterSettingsProvider, IGuideSourceRefreshPolicy
{
    private readonly IPhysicalDeviceResolver _devices;

    /// <summary>Gets refresh interval.</summary>
    /// <param name="configuredInterval">The configured interval value.</param>
    /// <returns>The operation's result.</returns>
    public TimeSpan GetRefreshInterval(TimeSpan configuredInterval) => configuredInterval;

    /// <summary>Initializes the Lineup Link guide driver.</summary>
    public LineupLinkGuideSourceDriver(IPhysicalDeviceResolver devices)
    {
        _devices = devices;
    }

    /// <summary>Gets the kind.</summary>
    public string Kind => LineupLinkProtocol.AdapterKind;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => "Lineup Link";

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation => true;

    /// <summary>Gets the settings descriptor.</summary>
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = LineupLinkProtocol.AdapterKind
    };

    /// <summary>Creates default configuration.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new { }));

    /// <summary>Gets settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = Kind
    };

    /// <summary>Applies settings.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="edits">The edits value.</param>
    /// <returns>The operation's result.</returns>
    public AdapterSettingsApplyResult ApplySettings(AdapterConfiguration configuration, IReadOnlyList<AdapterSettingEdit> edits)
    {
        if (edits.Any(edit => edit.Action != AdapterSettingEditAction.Preserve))
        {
            return new AdapterSettingsApplyResult
            {
                Errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    [""] = ["Lineup Link guide sources do not accept adapter-specific settings."]
                }
            };
        }
        return new AdapterSettingsApplyResult { Configuration = configuration };
    }

    /// <summary>Determines support for physical adapter.</summary>
    /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
    /// <returns>The operation's result.</returns>
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the Lineup Link guide source driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        if (!string.Equals(configuration.Kind, Kind, StringComparison.Ordinal))
        {
            errors.Add($"Adapter kind must be '{Kind}'.");
        }
        if (configuration.Configuration.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Lineup Link guide configuration must be a JSON object.");
        }
        else if (configuration.Configuration.EnumerateObject().Any())
        {
            errors.Add("Lineup Link guide configuration does not support adapter-specific properties.");
        }
        return errors;
    }

    /// <summary>Creates the Lineup Link guide source driver asynchronously.</summary>
    /// <param name="sourceId">The source ID value.</param>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="physicalDeviceIds">The physical device IDs value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }
        if (physicalDeviceIds.Count == 0)
        {
            throw new ArgumentException("A Lineup Link guide source requires at least one physical link.", nameof(physicalDeviceIds));
        }

        var capabilities = physicalDeviceIds.Select(deviceId =>
        {
            var device = _devices.GetRequired(deviceId);
            return device as ILineupLinkGuideCapability
                ?? throw new InvalidOperationException($"Physical device {deviceId} does not provide a Lineup Link guide.");
        }).ToArray();
        return ValueTask.FromResult<IGuideSource>(new LineupLinkGuideSource(sourceId, capabilities));
    }

    private sealed class LineupLinkGuideSource(GuideSourceId sourceId, IReadOnlyList<ILineupLinkGuideCapability> devices) : IGuideSource
    {
        /// <summary>Gets the ID.</summary>
        public GuideSourceId Id { get; } = sourceId;

        /// <summary>Refreshes the Lineup Link guide source asynchronously.</summary>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public async Task<GuideImport> RefreshAsync(CancellationToken cancellationToken = default)
        {
            var channels = new Dictionary<string, GuideChannel>(StringComparer.Ordinal);
            var programs = new List<GuideProgram>();
            var supplemental = new List<string>();
            foreach (var device in devices)
            {
                var guide = await device.GetGuideAsync(cancellationToken);
                foreach (var channel in guide.Channels)
                {
                    var providerKey = LineupLinkIdentity.CreateProviderKey(device.Manifest.InstanceId, device.Manifest.VirtualDeviceId, channel.Key);
                    var channelId = new GuideChannelId(Id, providerKey);
                    channels.TryAdd(providerKey, new GuideChannel
                    {
                        Id = channelId,
                        Number = channel.Number,
                        Name = channel.Name,
                        Affiliate = channel.Affiliate,
                        ImageUrl = channel.ImageUrl,
                        IsProtected = channel.IsProtected,
                        IsFavorite = channel.IsFavorite,
                        SupplementalXml = channel.SupplementalXml
                    });
                }
                foreach (var program in guide.Programs)
                {
                    var providerKey = LineupLinkIdentity.CreateProviderKey(device.Manifest.InstanceId, device.Manifest.VirtualDeviceId, program.ChannelKey);
                    if (!channels.ContainsKey(providerKey))
                    {
                        continue;
                    }
                    programs.Add(new GuideProgram
                    {
                        ChannelId = new GuideChannelId(Id, providerKey),
                        StartUtc = program.StartUtc,
                        EndUtc = program.EndUtc,
                        Title = program.Title,
                        Synopsis = program.Synopsis,
                        EpisodeTitle = program.EpisodeTitle,
                        ImageUrl = program.ImageUrl,
                        PosterUrl = program.PosterUrl,
                        EpisodeNumber = program.EpisodeNumber,
                        OriginalAirdateUtc = program.OriginalAirdateUtc,
                        IsFirstAiring = program.IsFirstAiring,
                        SeriesId = program.SeriesId,
                        Categories = program.Categories,
                        Metadata = program.Metadata,
                        SupplementalXml = program.SupplementalXml
                    });
                }
                if (!string.IsNullOrWhiteSpace(guide.SupplementalXml))
                {
                    supplemental.Add(guide.SupplementalXml);
                }
            }

            var rootMetadata = supplemental.Select(XmltvSupplementalMetadata.Parse).OfType<XmltvSupplementalMetadata>().ToArray();
            var now = DateTimeOffset.UtcNow;
            return new GuideImport
            {
                SourceId = Id,
                Channels = channels.Values.OrderBy(channel => channel.Number, ChannelNumberComparer.Instance).ToArray(),
                Programs = programs.OrderBy(program => program.StartUtc).ToArray(),
                CoverageStartUtc = programs.Count == 0 ? now : programs.Min(program => program.StartUtc),
                CoverageEndUtc = programs.Count == 0 ? now.AddHours(1) : programs.Max(program => program.EndUtc),
                SupplementalXml = XmltvSupplementalMetadata.Create(
                    rootMetadata.SelectMany(metadata => metadata.Attributes).DistinctBy(attribute => attribute.Name),
                    rootMetadata.SelectMany(metadata => metadata.Elements))
            };
        }
    }
}

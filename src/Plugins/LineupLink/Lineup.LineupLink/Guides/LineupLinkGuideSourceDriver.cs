using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Guides;

/// <summary>Creates guide sources backed by active Lineup Link physical devices.</summary>
public sealed class LineupLinkGuideSourceDriver : IGuideSourceDriver, IAdapterSettingsProvider
{
    private readonly IPhysicalDeviceResolver _devices;

    /// <summary>Initializes the Lineup Link guide driver.</summary>
    public LineupLinkGuideSourceDriver(IPhysicalDeviceResolver devices)
    {
        _devices = devices;
    }

    /// <inheritdoc />
    public string Kind => LineupLinkProtocol.AdapterKind;

    /// <inheritdoc />
    public string DisplayName => "Lineup Link";

    /// <inheritdoc />
    public bool SupportsUserCreation => true;

    /// <inheritdoc />
    public AdapterSettingsDescriptor SettingsDescriptor { get; } = new()
    {
        AdapterKind = LineupLinkProtocol.AdapterKind
    };

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) =>
        new(Kind, JsonSerializer.SerializeToElement(new { }));

    /// <inheritdoc />
    public AdapterSettingsSnapshot GetSettings(AdapterConfiguration configuration) => new()
    {
        AdapterKind = Kind
    };

    /// <inheritdoc />
    public AdapterSettingsApplyResult ApplySettings(
        AdapterConfiguration configuration,
        IReadOnlyList<AdapterSettingEdit> edits)
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

    /// <inheritdoc />
    public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
        string.Equals(physicalAdapterKind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public ValueTask<IGuideSource> CreateAsync(
        GuideSourceId sourceId,
        AdapterConfiguration configuration,
        IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
        CancellationToken cancellationToken = default)
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
        public GuideSourceId Id { get; } = sourceId;

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

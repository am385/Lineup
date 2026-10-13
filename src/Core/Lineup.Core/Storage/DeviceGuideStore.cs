using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Storage.Entities;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lineup.Core.Storage;

/// <summary>
/// Describes the authoritative guide selection and matching candidates for one physical source channel.
/// </summary>
public sealed record SourceGuideBindingState
{
    /// <summary>Gets the physical source channel.</summary>
    public required SourceChannelId SourceChannelId { get; init; }

    /// <summary>Gets the current binding resolution.</summary>
    public required GuideBindingState State { get; init; }

    /// <summary>Gets the selected guide channel, including an unavailable manual selection when known.</summary>
    public GuideChannelId? SelectedGuideChannelId { get; init; }

    /// <summary>Gets the selected guide-channel details, including an unavailable manual selection when known.</summary>
    public GuideChannel? SelectedGuideChannel { get; init; }

    /// <summary>Gets every active eligible guide channel available for automatic or manual selection.</summary>
    public required IReadOnlyList<GuideChannel> MatchingCandidates { get; init; }

    /// <summary>Gets whether multiple matching candidates require an explicit authoritative choice.</summary>
    public bool IsAmbiguous => State == GuideBindingState.NeedsGuideSelection;
}

/// <summary>
/// Describes the last successfully committed import for one guide source.
/// </summary>
public sealed record GuideSourceImportState
{
    /// <summary>Gets the configured guide source.</summary>
    public required GuideSourceId GuideSourceId { get; init; }

    /// <summary>Gets when source data was fetched.</summary>
    public required DateTime FetchedAtUtc { get; init; }

    /// <summary>Gets when the import transaction committed.</summary>
    public required DateTime CommittedAtUtc { get; init; }

    /// <summary>Gets the earliest covered program time.</summary>
    public required DateTimeOffset CoverageStartUtc { get; init; }

    /// <summary>Gets the latest covered program time.</summary>
    public required DateTimeOffset CoverageEndUtc { get; init; }

    /// <summary>Gets the imported channel count.</summary>
    public required int ChannelCount { get; init; }

    /// <summary>Gets the imported program count.</summary>
    public required int ProgramCount { get; init; }

    /// <summary>Gets whether supplemental root metadata was imported.</summary>
    public required bool HasSupplementalMetadata { get; init; }
}

/// <summary>
/// Atomically persists guide data and authoritative bindings.
/// </summary>
public sealed class DeviceGuideStore
{
    private readonly IDbContextFactory<EpgDbContext> _contextFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a device guide store.</summary>
    public DeviceGuideStore(IDbContextFactory<EpgDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Reads the last successfully committed import metadata for every guide source.
    /// </summary>
    public async Task<IReadOnlyList<GuideSourceImportState>> ReadImportStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        var imports = await context.DeviceGuideImports
            .AsNoTracking()
            .OrderBy(item => item.GuideSourceId)
            .ToArrayAsync(cancellationToken);
        return imports.Select(item => new GuideSourceImportState
        {
            GuideSourceId = new GuideSourceId(Guid.Parse(item.GuideSourceId)),
            FetchedAtUtc = DateTime.SpecifyKind(item.FetchedUtc, DateTimeKind.Utc),
            CommittedAtUtc = DateTime.SpecifyKind(item.CommittedUtc, DateTimeKind.Utc),
            CoverageStartUtc = DateTimeOffset.FromUnixTimeSeconds(item.CoverageStart),
            CoverageEndUtc = DateTimeOffset.FromUnixTimeSeconds(item.CoverageEnd),
            ChannelCount = item.ChannelCount,
            ProgramCount = item.ProgramCount,
            HasSupplementalMetadata = !string.IsNullOrWhiteSpace(item.SupplementalXml)
        }).ToArray();
    }

    /// <summary>
    /// Reads binding choices for every active physical source channel.
    /// </summary>
    public async Task<IReadOnlyList<SourceGuideBindingState>> ReadBindingStatesAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyList<GuideSourceScope>? activeGuideScopes = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        var sources = await context.PhysicalSourceChannels.AsNoTracking().Where(channel => channel.IsActive).ToArrayAsync(cancellationToken);
        var guides = await context.GuideSourceChannels.AsNoTracking().ToArrayAsync(cancellationToken);
        var bindings = await context.SourceGuideBindings.AsNoTracking().ToDictionaryAsync(binding => binding.PhysicalSourceChannelId, cancellationToken);
        var guideById = guides.ToDictionary(channel => channel.Id);
        var activeGuides = guides.Where(channel => channel.IsActive).ToArray();

        return sources.Select(source =>
        {
            bindings.TryGetValue(source.Id, out var binding);
            var eligibleSourceIds = GetEligibleSourceIds(source.PhysicalDeviceId, activeGuideScopes, activeGuides);
            var resolution = ResolveBinding(source, activeGuides, guideById, binding, eligibleSourceIds);
            return new SourceGuideBindingState
            {
                SourceChannelId = new SourceChannelId(new PhysicalDeviceId(Guid.Parse(source.PhysicalDeviceId)), source.ProviderKey),
                State = resolution.State,
                SelectedGuideChannelId = resolution.SelectedGuide is null ? null : MapGuideChannelId(resolution.SelectedGuide),
                SelectedGuideChannel = resolution.SelectedGuide is null ? null : MapGuideChannel(resolution.SelectedGuide),
                MatchingCandidates = resolution.Candidates.Select(MapGuideChannel).ToArray()
            };
        }).ToArray();
    }

    /// <summary>
    /// Replaces the last successful import for one guide source.
    /// </summary>
    public async Task ImportAsync(GuideImport import, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (import.CoverageEndUtc <= import.CoverageStartUtc)
        {
            throw new InvalidDataException("Guide import coverage end must be later than its start.");
        }
        foreach (var channel in import.Channels)
        {
            ValidateOwnership(import.SourceId, channel.Id);
        }
        foreach (var program in import.Programs)
        {
            ValidateOwnership(import.SourceId, program.ChannelId);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var sourceKey = import.SourceId.Value.ToString("D");
            var existing = await context.GuideSourceChannels
                .Where(channel => channel.GuideSourceId == sourceKey)
                .ToDictionaryAsync(channel => channel.ProviderKey, StringComparer.Ordinal, cancellationToken);
            foreach (var channel in existing.Values)
            {
                channel.IsActive = false;
            }

            foreach (var channel in import.Channels)
            {
                if (!existing.TryGetValue(channel.Id.ProviderKey, out var stored))
                {
                    stored = new StoredGuideSourceChannel
                    {
                        GuideSourceId = sourceKey,
                        ProviderKey = channel.Id.ProviderKey,
                        Number = channel.Number,
                        Name = channel.Name
                    };
                    existing.Add(stored.ProviderKey, stored);
                    context.GuideSourceChannels.Add(stored);
                }
                stored.Number = channel.Number;
                stored.Name = channel.Name;
                stored.Affiliate = channel.Affiliate;
                stored.ImageUrl = channel.ImageUrl;
                stored.IsProtected = channel.IsProtected;
                stored.IsFavorite = channel.IsFavorite;
                stored.SupplementalXml = channel.SupplementalXml;
                stored.IsActive = true;
            }
            await context.SaveChangesAsync(cancellationToken);

            var channelIds = existing.Values.Select(channel => channel.Id).ToArray();
            await context.GuideSourcePrograms
                .Where(program => channelIds.Contains(program.GuideSourceChannelId))
                .ExecuteDeleteAsync(cancellationToken);
            foreach (var program in import.Programs)
            {
                if (!existing.TryGetValue(program.ChannelId.ProviderKey, out var channel) || !channel.IsActive)
                {
                    throw new InvalidDataException($"Program channel {program.ChannelId.ProviderKey} is not present in the guide import.");
                }
                context.GuideSourcePrograms.Add(new StoredGuideSourceProgram
                {
                    GuideSourceChannelId = channel.Id,
                    StartTime = program.StartUtc.ToUnixTimeSeconds(),
                    EndTime = program.EndUtc.ToUnixTimeSeconds(),
                    Title = program.Title,
                    Synopsis = program.Synopsis,
                    EpisodeTitle = program.EpisodeTitle,
                    ImageUrl = program.ImageUrl,
                    PosterUrl = program.PosterUrl,
                    EpisodeNumber = program.EpisodeNumber,
                    OriginalAirdate = program.OriginalAirdateUtc?.ToUnixTimeSeconds(),
                    IsFirstAiring = program.IsFirstAiring,
                    SeriesId = program.SeriesId,
                    CategoriesJson = Serialize(program.Categories),
                    MetadataJson = Serialize(program.Metadata),
                    SupplementalXml = program.SupplementalXml
                });
            }
            await context.SaveChangesAsync(cancellationToken);

            var storedImport = await context.DeviceGuideImports.SingleOrDefaultAsync(
                item => item.GuideSourceId == sourceKey,
                cancellationToken);
            if (storedImport is null)
            {
                storedImport = new StoredDeviceGuideImport
                {
                    GuideSourceId = sourceKey
                };
                context.DeviceGuideImports.Add(storedImport);
            }
            storedImport.FetchedUtc = import.FetchedAtUtc.UtcDateTime;
            storedImport.CommittedUtc = DateTime.UtcNow;
            storedImport.CoverageStart = import.CoverageStartUtc.ToUnixTimeSeconds();
            storedImport.CoverageEnd = import.CoverageEndUtc.ToUnixTimeSeconds();
            storedImport.ChannelCount = import.Channels.Count;
            storedImport.ProgramCount = import.Programs.Count;
            storedImport.SupplementalXml = import.SupplementalXml;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reports automatic matches, manual bindings, unavailable selections, and ambiguities for one physical device.
    /// </summary>
    public async Task<IReadOnlyList<GuideBindingDiagnostic>> GetBindingDiagnosticsAsync(
        GuideSourceId sourceId,
        CancellationToken cancellationToken = default,
        GuideSourceScope? scope = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        if (scope is not null && scope.SourceId != sourceId)
        {
            throw new ArgumentException("The guide source scope does not match the requested source.", nameof(scope));
        }
        var physicalKeys = scope?.PhysicalDeviceIds.Select(id => id.ToString()).ToArray();
        var sourceChannels = await context.PhysicalSourceChannels
            .AsNoTracking()
            .Where(channel => (physicalKeys == null || physicalKeys.Contains(channel.PhysicalDeviceId)) && channel.IsActive)
            .OrderBy(channel => channel.ProviderKey)
            .ToArrayAsync(cancellationToken);
        var guideChannels = await context.GuideSourceChannels
            .AsNoTracking()
            .Where(channel => channel.GuideSourceId == sourceId.Value.ToString("D"))
            .OrderBy(channel => channel.ProviderKey)
            .ToArrayAsync(cancellationToken);
        var sourceIds = sourceChannels.Select(channel => channel.Id).ToArray();
        var bindings = await context.SourceGuideBindings
            .AsNoTracking()
            .Where(binding => sourceIds.Contains(binding.PhysicalSourceChannelId))
            .ToDictionaryAsync(binding => binding.PhysicalSourceChannelId, cancellationToken);
        var guideById = guideChannels.ToDictionary(channel => channel.Id);
        var activeGuides = guideChannels.Where(channel => channel.IsActive).ToArray();

        return sourceChannels.Select(channel =>
        {
            var sourceChannelId = new SourceChannelId(new PhysicalDeviceId(Guid.Parse(channel.PhysicalDeviceId)), channel.ProviderKey);
            bindings.TryGetValue(channel.Id, out var binding);
            var resolution = ResolveBinding(channel, activeGuides, guideById, binding, new HashSet<GuideSourceId> { sourceId });
            return new GuideBindingDiagnostic
            {
                SourceChannelId = sourceChannelId,
                State = resolution.State,
                BoundChannelId = resolution.SelectedGuide is null ? null : MapGuideChannelId(resolution.SelectedGuide),
                Candidates = resolution.AutomaticCandidates.Select(MapGuideChannel).ToArray()
            };
        }).ToArray();
    }

    /// <summary>
    /// Builds a provider-neutral virtual guide from resolved physical candidates and authoritative bindings.
    /// </summary>
    public async Task<VirtualGuideSnapshot> BuildSnapshotAsync(
        VirtualDeviceId virtualDeviceId,
        IReadOnlyList<ResolvedVirtualChannel> channels,
        IReadOnlyList<GuideSourceScope>? activeGuideScopes = null,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        var storedSources = await context.PhysicalSourceChannels.AsNoTracking().Where(channel => channel.IsActive).ToArrayAsync(cancellationToken);
        var sourceLookup = storedSources.ToDictionary(channel => (channel.PhysicalDeviceId, channel.ProviderKey));
        var sourceIds = storedSources.Select(channel => channel.Id).ToArray();
        var bindings = await context.SourceGuideBindings
            .AsNoTracking()
            .Where(binding => sourceIds.Contains(binding.PhysicalSourceChannelId))
            .ToDictionaryAsync(binding => binding.PhysicalSourceChannelId, cancellationToken);
        var storedGuides = await context.GuideSourceChannels.AsNoTracking().ToArrayAsync(cancellationToken);
        var guideById = storedGuides.ToDictionary(channel => channel.Id);
        var activeGuideSourceIds = activeGuideScopes?.Select(scope => scope.SourceId).ToHashSet();
        var activeGuides = storedGuides
            .Where(channel => channel.IsActive && (activeGuideSourceIds is null || activeGuideSourceIds.Contains(MapGuideChannelId(channel).GuideSourceId)))
            .ToArray();
        var resolvedGuides = storedSources.ToDictionary(
            source => source.Id,
            source =>
            {
                bindings.TryGetValue(source.Id, out var binding);
                var eligibleSourceIds = GetEligibleSourceIds(source.PhysicalDeviceId, activeGuideScopes, activeGuides);
                return ResolveBinding(source, activeGuides, guideById, binding, eligibleSourceIds).SelectedActiveGuide;
            });
        var guides = resolvedGuides.Values.OfType<StoredGuideSourceChannel>().DistinctBy(channel => channel.Id).ToDictionary(channel => channel.Id);
        var activeGuideIds = guides.Keys.ToArray();
        var programQuery = context.GuideSourcePrograms
            .AsNoTracking()
            .Where(program => activeGuideIds.Contains(program.GuideSourceChannelId));
        if (startUtc.HasValue)
        {
            var start = startUtc.Value.ToUnixTimeSeconds();
            programQuery = programQuery.Where(program => program.EndTime > start);
        }
        if (endUtc.HasValue)
        {
            var end = endUtc.Value.ToUnixTimeSeconds();
            programQuery = programQuery.Where(program => program.StartTime < end);
        }
        var programs = await programQuery.OrderBy(program => program.StartTime).ToArrayAsync(cancellationToken);
        var programsByGuide = programs.ToLookup(program => program.GuideSourceChannelId);
        var contributingGuideSources = guides.Values
            .Select(guide => guide.GuideSourceId)
            .Distinct()
            .ToArray();
        var deviceGuideImports = await context.DeviceGuideImports
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var importSupplementalXml = deviceGuideImports
            .Where(import => contributingGuideSources.Contains(import.GuideSourceId))
            .Select(import => import.SupplementalXml)
            .OfType<string>()
            .ToArray();

        var projected = new List<VirtualGuideChannel>(channels.Count);
        foreach (var channel in channels)
        {
            StoredGuideSourceChannel? guide = null;
            foreach (var sourceId in channel.Sources)
            {
                if (sourceLookup.TryGetValue((sourceId.PhysicalDeviceId.ToString(), sourceId.ProviderKey), out var source)
                    && resolvedGuides.TryGetValue(source.Id, out guide)
                    && guide is not null
                    && GetEligibleSourceIds(source.PhysicalDeviceId, activeGuideScopes, activeGuides).Contains(MapGuideChannelId(guide).GuideSourceId))
                {
                    break;
                }
                guide = null;
            }

            var channelPrograms = guide is null
                ? []
                : programsByGuide[guide.Id].Select(program => new GuideProgram
                {
                    ChannelId = MapId(new GuideSourceId(Guid.Parse(guide.GuideSourceId)), guide),
                    StartUtc = DateTimeOffset.FromUnixTimeSeconds(program.StartTime),
                    EndUtc = DateTimeOffset.FromUnixTimeSeconds(program.EndTime),
                    Title = program.Title,
                    Synopsis = program.Synopsis,
                    EpisodeTitle = program.EpisodeTitle,
                    ImageUrl = program.ImageUrl,
                    PosterUrl = program.PosterUrl,
                    EpisodeNumber = program.EpisodeNumber,
                    OriginalAirdateUtc = program.OriginalAirdate.HasValue ? DateTimeOffset.FromUnixTimeSeconds(program.OriginalAirdate.Value) : null,
                    IsFirstAiring = program.IsFirstAiring,
                    SeriesId = program.SeriesId,
                    Categories = Deserialize<IReadOnlyList<string>>(program.CategoriesJson) ?? [],
                    Metadata = Deserialize<XmltvProgramMetadata>(program.MetadataJson),
                    SupplementalXml = program.SupplementalXml
                }).ToArray();
            projected.Add(new VirtualGuideChannel
            {
                ChannelId = CreateVirtualChannelId(virtualDeviceId, channel.Number),
                Number = channel.Number,
                Name = channel.Name,
                Affiliate = guide?.Affiliate,
                ImageUrl = guide?.ImageUrl,
                IsProtected = guide?.IsProtected ?? false,
                IsFavorite = guide?.IsFavorite ?? false,
                SupplementalXml = guide?.SupplementalXml,
                Programs = channelPrograms
            });
        }

        return new VirtualGuideSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            Channels = projected,
            SupplementalXml = importSupplementalXml
        };
    }

    /// <summary>
    /// Selects one authoritative guide channel for a physical source channel.
    /// </summary>
    public async Task SetBindingAsync(
        SourceChannelId sourceChannelId,
        GuideChannelId guideChannelId,
        CancellationToken cancellationToken = default,
        IReadOnlyList<GuideSourceScope>? activeGuideScopes = null)
    {
        if (activeGuideScopes is not null
            && !activeGuideScopes.Any(scope => scope.SourceId == guideChannelId.GuideSourceId && scope.PhysicalDeviceIds.Contains(sourceChannelId.PhysicalDeviceId)))
        {
            throw new ArgumentException("The guide source is not subscribed to the physical source channel's device.", nameof(guideChannelId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var physicalKey = sourceChannelId.PhysicalDeviceId.ToString();
            var source = await context.PhysicalSourceChannels.SingleOrDefaultAsync(
                channel => channel.PhysicalDeviceId == physicalKey && channel.ProviderKey == sourceChannelId.ProviderKey,
                cancellationToken) ?? throw new InvalidOperationException("The physical source channel was not found.");
            var guideSourceKey = guideChannelId.GuideSourceId.Value.ToString("D");
            var guide = await context.GuideSourceChannels.SingleOrDefaultAsync(
                channel => channel.GuideSourceId == guideSourceKey && channel.ProviderKey == guideChannelId.ProviderKey && channel.IsActive,
                cancellationToken) ?? throw new InvalidOperationException("The active guide channel was not found.");
            var binding = await context.SourceGuideBindings.SingleOrDefaultAsync(item => item.PhysicalSourceChannelId == source.Id, cancellationToken);
            if (binding is null)
            {
                context.SourceGuideBindings.Add(new StoredSourceGuideBinding { PhysicalSourceChannelId = source.Id, GuideSourceChannelId = guide.Id });
            }
            else
            {
                binding.GuideSourceChannelId = guide.Id;
            }
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the authoritative guide selection for a physical source channel.
    /// </summary>
    public async Task ClearBindingAsync(SourceChannelId sourceChannelId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var physicalKey = sourceChannelId.PhysicalDeviceId.ToString();
            var sourceId = await context.PhysicalSourceChannels
                .Where(channel => channel.PhysicalDeviceId == physicalKey && channel.ProviderKey == sourceChannelId.ProviderKey)
                .Select(channel => (int?)channel.Id)
                .SingleOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("The physical source channel was not found.");
            await context.SourceGuideBindings.Where(binding => binding.PhysicalSourceChannelId == sourceId).ExecuteDeleteAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateOwnership(GuideSourceId sourceId, GuideChannelId channelId)
    {
        if (channelId.GuideSourceId != sourceId)
        {
            throw new InvalidDataException($"Guide channel {channelId.ProviderKey} does not belong to import source {sourceId.Value:D}.");
        }
    }

    private static GuideBindingResolution ResolveBinding(
        StoredPhysicalSourceChannel source,
        IReadOnlyList<StoredGuideSourceChannel> activeGuides,
        IReadOnlyDictionary<int, StoredGuideSourceChannel> guideById,
        StoredSourceGuideBinding? manualBinding,
        IReadOnlySet<GuideSourceId> eligibleSourceIds)
    {
        var eligibleGuides = activeGuides
            .Where(guide => eligibleSourceIds.Contains(MapGuideChannelId(guide).GuideSourceId))
            .OrderBy(guide => guide.Number, ChannelNumberComparer.Instance)
            .ThenBy(guide => guide.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var providerKeyMatches = eligibleGuides
            .Where(guide => string.Equals(guide.ProviderKey, source.ProviderKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var numberMatches = eligibleGuides
            .Where(guide => string.Equals(guide.Number, source.ProviderNumber, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var automaticCandidates = providerKeyMatches.Length == 1
            ? providerKeyMatches
            : numberMatches.Length > 0
                ? numberMatches
                : providerKeyMatches;

        if (manualBinding is not null)
        {
            guideById.TryGetValue(manualBinding.GuideSourceChannelId, out var selectedGuide);
            var selectedActiveGuide = selectedGuide is not null && selectedGuide.IsActive && eligibleGuides.Any(guide => guide.Id == selectedGuide.Id)
                ? selectedGuide
                : null;
            return new GuideBindingResolution(
                selectedActiveGuide is null ? GuideBindingState.ManualUnavailable : GuideBindingState.Manual,
                selectedGuide,
                selectedActiveGuide,
                eligibleGuides,
                automaticCandidates);
        }

        if (providerKeyMatches.Length == 1)
        {
            return new GuideBindingResolution(
                GuideBindingState.Automatic,
                providerKeyMatches[0],
                providerKeyMatches[0],
                eligibleGuides,
                providerKeyMatches);
        }
        if (numberMatches.Length == 1)
        {
            return new GuideBindingResolution(
                GuideBindingState.Automatic,
                numberMatches[0],
                numberMatches[0],
                eligibleGuides,
                numberMatches);
        }

        return new GuideBindingResolution(
            automaticCandidates.Length > 1 ? GuideBindingState.NeedsGuideSelection : GuideBindingState.Unmatched,
            null,
            null,
            eligibleGuides,
            automaticCandidates);
    }

    private static GuideChannelId MapId(GuideSourceId sourceId, StoredGuideSourceChannel channel) =>
        new(sourceId, channel.ProviderKey);

    private static GuideChannel MapGuideChannel(StoredGuideSourceChannel channel) =>
        new()
        {
            Id = MapGuideChannelId(channel),
            Number = channel.Number,
            Name = channel.Name,
            Affiliate = channel.Affiliate,
            ImageUrl = channel.ImageUrl,
            IsProtected = channel.IsProtected,
            IsFavorite = channel.IsFavorite,
            SupplementalXml = channel.SupplementalXml
        };

    private static string? Serialize<T>(T? value)
    {
        return value is null ? null : JsonSerializer.Serialize(value);
    }

    private static T? Deserialize<T>(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<T>(value);
    }

    private static GuideChannelId MapGuideChannelId(StoredGuideSourceChannel channel)
    {
        return new GuideChannelId(new GuideSourceId(Guid.Parse(channel.GuideSourceId)), channel.ProviderKey);
    }

    private static IReadOnlySet<GuideSourceId> GetEligibleSourceIds(
        string physicalDeviceId,
        IReadOnlyList<GuideSourceScope>? activeGuideScopes,
        IReadOnlyList<StoredGuideSourceChannel> activeGuides)
    {
        if (activeGuideScopes is null)
        {
            return activeGuides.Select(guide => MapGuideChannelId(guide).GuideSourceId).ToHashSet();
        }

        var parsedPhysicalDeviceId = new PhysicalDeviceId(Guid.Parse(physicalDeviceId));
        return activeGuideScopes
            .Where(scope => scope.PhysicalDeviceIds.Contains(parsedPhysicalDeviceId))
            .Select(scope => scope.SourceId)
            .ToHashSet();
    }

    private static VirtualChannelId CreateVirtualChannelId(VirtualDeviceId virtualDeviceId, string number)
    {
        var input = Encoding.UTF8.GetBytes($"{virtualDeviceId.Value:D}\n{number}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new VirtualChannelId(virtualDeviceId, new Guid(hash[..16]));
    }

    private sealed record GuideBindingResolution(
        GuideBindingState State,
        StoredGuideSourceChannel? SelectedGuide,
        StoredGuideSourceChannel? SelectedActiveGuide,
        IReadOnlyList<StoredGuideSourceChannel> Candidates,
        IReadOnlyList<StoredGuideSourceChannel> AutomaticCandidates);
}

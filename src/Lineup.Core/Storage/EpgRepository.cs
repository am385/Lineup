using Lineup.HDHomeRun.Api.Models;
using Lineup.Core.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Storage;

/// <summary>
/// EF Core implementation of the EPG repository.
/// Stores raw API data for later enrichment.
/// </summary>
public class EpgRepository : IEpgRepository
{
    private readonly EpgDbContext _context;
    private readonly ILogger<EpgRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgRepository"/> class.
    /// </summary>
    public EpgRepository(EpgDbContext context, ILogger<EpgRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Performs the ensure database created operation.
    /// </summary>
    public async Task EnsureDatabaseCreatedAsync()
    {
        await EpgDatabaseSchema.EnsureAsync(_context);
        _logger.LogDebug("Database ensured created");
    }

    /// <summary>
    /// Performs the store channel operation.
    /// </summary>
    public async Task StoreChannelAsync(HDHomeRunChannelEpgSegment channel)
    {
        var existing = await _context.Channels
            .FirstOrDefaultAsync(c => c.GuideNumber == channel.GuideNumber);

        if (existing != null)
        {
            existing.GuideName = channel.GuideName;
            existing.Affiliate = channel.Affiliate;
            existing.ImageURL = channel.ImageURL;
            existing.DRM = channel.DRM;
            existing.Favorite = channel.Favorite;
            existing.LastUpdatedUtc = DateTime.UtcNow;
        }
        else
        {
            _context.Channels.Add(new StoredChannel
            {
                GuideNumber = channel.GuideNumber ?? "",
                GuideName = channel.GuideName,
                Affiliate = channel.Affiliate,
                ImageURL = channel.ImageURL,
                DRM = channel.DRM,
                Favorite = channel.Favorite,
                LastUpdatedUtc = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Performs the store channels operation.
    /// </summary>
    public async Task StoreChannelsAsync(IEnumerable<HDHomeRunChannelEpgSegment> channels)
    {
        foreach (var channel in channels)
        {
            if (string.IsNullOrEmpty(channel.GuideNumber))
            {
                _logger.LogWarning("Skipping channel with empty GuideNumber");
                continue;
            }

            var existing = await _context.Channels
                .FirstOrDefaultAsync(c => c.GuideNumber == channel.GuideNumber);

            if (existing != null)
            {
                existing.GuideName = channel.GuideName;
                existing.Affiliate = channel.Affiliate;
                existing.ImageURL = channel.ImageURL;
                existing.DRM = channel.DRM;
                existing.Favorite = channel.Favorite;
                existing.LastUpdatedUtc = DateTime.UtcNow;
            }
            else
            {
                _context.Channels.Add(new StoredChannel
                {
                    GuideNumber = channel.GuideNumber,
                    GuideName = channel.GuideName,
                    Affiliate = channel.Affiliate,
                    ImageURL = channel.ImageURL,
                    DRM = channel.DRM,
                    Favorite = channel.Favorite,
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogDebug("Stored {Count} channels", channels.Count());
    }

    /// <summary>
    /// Performs the store program operation.
    /// </summary>
    public async Task StoreProgramAsync(HDHomeRunProgram program, string guideNumber)
    {
        // Check for duplicate
        var exists = await _context.Programs.AnyAsync(p =>
            p.GuideNumber == guideNumber &&
            p.StartTime == program.StartTime &&
            p.Title == program.Title);

        if (exists)
        {
            _logger.LogDebug("Skipping duplicate program: {Title} at {StartTime}", program.Title, program.StartTime);
            return;
        }

        _context.Programs.Add(MapToEntity(program, guideNumber));
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Performs the store programs operation.
    /// </summary>
    public async Task StoreProgramsAsync(HDHomeRunChannelEpgSegment channelSegment)
    {
        if (string.IsNullOrEmpty(channelSegment.GuideNumber))
        {
            _logger.LogWarning("Cannot store programs without GuideNumber");
            return;
        }

        var storedCount = 0;
        var skippedCount = 0;

        foreach (var program in channelSegment.Guide)
        {
            // Check for duplicate
            var exists = await _context.Programs.AnyAsync(p =>
                p.GuideNumber == channelSegment.GuideNumber &&
                p.StartTime == program.StartTime &&
                p.Title == program.Title);

            if (exists)
            {
                skippedCount++;
                continue;
            }

            _context.Programs.Add(MapToEntity(program, channelSegment.GuideNumber));
            storedCount++;
        }

        await _context.SaveChangesAsync();
        _logger.LogDebug("Stored {StoredCount} programs for channel {GuideNumber}, skipped {SkippedCount} duplicates", storedCount, channelSegment.GuideNumber, skippedCount);
    }

    /// <summary>
    /// Performs the store raw segment operation.
    /// </summary>
    public async Task StoreRawSegmentAsync(IEnumerable<HDHomeRunChannelEpgSegment> segments)
    {
        var segmentList = segments.ToList();

        // Store channels
        await StoreChannelsAsync(segmentList);

        // Store programs for each channel
        foreach (var segment in segmentList)
        {
            await StoreProgramsAsync(segment);
        }

        _logger.LogDebug("Stored raw segment with {ChannelCount} channels", segmentList.Count);
    }

    /// <summary>
    /// Performs the replace raw epg data operation.
    /// </summary>
    public async Task ReplaceRawEpgDataAsync(IEnumerable<HDHomeRunChannelEpgSegment> segments, CancellationToken cancellationToken = default)
    {
        var segmentList = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        await EpgDatabaseSchema.EnsureAsync(_context, cancellationToken);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Programs.ExecuteDeleteAsync(cancellationToken);
        await _context.Channels.ExecuteDeleteAsync(cancellationToken);
        var fetchedAt = DateTime.UtcNow;
        _context.Channels.AddRange(segmentList.Select(segment => new StoredChannel
        {
            GuideNumber = segment.GuideNumber!,
            GuideName = segment.GuideName,
            Affiliate = segment.Affiliate,
            ImageURL = segment.ImageURL,
            DRM = segment.DRM,
            Favorite = segment.Favorite,
            LastUpdatedUtc = fetchedAt
        }));
        _context.Programs.AddRange(segmentList.SelectMany(segment => segment.Guide.Select(program => MapToEntity(program, segment.GuideNumber!))));
        await _context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task ImportGuideAsync(IEnumerable<HDHomeRunChannelEpgSegment> segments, TimeSpan historyRetention, CancellationToken cancellationToken = default)
    {
        if (historyRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(historyRetention), "Guide history retention cannot be negative.");
        }

        await EpgDatabaseSchema.EnsureAsync(_context, cancellationToken);
        var segmentList = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.GuideNumber))
            .GroupBy(segment => segment.GuideNumber!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                GuideNumber = group.Key.Trim(),
                Guide = group.SelectMany(segment => segment.Guide)
                    .GroupBy(program => CreateProgramKey(group.Key.Trim(), program))
                    .Select(programs => programs.First())
                    .OrderBy(program => program.StartTime)
                    .ToList()
            })
            .ToArray();
        var incomingPrograms = segmentList.SelectMany(segment => segment.Guide.Select(program => (GuideNumber: segment.GuideNumber!, Program: program))).ToArray();
        if (incomingPrograms.Length == 0)
        {
            throw new InvalidDataException("The guide snapshot did not contain any programmes.");
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var importedAt = DateTime.UtcNow;
        var importId = Guid.NewGuid().ToString("N");
        var coverageStart = incomingPrograms.Min(item => item.Program.StartTime);
        var coverageEnd = incomingPrograms.Max(item => item.Program.EndTime);
        var guideImport = new StoredGuideImport
        {
            Id = importId,
            StartedUtc = importedAt,
            CoverageStart = coverageStart,
            CoverageEnd = coverageEnd,
            ChannelCount = segmentList.Length,
            ProgramCount = incomingPrograms.Length
        };
        _context.GuideImports.Add(guideImport);

        var storedChannels = await _context.Channels.ToDictionaryAsync(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
        foreach (var segment in segmentList)
        {
            if (!storedChannels.TryGetValue(segment.GuideNumber!, out var channel))
            {
                channel = new StoredChannel { GuideNumber = segment.GuideNumber! };
                _context.Channels.Add(channel);
                storedChannels.Add(segment.GuideNumber!, channel);
            }

            channel.GuideName = segment.GuideName;
            channel.Affiliate = segment.Affiliate;
            channel.ImageURL = segment.ImageURL;
            channel.DRM = segment.DRM;
            channel.Favorite = segment.Favorite;
            channel.LastUpdatedUtc = importedAt;
            channel.LastSeenImportId = importId;
        }

        var storedPrograms = await _context.Programs
            .Where(program => program.EndTime >= coverageStart && program.StartTime <= coverageEnd)
            .ToListAsync(cancellationToken);
        var storedProgramsByKey = storedPrograms
            .GroupBy(CreateProgramKey)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var incoming in incomingPrograms)
        {
            var key = CreateProgramKey(incoming.GuideNumber, incoming.Program);
            if (!storedProgramsByKey.TryGetValue(key, out var stored))
            {
                stored = MapToEntity(incoming.Program, incoming.GuideNumber);
                _context.Programs.Add(stored);
                storedProgramsByKey.Add(key, stored);
            }
            else
            {
                UpdateEntity(stored, incoming.Program, incoming.GuideNumber);
            }

            stored.FetchedAtUtc = importedAt;
            stored.LastSeenImportId = importId;
        }

        await _context.SaveChangesAsync(cancellationToken);
        var nowUnix = new DateTimeOffset(importedAt).ToUnixTimeSeconds();
        var historyCutoff = new DateTimeOffset(importedAt.Subtract(historyRetention)).ToUnixTimeSeconds();
        await _context.Programs
            .Where(program => program.EndTime < historyCutoff ||
                              (program.EndTime >= nowUnix &&
                               program.EndTime >= coverageStart &&
                               program.StartTime <= coverageEnd &&
                               program.LastSeenImportId != importId))
            .ExecuteDeleteAsync(cancellationToken);
        guideImport.CompletedUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);

        _logger.LogInformation(
            "Committed authoritative guide import {ImportId} with {ChannelCount} channels and {ProgramCount} programmes",
            importId,
            segmentList.Length,
            incomingPrograms.Length);
    }

    /// <summary>
    /// Performs the get channels operation.
    /// </summary>
    public async Task<List<HDHomeRunChannelEpgSegment>> GetChannelsAsync()
    {
        var channels = await _context.Channels.ToListAsync();
        return channels.Select(MapToChannelSegment).ToList();
    }

    /// <summary>
    /// Performs the get programs operation.
    /// </summary>
    public async Task<List<HDHomeRunProgram>> GetProgramsAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null)
    {
        var query = _context.Programs.AsQueryable();

        if (startTimeUtc.HasValue)
        {
            var startUnix = new DateTimeOffset(startTimeUtc.Value).ToUnixTimeSeconds();
            query = query.Where(p => p.EndTime >= startUnix);
        }

        if (endTimeUtc.HasValue)
        {
            var endUnix = new DateTimeOffset(endTimeUtc.Value).ToUnixTimeSeconds();
            query = query.Where(p => p.StartTime <= endUnix);
        }

        var programs = await query.OrderBy(p => p.StartTime).ToListAsync();
        return programs.Select(MapToProgram).ToList();
    }

    /// <summary>
    /// Performs the get raw epg data operation.
    /// </summary>
    public async Task<List<HDHomeRunChannelEpgSegment>> GetRawEpgDataAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null)
    {
        // Get all channels
        var channels = await _context.Channels.ToListAsync();

        // Build query for programs
        var programQuery = _context.Programs.AsQueryable();

        if (startTimeUtc.HasValue)
        {
            var startUnix = new DateTimeOffset(startTimeUtc.Value).ToUnixTimeSeconds();
            programQuery = programQuery.Where(p => p.EndTime >= startUnix);
        }

        if (endTimeUtc.HasValue)
        {
            var endUnix = new DateTimeOffset(endTimeUtc.Value).ToUnixTimeSeconds();
            programQuery = programQuery.Where(p => p.StartTime <= endUnix);
        }

        var programs = await programQuery.OrderBy(p => p.StartTime).ToListAsync();

        // Group programs by channel and build segments
        var programsByChannel = programs.GroupBy(p => p.GuideNumber);

        var result = new List<HDHomeRunChannelEpgSegment>();

        foreach (var channel in channels)
        {
            var channelPrograms = programsByChannel
                .FirstOrDefault(g => g.Key == channel.GuideNumber)?
                .Select(MapToProgram)
                .ToList() ?? [];

            result.Add(new HDHomeRunChannelEpgSegment
            {
                GuideNumber = channel.GuideNumber,
                GuideName = channel.GuideName,
                Affiliate = channel.Affiliate,
                ImageURL = channel.ImageURL,
                DRM = channel.DRM,
                Favorite = channel.Favorite,
                Guide = channelPrograms
            });
        }


        return result;
    }

    /// <summary>
    /// Performs the cleanup old programs operation.
    /// </summary>
    public async Task CleanupOldProgramsAsync(DateTime beforeUtc)
    {
        var beforeUnix = new DateTimeOffset(beforeUtc).ToUnixTimeSeconds();
        var oldPrograms = await _context.Programs
            .Where(p => p.EndTime < beforeUnix)
            .ToListAsync();

        if (oldPrograms.Count > 0)
        {
            _context.Programs.RemoveRange(oldPrograms);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Cleaned up {Count} old programs", oldPrograms.Count);
        }
    }

    /// <summary>
    /// Performs the get cache statistics operation.
    /// </summary>
    public async Task<CacheStatistics> GetCacheStatisticsAsync()
    {
        var channelCount = await _context.Channels.CountAsync();
        var programCount = await _context.Programs.CountAsync();

        DateTime? earliestStart = null;
        DateTime? latestEnd = null;
        TimeSpan? totalSpan = null;

        if (programCount > 0)
        {
            var minStartTime = await _context.Programs.MinAsync(p => p.StartTime);
            var maxEndTime = await _context.Programs.MaxAsync(p => p.EndTime);

            earliestStart = DateTimeOffset.FromUnixTimeSeconds(minStartTime).UtcDateTime;
            latestEnd = DateTimeOffset.FromUnixTimeSeconds(maxEndTime).UtcDateTime;

            // Calculate remaining coverage from now (ignore already-ended programs)
            var now = DateTime.UtcNow;
            if (latestEnd > now)
            {
                var effectiveStart = earliestStart > now ? earliestStart.Value : now;
                totalSpan = latestEnd - effectiveStart;
            }
            else
            {
                totalSpan = TimeSpan.Zero;
            }
        }

        return new CacheStatistics(channelCount, programCount, earliestStart, latestEnd, totalSpan);
    }

    /// <summary>
    /// Performs the get latest program end time operation.
    /// </summary>
    public async Task<DateTime?> GetLatestProgramEndTimeAsync()
    {
        var hasPrograms = await _context.Programs.AnyAsync();
        if (!hasPrograms)
        {
            return null;
        }

        // Find the maximum end time across ALL programs (absolute latest)
        var maxEndTime = await _context.Programs.MaxAsync(p => p.EndTime);
        return DateTimeOffset.FromUnixTimeSeconds(maxEndTime).UtcDateTime;
    }

    /// <summary>
    /// Performs the get safe fetch start time operation.
    /// </summary>
    public async Task<DateTime?> GetSafeFetchStartTimeAsync()
    {
        var hasPrograms = await _context.Programs.AnyAsync();
        if (!hasPrograms)
        {
            return null;
        }

        // For each channel, find the latest program end time,
        // then return the MINIMUM of those - this is the earliest point
        // where any channel's data ends, ensuring no gaps.
        //
        // Example:
        //   Channel A: latest program ends at 11:00 PM
        //   Channel B: latest program ends at 9:00 PM  <-- This is the gap!
        //   Channel C: latest program ends at 10:00 PM
        //   
        // We should start fetching from 9:00 PM to avoid missing Channel B's data.

        var minOfMaxEndTimes = await _context.Programs
            .GroupBy(p => p.GuideNumber)
            .Select(g => g.Max(p => p.EndTime))
            .MinAsync();

        return DateTimeOffset.FromUnixTimeSeconds(minOfMaxEndTimes).UtcDateTime;
    }

    private static StoredProgram MapToEntity(HDHomeRunProgram program, string guideNumber)
    {
        var entity = new StoredProgram
        {
            GuideNumber = guideNumber,
            FetchedAtUtc = DateTime.UtcNow
        };
        UpdateEntity(entity, program, guideNumber);
        return entity;
    }

    private static HDHomeRunChannelEpgSegment MapToChannelSegment(StoredChannel entity)
    {
        return new HDHomeRunChannelEpgSegment
        {
            GuideNumber = entity.GuideNumber,
            GuideName = entity.GuideName,
            Affiliate = entity.Affiliate,
            ImageURL = entity.ImageURL,
            DRM = entity.DRM,
            Favorite = entity.Favorite,
            Guide = [] // Programs loaded separately
        };
    }

    private static void UpdateEntity(StoredProgram entity, HDHomeRunProgram program, string guideNumber)
    {
        entity.GuideNumber = guideNumber;
        entity.Title = program.Title;
        entity.EpisodeTitle = program.EpisodeTitle;
        entity.Synopsis = program.Synopsis;
        entity.StartTime = program.StartTime;
        entity.EndTime = program.EndTime;
        entity.ImageURL = program.ImageURL;
        entity.PosterURL = program.PosterURL;
        entity.EpisodeNumber = program.EpisodeNumber;
        entity.OriginalAirdate = program.OriginalAirdate;
        entity.First = program.First;
        entity.SeriesID = program.SeriesID;
        entity.Filter = program.Filter != null ? string.Join(",", program.Filter) : null;
    }

    private static string CreateProgramKey(StoredProgram program) =>
        CreateProgramKey(program.GuideNumber, program.StartTime, program.EndTime, program.Title);

    private static string CreateProgramKey(string guideNumber, HDHomeRunProgram program) =>
        CreateProgramKey(guideNumber, program.StartTime, program.EndTime, program.Title);

    private static string CreateProgramKey(string guideNumber, long startTime, long endTime, string? title) =>
        $"{guideNumber.ToUpperInvariant()}\u001f{startTime}\u001f{endTime}\u001f{title}";

    private static HDHomeRunProgram MapToProgram(StoredProgram entity)
    {
        return new HDHomeRunProgram
        {
            GuideNumber = entity.GuideNumber,
            Title = entity.Title,
            EpisodeTitle = entity.EpisodeTitle,
            Synopsis = entity.Synopsis,
            StartTime = entity.StartTime,
            EndTime = entity.EndTime,
            ImageURL = entity.ImageURL,
            PosterURL = entity.PosterURL,
            EpisodeNumber = entity.EpisodeNumber,
            OriginalAirdate = entity.OriginalAirdate,
            First = entity.First,
            SeriesID = entity.SeriesID,
            Filter = entity.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
        };
    }
}

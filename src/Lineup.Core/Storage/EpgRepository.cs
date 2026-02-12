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

    public EpgRepository(EpgDbContext context, ILogger<EpgRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task EnsureDatabaseCreatedAsync()
    {
        await _context.Database.EnsureCreatedAsync();
        _logger.LogDebug("Database ensured created");
    }

    public async Task StoreChannelAsync(HDHomeRunChannelEpgSegment channel)
    {
        var existing = await _context.Channels
            .FirstOrDefaultAsync(c => c.GuideNumber == channel.GuideNumber);

        if (existing != null)
        {
            existing.GuideName = channel.GuideName;
            existing.Affiliate = channel.Affiliate;
            existing.ImageURL = channel.ImageURL;
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
                LastUpdatedUtc = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
    }

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
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogDebug("Stored {Count} channels", channels.Count());
    }

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
        _logger.LogDebug("Stored {StoredCount} programs for channel {GuideNumber}, skipped {SkippedCount} duplicates",
            storedCount, channelSegment.GuideNumber, skippedCount);
    }

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

    public async Task<List<HDHomeRunChannelEpgSegment>> GetChannelsAsync()
    {
        var channels = await _context.Channels.ToListAsync();
        return channels.Select(MapToChannelSegment).ToList();
    }

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
                Guide = channelPrograms
            });
        }


        return result;
    }

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

        return new CacheStatistics(
            channelCount,
            programCount,
            earliestStart,
            latestEnd,
            totalSpan);
    }

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
        return new StoredProgram
        {
            GuideNumber = guideNumber,
            Title = program.Title,
            EpisodeTitle = program.EpisodeTitle,
            Synopsis = program.Synopsis,
            StartTime = program.StartTime,
            EndTime = program.EndTime,
            ImageURL = program.ImageURL,
            PosterURL = program.PosterURL,
            EpisodeNumber = program.EpisodeNumber,
            OriginalAirdate = program.OriginalAirdate,
            First = program.First,
            SeriesID = program.SeriesID,
            Filter = program.Filter != null ? string.Join(",", program.Filter) : null,
            FetchedAtUtc = DateTime.UtcNow
        };
    }

    private static HDHomeRunChannelEpgSegment MapToChannelSegment(StoredChannel entity)
    {
        return new HDHomeRunChannelEpgSegment
        {
            GuideNumber = entity.GuideNumber,
            GuideName = entity.GuideName,
            Affiliate = entity.Affiliate,
            ImageURL = entity.ImageURL,
            Guide = [] // Programs loaded separately
        };
    }

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

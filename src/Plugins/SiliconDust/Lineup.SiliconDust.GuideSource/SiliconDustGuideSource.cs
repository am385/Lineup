using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Api;
using Microsoft.Extensions.Logging;

namespace Lineup.SiliconDust.GuideSource;

/// <summary>
/// Imports SiliconDust XMLTV data for one or more HDHomeRun devices.
/// </summary>
public sealed class SiliconDustGuideSource : IGuideSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HDHomeRunApiClient> _apiLogger;
    private readonly ISiliconDustDeviceAuthorizationProvider _authorizationProvider;
    private readonly SiliconDustXmltvParser _parser;
    private readonly IReadOnlyList<PhysicalDeviceId> _physicalDeviceIds;

    internal SiliconDustGuideSource(
        GuideSourceId id,
        IHttpClientFactory httpClientFactory,
        ILogger<HDHomeRunApiClient> apiLogger,
        ISiliconDustDeviceAuthorizationProvider authorizationProvider,
        SiliconDustXmltvParser parser,
        IReadOnlyList<PhysicalDeviceId> physicalDeviceIds)
    {
        Id = id;
        _httpClientFactory = httpClientFactory;
        _apiLogger = apiLogger;
        _authorizationProvider = authorizationProvider;
        _parser = parser;
        _physicalDeviceIds = physicalDeviceIds;
    }

    /// <inheritdoc />
    public GuideSourceId Id { get; }

    /// <inheritdoc />
    public async Task<GuideImport> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = _httpClientFactory.CreateClient(SiliconDustGuideSourceServiceExtensions.HttpClientName);
        var authorization = new DeviceAuthorizationBridge(_authorizationProvider, _physicalDeviceIds, cancellationToken);
        var apiClient = new HDHomeRunApiClient(_apiLogger, httpClient, authorization);
        var content = await apiClient.FetchXmltvAsync(cancellationToken);
        var snapshot = _parser.Parse(content);
        return CreateImport(snapshot);
    }

    private GuideImport CreateImport(XmltvGuideSnapshot snapshot)
    {
        var segments = snapshot.Segments;
        var channels = new List<GuideChannel>(segments.Count);
        var programs = new List<GuideProgram>();
        var channelKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            var number = segment.GuideNumber?.Trim();
            if (string.IsNullOrEmpty(number))
            {
                continue;
            }

            var providerKey = segment.ProviderKey?.Trim();
            if (string.IsNullOrEmpty(providerKey))
            {
                continue;
            }

            var channelId = new GuideChannelId(Id, providerKey);
            if (!channelKeys.Add(channelId.ProviderKey))
            {
                throw new InvalidDataException($"The SiliconDust guide contains duplicate channel key '{channelId.ProviderKey}'.");
            }

            channels.Add(new GuideChannel
            {
                Id = channelId,
                Number = number,
                Name = string.IsNullOrWhiteSpace(segment.GuideName) ? number : segment.GuideName.Trim(),
                Affiliate = segment.Affiliate,
                ImageUrl = segment.ImageURL,
                IsProtected = segment.DRM,
                IsFavorite = segment.Favorite,
                SupplementalXml = segment.SupplementalXml
            });

            foreach (var program in segment.Guide)
            {
                AddProgram(programs, channelId, program);
            }
        }

        if (channels.Count == 0)
        {
            throw new InvalidDataException("The SiliconDust XMLTV guide did not contain any usable channels.");
        }

        if (programs.Count == 0)
        {
            throw new InvalidDataException("The SiliconDust XMLTV guide did not contain any usable programs.");
        }

        programs.Sort(static (left, right) =>
        {
            var channelComparison = string.Compare(left.ChannelId.ProviderKey, right.ChannelId.ProviderKey, StringComparison.Ordinal);
            return channelComparison != 0 ? channelComparison : left.StartUtc.CompareTo(right.StartUtc);
        });

        return new GuideImport
        {
            SourceId = Id,
            Channels = channels,
            Programs = programs,
            CoverageStartUtc = programs.Min(program => program.StartUtc),
            CoverageEndUtc = programs.Max(program => program.EndUtc),
            SupplementalXml = snapshot.SupplementalXml
        };
    }

    private static void AddProgram(List<GuideProgram> programs, GuideChannelId channelId, GuideCacheProgram program)
    {
        var title = program.Title?.Trim();
        if (string.IsNullOrEmpty(title) || program.EndTime <= program.StartTime)
        {
            return;
        }

        programs.Add(new GuideProgram
        {
            ChannelId = channelId,
            StartUtc = DateTimeOffset.FromUnixTimeSeconds(program.StartTime),
            EndUtc = DateTimeOffset.FromUnixTimeSeconds(program.EndTime),
            Title = title,
            Synopsis = string.IsNullOrWhiteSpace(program.Synopsis) ? null : program.Synopsis.Trim(),
            EpisodeTitle = string.IsNullOrWhiteSpace(program.EpisodeTitle) ? null : program.EpisodeTitle.Trim(),
            ImageUrl = program.ImageURL,
            PosterUrl = program.PosterURL,
            EpisodeNumber = program.EpisodeNumber,
            OriginalAirdateUtc = program.OriginalAirdate.HasValue ? DateTimeOffset.FromUnixTimeSeconds(program.OriginalAirdate.Value) : null,
            IsFirstAiring = program.First switch
            {
                1 => true,
                0 => false,
                _ => null
            },
            SeriesId = program.SeriesID,
            Categories = program.Filter ?? [],
            Metadata = program.Metadata,
            SupplementalXml = program.SupplementalXml
        });
    }

    private sealed class DeviceAuthorizationBridge : IDeviceAuthProvider
    {
        private readonly ISiliconDustDeviceAuthorizationProvider _authorizationProvider;
        private readonly IReadOnlyList<PhysicalDeviceId> _physicalDeviceIds;
        private readonly CancellationToken _cancellationToken;

        public DeviceAuthorizationBridge(
            ISiliconDustDeviceAuthorizationProvider authorizationProvider,
            IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
            CancellationToken cancellationToken)
        {
            _authorizationProvider = authorizationProvider;
            _physicalDeviceIds = physicalDeviceIds;
            _cancellationToken = cancellationToken;
        }

        public async Task<string> GetDeviceAuthAsync()
        {
            var authorizations = new string[_physicalDeviceIds.Count];
            for (var index = 0; index < _physicalDeviceIds.Count; index++)
            {
                authorizations[index] = await _authorizationProvider.GetDeviceAuthAsync(_physicalDeviceIds[index], _cancellationToken);
                if (string.IsNullOrWhiteSpace(authorizations[index]))
                {
                    throw new InvalidOperationException($"Physical device {_physicalDeviceIds[index]} returned empty SiliconDust authorization.");
                }
            }

            return string.Concat(authorizations);
        }
    }
}

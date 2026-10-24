using System.Net.Http.Headers;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Controllers;

/// <summary>Publishes management-hosted Lineup Link virtual-device outputs.</summary>
[ApiController]
[Route(LineupLinkProtocol.ControllerRoute)]
public sealed class LineupLinkController : ControllerBase
{
    private readonly ILineupSettingsService _settings;
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly IPhysicalDeviceCatalog _physicalDevices;
    private readonly IStreamingRouteResolver _routes;
    private readonly IGuideOrchestrator _guides;
    private readonly IVirtualMpegTsOutputService _streams;
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly ILogger<LineupLinkController> _logger;

    /// <summary>Initializes the management-hosted Lineup Link output API.</summary>
    public LineupLinkController(
        ILineupSettingsService settings,
        IStreamingAdapterRegistry adapters,
        IPhysicalDeviceCatalog physicalDevices,
        IStreamingRouteResolver routes,
        IGuideOrchestrator guides,
        IVirtualMpegTsOutputService streams,
        IActiveStreamRegistry activeStreams,
        ILogger<LineupLinkController> logger)
    {
        _settings = settings;
        _adapters = adapters;
        _physicalDevices = physicalDevices;
        _routes = routes;
        _guides = guides;
        _streams = streams;
        _activeStreams = activeStreams;
        _logger = logger;
    }

    /// <summary>Gets the exported virtual-device manifest.</summary>
    [HttpGet("manifest")]
    public ActionResult<LineupLinkManifest> GetManifest(Guid virtualDeviceId)
    {
        if (!TryAuthorize(virtualDeviceId, out var context, out var failure))
        {
            return failure;
        }
        var source = VirtualDeviceSubscriptionResolver.Resolve(
            context.Definition,
            _settings.Settings.Streaming.PhysicalDevices,
            context.Driver);
        var subscribedIds = source.Select(device => device.Id).ToHashSet();
        var capacity = context.Driver.GetCapacity(
            _physicalDevices.Devices
                .Where(device => subscribedIds.Contains(device.Snapshot.Id))
                .Select(device => device.Snapshot));
        return new LineupLinkManifest
        {
            InstanceId = context.Configuration.InstanceId,
            VirtualDeviceId = virtualDeviceId,
            Name = context.Definition.Name,
            TunerCount = capacity.TunerCount,
            GuideAvailable = true
        };
    }

    /// <summary>Gets the exported virtual-device lineup.</summary>
    [HttpGet("lineup")]
    public async Task<ActionResult<LineupLinkLineup>> GetLineupAsync(Guid virtualDeviceId, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(virtualDeviceId, out _, out var failure))
        {
            return failure;
        }
        try
        {
            var channels = await _routes.GetVirtualLineupAsync(new VirtualDeviceId(virtualDeviceId), cancellationToken);
            return new LineupLinkLineup
            {
                VirtualDeviceId = virtualDeviceId,
                Channels = channels
                    .OrderBy(channel => channel.Number, ChannelNumberComparer.Instance)
                    .Select(channel => new LineupLinkChannel
                    {
                        Key = channel.Number,
                        Number = channel.Number,
                        Name = channel.Name,
                        Metadata = GetPublishedMetadata(channel.Metadata)
                    })
                    .ToArray()
            };
        }
        catch (KeyNotFoundException)
        {
            return NotFound(UnavailableDevice());
        }
    }

    /// <summary>Gets the exported virtual-device guide.</summary>
    [HttpGet("guide")]
    public async Task<ActionResult<LineupLinkGuide>> GetGuideAsync(Guid virtualDeviceId, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(virtualDeviceId, out _, out var failure))
        {
            return failure;
        }
        try
        {
            var snapshot = await _guides.BuildSnapshotAsync(new VirtualDeviceId(virtualDeviceId), cancellationToken);
            return new LineupLinkGuide
            {
                VirtualDeviceId = virtualDeviceId,
                Channels = snapshot.Channels.Select(channel => new LineupLinkGuideChannel
                {
                    Key = channel.Number,
                    Number = channel.Number,
                    Name = channel.Name,
                    Affiliate = channel.Affiliate,
                    ImageUrl = channel.ImageUrl,
                    IsProtected = _settings.Settings.ProtectedContentMode != ProtectedContentMode.StreamSlate && channel.IsProtected,
                    IsFavorite = channel.IsFavorite,
                    SupplementalXml = channel.SupplementalXml
                }).ToArray(),
                Programs = snapshot.Channels.SelectMany(channel => channel.Programs.Select(program => new LineupLinkGuideProgram
                {
                    ChannelKey = channel.Number,
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
                })).ToArray(),
                SupplementalXml = snapshot.SupplementalXml.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, snapshot.SupplementalXml)
            };
        }

        catch (InvalidOperationException)
        {
            return NotFound(UnavailableDevice());
        }
    }

    private IReadOnlyDictionary<string, string> GetPublishedMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (_settings.Settings.ProtectedContentMode != ProtectedContentMode.StreamSlate || !ProtectedContentDetector.IsProtected(metadata))
        {
            return metadata;
        }
        return new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        {
            ["drm"] = bool.FalseString
        };
    }

    /// <summary>Streams one exported channel as MPEG-TS.</summary>
    [HttpGet("streams/{channelKey}")]
    public async Task<IActionResult> GetStreamAsync(Guid virtualDeviceId, string channelKey, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(virtualDeviceId, out var context, out var failure))
        {
            return failure;
        }
        try
        {
            _logger.LogInformation(
                "Opening management-hosted Lineup Link stream for access grant {AccessGrantId} ({AccessGrantName}) on channel {ChannelKey}",
                context.AuthenticatedGrant.Id,
                context.AuthenticatedGrant.Name,
                channelKey);
            await using var output = await _streams.OpenAsync(new VirtualDeviceId(virtualDeviceId), channelKey, cancellationToken);
            var sessionId = StreamSessionId.New().ToString();
            var stream = ActiveStreamPlanFactory.CreateMpegTs(
                sessionId,
                channelKey,
                DateTime.UtcNow,
                output.RawSource,
                output.Source,
                output.Profile) with
            {
                ClientAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Flow = output.Flow
            };
            if (!_activeStreams.TryRegister(stream, _settings.Settings.MaximumConcurrentStreams, HttpContext.Abort))
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new LineupLinkError
                {
                    Code = "stream_limit",
                    Message = "The maximum number of concurrent streams is already active."
                });
            }
            try
            {
                Response.ContentType = LineupLinkProtocol.MpegTsMediaType;
                Response.Headers.CacheControl = "no-cache, no-store";
                await output.Stream.CopyToAsync(Response.Body, cancellationToken);
                return new EmptyResult();
            }
            finally
            {
                _activeStreams.Unregister(sessionId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new LineupLinkError
            {
                Code = "channel_not_found",
                Message = "The requested channel is unavailable."
            });
        }
        catch (StreamAcquisitionException exception)
        {
            var mapped = StreamAcquisitionHttpMapper.Map(exception);
            _logger.LogWarning(
                exception,
                "Lineup Link source acquisition failed for channel {ChannelKey} with code {FailureCode}; retryable: {Retryable}; candidates: {Candidates}",
                channelKey,
                mapped.Code,
                exception.Retryable,
                StreamAcquisitionHttpMapper.FormatCandidates(exception));
            return StatusCode(mapped.StatusCode, new LineupLinkError
            {
                Code = mapped.Code,
                Message = mapped.Error
            });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new LineupLinkError
            {
                Code = "stream_unavailable",
                Message = "The requested channel cannot be streamed right now."
            });
        }
    }

    private bool TryAuthorize(
        Guid virtualDeviceId,
        out LineupLinkVirtualContext context,
        out ActionResult failure)
    {
        var definition = _settings.Settings.Streaming.VirtualDevices.SingleOrDefault(device =>
            device.Enabled &&
            device.Hosting is null &&
            device.Id.Value == virtualDeviceId &&
            string.Equals(device.Adapter.Kind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            context = null!;
            failure = NotFound(UnavailableDevice());
            return false;
        }

        var driver = (LineupLinkVirtualTunerDriver)_adapters.GetVirtual(LineupLinkProtocol.AdapterKind);
        var configuration = driver.GetConfiguration(definition.Adapter);
        if (configuration.RequireEncryption && !Request.IsHttps)
        {
            Response.Headers.Upgrade = "TLS/1.2";
            context = null!;
            failure = StatusCode(StatusCodes.Status426UpgradeRequired, new LineupLinkError
            {
                Code = "https_required",
                Message = "This Lineup Link virtual device requires HTTPS."
            });
            return false;
        }

        var authorization = Request.Headers.Authorization.ToString();
        var token = AuthenticationHeaderValue.TryParse(authorization, out var parsed) &&
            string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                ? parsed.Parameter
                : null;
        var authenticatedGrant = LineupLinkAccessGrantAuthenticator.Authenticate(token, configuration, DateTime.UtcNow);
        if (authenticatedGrant is null)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            context = null!;
            failure = Unauthorized(new LineupLinkError
            {
                Code = "unauthorized",
                Message = "A valid Lineup Link access token is required."
            });
            return false;
        }

        context = new LineupLinkVirtualContext(definition, driver, configuration, authenticatedGrant);
        failure = null!;
        return true;
    }

    private static LineupLinkError UnavailableDevice() =>
        new()
        {
            Code = "device_not_found",
            Message = "The requested Lineup Link virtual device is unavailable."
        };

    private sealed record LineupLinkVirtualContext(
        VirtualDeviceDefinition Definition,
        LineupLinkVirtualTunerDriver Driver,
        LineupLinkVirtualConfiguration Configuration,
        LineupLinkAuthenticatedGrant AuthenticatedGrant);
}

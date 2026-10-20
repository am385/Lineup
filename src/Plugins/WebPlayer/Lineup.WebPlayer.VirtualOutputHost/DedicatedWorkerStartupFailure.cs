using System.ComponentModel;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;

namespace Lineup.WebPlayer.VirtualOutputHost;

/// <summary>Identifies the dedicated Web Player startup stage that produced a failure.</summary>
internal enum DedicatedWorkerStartupStage
{
    /// <summary>Preparing transient resources for the media session.</summary>
    Preparation,

    /// <summary>Opening the physical-effective stream through the trusted broker.</summary>
    Broker,

    /// <summary>Planning the browser presentation from brokered media metadata.</summary>
    Planning,

    /// <summary>Starting the FFmpeg media processes.</summary>
    Process,

    /// <summary>Waiting for the initial browser presentation to become ready.</summary>
    Readiness
}

/// <summary>Describes a safe, structured dedicated Web Player startup failure.</summary>
internal sealed record DedicatedWorkerStartupFailure
{
    /// <summary>Gets the HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the safe client-facing error message.</summary>
    public required string Error { get; init; }

    /// <summary>Gets the startup stage that failed.</summary>
    public required string Stage { get; init; }
}

/// <summary>Maps dedicated Web Player startup exceptions to stable client-facing failures.</summary>
internal static class DedicatedWorkerStartupFailureMapper
{
    /// <summary>Maps one startup exception without exposing process output or internal endpoint details.</summary>
    public static DedicatedWorkerStartupFailure Map(Exception exception, DedicatedWorkerStartupStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var stageName = stage.ToString().ToLowerInvariant();
        return exception switch
        {
            VirtualOutputClientException brokerException => MapBrokerFailure(brokerException, stageName),
            TimeoutException => Create(
                StatusCodes.Status504GatewayTimeout,
                "media_startup_timeout",
                "The Web Player timed out while preparing the media presentation.",
                stageName),
            Win32Exception => Create(
                StatusCodes.Status502BadGateway,
                "media_process_unavailable",
                "The Web Player could not start its media processor.",
                stageName),
            InvalidDataException => Create(
                StatusCodes.Status502BadGateway,
                "invalid_media_metadata",
                "The Web Player received incomplete media metadata from the streaming service.",
                stageName),
            ArgumentException when stage == DedicatedWorkerStartupStage.Planning => Create(
                StatusCodes.Status400BadRequest,
                "invalid_stream_request",
                "The Web Player stream request contains unsupported or invalid settings.",
                stageName),
            InvalidOperationException => Create(
                StatusCodes.Status502BadGateway,
                "media_startup_failed",
                "The Web Player media processor exited before the presentation was ready.",
                stageName),
            IOException when stage == DedicatedWorkerStartupStage.Broker => Create(
                StatusCodes.Status503ServiceUnavailable,
                "broker_unavailable",
                "The Web Player could not reach the Lineup streaming service.",
                stageName),
            _ => Create(
                StatusCodes.Status500InternalServerError,
                "media_startup_error",
                "The Web Player could not start the requested channel.",
                stageName)
        };
    }

    private static DedicatedWorkerStartupFailure MapBrokerFailure(VirtualOutputClientException exception, string stage) =>
        exception.FailureCode switch
        {
            VirtualOutputFailureCode.ChannelNotFound => Create(StatusCodes.Status404NotFound, "channel_not_found", exception.Message, stage),
            VirtualOutputFailureCode.ResourceLimit => Create(StatusCodes.Status429TooManyRequests, "resource_limit", exception.Message, stage),
            VirtualOutputFailureCode.CapabilityDenied => Create(StatusCodes.Status403Forbidden, "capability_denied", exception.Message, stage),
            VirtualOutputFailureCode.InvalidRequest => Create(StatusCodes.Status400BadRequest, "invalid_request", exception.Message, stage),
            VirtualOutputFailureCode.Unauthorized => Create(StatusCodes.Status503ServiceUnavailable, "broker_unauthorized", exception.Message, stage),
            VirtualOutputFailureCode.StaleGeneration => Create(StatusCodes.Status503ServiceUnavailable, "stale_worker_generation", exception.Message, stage),
            VirtualOutputFailureCode.SourceNoSignal => Create(StatusCodes.Status503ServiceUnavailable, "source_no_signal", exception.Message, stage),
            VirtualOutputFailureCode.SourceNoData => Create(StatusCodes.Status503ServiceUnavailable, "source_no_video_data", exception.Message, stage),
            VirtualOutputFailureCode.SourceCapacityExceeded => Create(StatusCodes.Status503ServiceUnavailable, "source_capacity_exceeded", exception.Message, stage),
            VirtualOutputFailureCode.SourceNotFound => Create(StatusCodes.Status404NotFound, "source_not_found", exception.Message, stage),
            VirtualOutputFailureCode.SourceInvalid => Create(StatusCodes.Status400BadRequest, "source_invalid", exception.Message, stage),
            VirtualOutputFailureCode.ProtectedContent => Create(StatusCodes.Status422UnprocessableEntity, "protected_content", exception.Message, stage),
            VirtualOutputFailureCode.SourceUnavailable => Create(StatusCodes.Status503ServiceUnavailable, "source_unavailable", exception.Message, stage),
            _ => Create(StatusCodes.Status503ServiceUnavailable, "broker_unavailable", exception.Message, stage)
        };

    private static DedicatedWorkerStartupFailure Create(int statusCode, string code, string error, string stage) =>
        new()
        {
            StatusCode = statusCode,
            Code = code,
            Error = error,
            Stage = stage
        };
}

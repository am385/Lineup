using Lineup.Extensibility.Streaming;

namespace Lineup.Web.Services;

/// <summary>Describes a safe HTTP response for a route-level source acquisition failure.</summary>
internal sealed record StreamAcquisitionHttpFailure
{
    /// <summary>Gets the HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Gets the stable machine-readable failure code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the safe client-facing error.</summary>
    public required string Error { get; init; }
}

/// <summary>Maps provider-neutral source acquisition failures to the public streaming API contract.</summary>
internal static class StreamAcquisitionHttpMapper
{
    /// <summary>Maps one route-level acquisition failure to a stable HTTP status and error code.</summary>
    public static StreamAcquisitionHttpFailure Map(StreamAcquisitionException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var statusCode = exception.Category switch
        {
            PhysicalStreamFailureCategory.NotFound => StatusCodes.Status404NotFound,
            PhysicalStreamFailureCategory.InvalidInput => StatusCodes.Status400BadRequest,
            PhysicalStreamFailureCategory.ProtectedContent => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return new StreamAcquisitionHttpFailure
        {
            StatusCode = statusCode,
            Code = exception.Code,
            Error = exception.Message
        };
    }

    /// <summary>Formats bounded candidate identities and safe reasons for structured server logs.</summary>
    public static string FormatCandidates(StreamAcquisitionException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Join(
            "; ",
            exception.CandidateFailures.Take(16).Select(failure =>
                $"{failure.DeviceId}/{failure.ChannelId.ProviderKey}:{failure.Code}:{failure.Message}"));
    }
}

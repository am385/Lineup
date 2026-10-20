using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Protocol;

/// <summary>Validates untrusted virtual-output IPC control messages before dispatch.</summary>
public static class VirtualOutputProtocolValidation
{
    /// <summary>Returns registration-request validation errors.</summary>
    public static IReadOnlyList<string> Validate(VirtualOutputWorkerRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.WorkerId.Length > VirtualOutputProtocol.MaximumWorkerIdLength)
        {
            errors.Add($"Worker ID is required and cannot exceed {VirtualOutputProtocol.MaximumWorkerIdLength} characters.");
        }
        if (request.VirtualDeviceId.Value == Guid.Empty)
        {
            errors.Add("A virtual-device ID is required.");
        }
        if (request.Generation < 1)
        {
            errors.Add("Worker generation must be positive.");
        }
        if (string.IsNullOrWhiteSpace(request.BootstrapToken))
        {
            errors.Add("A bootstrap token is required.");
        }
        if (VirtualOutputProtocol.Negotiate(request.MinimumProtocolVersion, request.MaximumProtocolVersion) is null)
        {
            errors.Add("The worker does not support a compatible protocol version.");
        }
        return errors;
    }

    /// <summary>Returns stream-request validation errors.</summary>
    public static IReadOnlyList<string> Validate(VirtualOutputOpenStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (request.Session.SessionId.Value == Guid.Empty)
        {
            errors.Add("A worker session is required.");
        }
        if (request.Session.Generation < 1)
        {
            errors.Add("Worker generation must be positive.");
        }
        if (request.RequestId.Value == Guid.Empty)
        {
            errors.Add("A stream request ID is required.");
        }
        if (string.IsNullOrWhiteSpace(request.ChannelKey) || request.ChannelKey.Length > VirtualOutputProtocol.MaximumChannelKeyLength)
        {
            errors.Add($"Channel key is required and cannot exceed {VirtualOutputProtocol.MaximumChannelKeyLength} characters.");
        }
        if (!Enum.IsDefined(request.Stage))
        {
            errors.Add("The requested stream stage is invalid.");
        }
        if (request.Client.AuthenticatedClientId?.Length > VirtualOutputProtocol.MaximumAuthenticatedClientIdLength)
        {
            errors.Add($"Authenticated client ID cannot exceed {VirtualOutputProtocol.MaximumAuthenticatedClientIdLength} characters.");
        }
        if (request.Client.AuthenticatedClientName?.Length > VirtualOutputProtocol.MaximumAuthenticatedClientNameLength)
        {
            errors.Add($"Authenticated client name cannot exceed {VirtualOutputProtocol.MaximumAuthenticatedClientNameLength} characters.");
        }
        return errors;
    }

    /// <summary>Returns close-stream request validation errors.</summary>
    public static IReadOnlyList<string> Validate(VirtualOutputCloseStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (request.Session.SessionId.Value == Guid.Empty)
        {
            errors.Add("A worker session is required.");
        }
        if (request.Session.Generation < 1)
        {
            errors.Add("Worker generation must be positive.");
        }
        if (request.RequestId.Value == Guid.Empty)
        {
            errors.Add("A stream request ID is required.");
        }
        if (request.StreamSessionId.Value == Guid.Empty)
        {
            errors.Add("A Lineup stream session ID is required.");
        }
        return errors;
    }

    /// <summary>Returns worker stream-status report validation errors.</summary>
    public static IReadOnlyList<string> Validate(VirtualOutputStreamStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = new List<string>();
        if (report.Session.SessionId.Value == Guid.Empty)
        {
            errors.Add("A worker session is required.");
        }
        if (report.Session.Generation < 1)
        {
            errors.Add("Worker generation must be positive.");
        }
        if (report.RequestId.Value == Guid.Empty)
        {
            errors.Add("A stream request ID is required.");
        }
        if (report.StreamSessionId.Value == Guid.Empty)
        {
            errors.Add("A Lineup stream session ID is required.");
        }
        if (string.IsNullOrWhiteSpace(report.WorkerMediaSessionId) ||
            report.WorkerMediaSessionId.Length > VirtualOutputProtocol.MaximumWorkerMediaSessionIdLength)
        {
            errors.Add($"Worker media-session ID is required and cannot exceed {VirtualOutputProtocol.MaximumWorkerMediaSessionIdLength} characters.");
        }
        if (!Enum.IsDefined(report.Format))
        {
            errors.Add("The final stream format is invalid.");
        }
        if (!Enum.IsDefined(report.StartupStage))
        {
            errors.Add("The stream startup stage is invalid.");
        }
        if (report.LastFailure?.Length > VirtualOutputProtocol.MaximumStreamFailureLength)
        {
            errors.Add($"The stream failure cannot exceed {VirtualOutputProtocol.MaximumStreamFailureLength} characters.");
        }
        var trackCount = (report.Tracks?.Count ?? 0) +
            (report.PhysicalInput?.Tracks?.Count ?? 0) +
            (report.PhysicalOutput?.Tracks?.Count ?? 0) +
            (report.VirtualOutput?.Tracks?.Count ?? 0);
        if (trackCount > VirtualOutputProtocol.MaximumStreamStatusTrackCount)
        {
            errors.Add($"The stream status cannot contain more than {VirtualOutputProtocol.MaximumStreamStatusTrackCount} tracks.");
        }
        if (report.Tracks is null ||
            report.PhysicalInput is { Tracks: null } ||
            report.PhysicalOutput is { Tracks: null } ||
            report.VirtualOutput is { Tracks: null })
        {
            errors.Add("Stream-status track collections cannot be null.");
        }
        else if (report.Tracks.Any(IsInvalid) ||
            report.PhysicalInput?.Tracks.Any(IsInvalid) == true ||
            report.PhysicalOutput?.Tracks.Any(IsInvalid) == true ||
            report.VirtualOutput?.Tracks.Any(IsInvalid) == true)
        {
            errors.Add("Stream-status tracks contain invalid or oversized media metadata.");
        }
        return errors;
    }

    private static bool IsInvalid(ActiveStreamTrack track) =>
        !Enum.IsDefined(track.Type) ||
        track.SourceIndex < 0 ||
        IsInvalidCodec(track.SourceCodec) ||
        IsInvalidCodec(track.OutputCodec) ||
        IsInvalidText(track.Language) ||
        IsInvalidText(track.Title) ||
        IsInvalidText(track.OutputTitle);

    private static bool IsInvalid(ActiveStreamMediaTrack track) =>
        !Enum.IsDefined(track.Type) ||
        track.Index < 0 ||
        IsInvalidCodec(track.Codec) ||
        IsInvalidText(track.Language) ||
        IsInvalidText(track.Title) ||
        IsInvalidText(track.RenditionTitle);

    private static bool IsInvalidCodec(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > VirtualOutputProtocol.MaximumStreamCodecLength;

    private static bool IsInvalidText(string? value) =>
        value?.Length > VirtualOutputProtocol.MaximumStreamMediaTextLength;
}

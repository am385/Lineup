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
}

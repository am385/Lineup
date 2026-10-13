using System.Net;
using Lineup.VirtualOutputs.Protocol;
using Lineup.VirtualOutputs.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Controllers;

/// <summary>Provides one-time bootstrap retrieval for externally supervised virtual-output workers.</summary>
[ApiController]
[Route("api/virtual-outputs")]
public sealed class VirtualOutputEnrollmentController : ControllerBase
{
    private readonly ILineupSettingsService _settings;
    private readonly IVirtualOutputExternalEnrollmentStore _enrollments;

    /// <summary>Initializes the external-worker enrollment controller.</summary>
    public VirtualOutputEnrollmentController(
        ILineupSettingsService settings,
        IVirtualOutputExternalEnrollmentStore enrollments)
    {
        _settings = settings;
        _enrollments = enrollments;
    }

    /// <summary>Retrieves and invalidates one pending external-worker bootstrap descriptor.</summary>
    [HttpPost("{virtualDeviceId:guid}/enrollment")]
    public ActionResult<VirtualOutputWorkerBootstrap> TakeEnrollment(Guid virtualDeviceId)
    {
        if (HttpContext.Connection.RemoteIpAddress is not { } remoteAddress ||
            !IPAddress.IsLoopback(remoteAddress))
        {
            return NotFound();
        }
        var id = new VirtualDeviceId(virtualDeviceId);
        var device = _settings.Settings.Streaming.VirtualDevices.SingleOrDefault(candidate => candidate.Id == id);
        if (device?.Hosting?.ExecutionMode != VirtualOutputExecutionMode.ExternalProcess)
        {
            return NotFound(new VirtualOutputEnrollmentError
            {
                Error = "The requested virtual output is not configured for external worker enrollment."
            });
        }
        var enrollment = _enrollments.Take(id);
        return enrollment is null
            ? NotFound(new VirtualOutputEnrollmentError
            {
                Error = "No pending enrollment is available. Save the output configuration to create a new activation."
            })
            : Ok(enrollment.Bootstrap);
    }
}

/// <summary>Describes a safe external-worker enrollment failure.</summary>
public sealed record VirtualOutputEnrollmentError
{
    /// <summary>Gets the operator-facing failure description.</summary>
    public required string Error { get; init; }
}

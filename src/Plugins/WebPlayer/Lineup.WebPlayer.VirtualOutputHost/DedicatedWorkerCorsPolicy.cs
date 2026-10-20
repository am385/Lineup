using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Lineup.WebPlayer.VirtualOutputHost;

/// <summary>Creates the browser-facing CORS policy for dedicated Web Player listeners.</summary>
internal static class DedicatedWorkerCorsPolicy
{
    /// <summary>Creates a policy that preserves the worker's public-listener behavior for success and error responses.</summary>
    public static CorsPolicy Create() =>
        new CorsPolicyBuilder()
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .Build();
}

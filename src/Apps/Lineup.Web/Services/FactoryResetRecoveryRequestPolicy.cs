namespace Lineup.Web.Services;

/// <summary>
/// Describes how factory-reset recovery mode handles an HTTP request.
/// </summary>
public enum FactoryResetRecoveryRequestAction
{
    /// <summary>Allows the request to continue through the normal pipeline.</summary>
    Continue,

    /// <summary>Rejects an unavailable API request with a recovery status response.</summary>
    RejectApi,

    /// <summary>Redirects a page request to the recovery settings surface.</summary>
    RedirectToSettings
}

/// <summary>
/// Restricts requests while incompatible persisted state awaits an explicit factory reset.
/// </summary>
public static class FactoryResetRecoveryRequestPolicy
{
    /// <summary>
    /// Determines how recovery mode handles a request path.
    /// </summary>
    /// <param name="path">Requested application path.</param>
    /// <returns>The recovery-mode action for the path.</returns>
    public static FactoryResetRecoveryRequestAction GetAction(PathString path)
    {
        if (path == "/")
        {
            return FactoryResetRecoveryRequestAction.RedirectToSettings;
        }

        if (path.StartsWithSegments("/api"))
        {
            return path.StartsWithSegments("/api/v1/status") || path.StartsWithSegments("/api/runtime")
                ? FactoryResetRecoveryRequestAction.Continue
                : FactoryResetRecoveryRequestAction.RejectApi;
        }

        return path.StartsWithSegments("/settings") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_blazor") ||
            path.StartsWithSegments("/_content") ||
            Path.HasExtension(path)
                ? FactoryResetRecoveryRequestAction.Continue
                : FactoryResetRecoveryRequestAction.RedirectToSettings;
    }
}

using System.Collections.Concurrent;
using Lineup.HDHomeRun.Device.Models;

namespace Lineup.Web.Services;

/// <summary>
/// Captures the latest successful resolution of a virtual HDHomeRun device.
/// </summary>
internal sealed record VirtualDeviceResolutionSnapshot(string VirtualDeviceId, string PhysicalAddress, string FriendlyName, int TunerCount, HDHomeRunDeviceInfo PhysicalDevice, DateTime ResolvedAtUtc);

/// <summary>
/// Captures passive virtual-device resolution state and diagnostics.
/// </summary>
internal sealed record VirtualDeviceResolutionState(VirtualDeviceResolutionSnapshot? LastSuccessfulSnapshot, DateTime? LastAttemptAtUtc, string? LastError);

/// <summary>
/// Stores the latest passive virtual-device resolution state for diagnostics.
/// </summary>
public sealed class VirtualDeviceStatusCache
{
    private readonly ConcurrentDictionary<string, VirtualDeviceResolutionState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a successful virtual-device resolution.
    /// </summary>
    internal void RecordSuccess(HdHomeRunProxyProfileSnapshot snapshot)
    {
        var resolution = new VirtualDeviceResolutionSnapshot(snapshot.DeviceIdText, snapshot.Settings.PhysicalAddress, snapshot.FriendlyName, snapshot.TunerCount, snapshot.PhysicalDevice, DateTime.UtcNow);
        _states.AddOrUpdate(
            snapshot.DeviceIdText,
            _ => new VirtualDeviceResolutionState(resolution, resolution.ResolvedAtUtc, null),
            (_, _) => new VirtualDeviceResolutionState(resolution, resolution.ResolvedAtUtc, null));
    }

    /// <summary>
    /// Records a failed virtual-device resolution attempt.
    /// </summary>
    internal void RecordFailure(HdHomeRunProxyProfileSettings profile, string error)
    {
        var attemptedAtUtc = DateTime.UtcNow;
        _states.AddOrUpdate(
            profile.VirtualDeviceId,
            _ => new VirtualDeviceResolutionState(null, attemptedAtUtc, error),
            (_, current) => current with { LastAttemptAtUtc = attemptedAtUtc, LastError = error });
    }

    /// <summary>
    /// Gets cached resolution state when it still matches the configured physical address.
    /// </summary>
    internal VirtualDeviceResolutionState? Get(string virtualDeviceId, string physicalAddress)
    {
        if (!_states.TryGetValue(virtualDeviceId, out var state))
        {
            return null;
        }

        return state.LastSuccessfulSnapshot is { } snapshot &&
            !string.Equals(snapshot.PhysicalAddress, physicalAddress, StringComparison.OrdinalIgnoreCase)
                ? null
                : state;
    }
}

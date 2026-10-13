using System.Text;

namespace Lineup.LineupLink.Protocol;

/// <summary>Creates stable source identities for channels imported through Lineup Link.</summary>
public static class LineupLinkIdentity
{
    /// <summary>Creates a collision-safe provider key for one remote channel.</summary>
    public static string CreateProviderKey(Guid instanceId, Guid virtualDeviceId, string channelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(channelKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{instanceId:N}:{virtualDeviceId:N}:{encoded}";
    }
}

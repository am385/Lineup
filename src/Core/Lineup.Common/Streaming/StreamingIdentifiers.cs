namespace Lineup.Common.Streaming;

/// <summary>
/// Identifies one configured physical tuner device.
/// </summary>
/// <param name="Value">Stable persisted identifier.</param>
public readonly record struct PhysicalDeviceId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static PhysicalDeviceId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one configured virtual tuner device.
/// </summary>
/// <param name="Value">Stable persisted identifier.</param>
public readonly record struct VirtualDeviceId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static VirtualDeviceId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies one source channel within a physical device.
/// </summary>
/// <param name="PhysicalDeviceId">Owning physical device.</param>
/// <param name="ProviderKey">Stable adapter-defined channel key.</param>
public readonly record struct SourceChannelId(PhysicalDeviceId PhysicalDeviceId, string ProviderKey);

/// <summary>
/// Identifies one projected channel within a virtual device.
/// </summary>
/// <param name="VirtualDeviceId">Owning virtual device.</param>
/// <param name="Value">Stable projected channel identifier.</param>
public readonly record struct VirtualChannelId(VirtualDeviceId VirtualDeviceId, Guid Value)
{
    /// <summary>Creates an identifier for a virtual device.</summary>
    public static VirtualChannelId New(VirtualDeviceId virtualDeviceId) => new(virtualDeviceId, Guid.NewGuid());
}

/// <summary>
/// Identifies one active hosted stream session.
/// </summary>
/// <param name="Value">Stable session identifier.</param>
public readonly record struct StreamSessionId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static StreamSessionId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

namespace Lineup.Core.Settings;

/// <summary>Fences physical runtime and lineup publication against the last successfully saved configuration.</summary>
internal interface IPhysicalConfigurationAuthority
{
    /// <summary>Gets whether the physical definitions still match the saved desired configuration.</summary>
    bool IsDesired(StreamingConfiguration configuration);

    /// <summary>Publishes matching physical state while preventing a settings save from superseding it during asynchronous publication.</summary>
    Task<bool> TryPublishAsync(StreamingConfiguration configuration, Func<CancellationToken, Task> publish, CancellationToken cancellationToken);
}

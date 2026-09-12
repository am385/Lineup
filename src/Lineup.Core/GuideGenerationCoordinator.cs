namespace Lineup.Core;

/// <summary>
/// Serializes canonical guide publication, normalized database replacement, and generation marker updates.
/// </summary>
public sealed class GuideGenerationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Executes a complete guide generation transition exclusively.
    /// </summary>
    /// <param name="transition">The generation transition to execute.</param>
    /// <param name="cancellationToken">A token used to cancel waiting for or executing the transition.</param>
    public async Task ExecuteAsync(Func<CancellationToken, Task> transition, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await transition(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}

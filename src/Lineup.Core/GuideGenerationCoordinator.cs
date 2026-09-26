namespace Lineup.Core;

/// <summary>
/// Serializes normalized guide database updates.
/// </summary>
public sealed class GuideGenerationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Executes a complete guide database transition exclusively.
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

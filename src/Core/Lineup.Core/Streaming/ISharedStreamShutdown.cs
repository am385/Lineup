namespace Lineup.Core.Streaming;

/// <summary>Stops shared streaming producers during host shutdown.</summary>
public interface ISharedStreamShutdown
{
    /// <summary>Stops all shared producers.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

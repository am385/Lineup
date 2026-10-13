namespace Lineup.Core.Storage;

/// <summary>
/// Supplies the amount of ended guide history retained in authoritative storage.
/// </summary>
public interface IEpgRetentionPolicy
{
    /// <summary>Gets the program history retention duration.</summary>
    TimeSpan HistoryRetention { get; }
}

/// <summary>
/// Supplies the default 24-hour guide history retention outside the web host.
/// </summary>
public sealed class DefaultEpgRetentionPolicy : IEpgRetentionPolicy
{
    /// <inheritdoc />
    public TimeSpan HistoryRetention => TimeSpan.FromHours(24);
}

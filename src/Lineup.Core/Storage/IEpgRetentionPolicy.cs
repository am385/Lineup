namespace Lineup.Core.Storage;

/// <summary>
/// Supplies the amount of ended guide history retained in authoritative storage.
/// </summary>
public interface IEpgRetentionPolicy
{
    /// <summary>Gets the programme history retention duration.</summary>
    TimeSpan HistoryRetention { get; }
}

/// <summary>
/// Supplies the default 24-hour guide history retention outside the web host.
/// </summary>
internal sealed class DefaultEpgRetentionPolicy : IEpgRetentionPolicy
{
    /// <inheritdoc />
    public TimeSpan HistoryRetention => TimeSpan.FromHours(24);
}

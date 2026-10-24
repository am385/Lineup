namespace Lineup.Web.Services;

/// <summary>
/// Publishes transient user-facing status notifications within the current UI circuit.
/// </summary>
public interface IStatusNotificationService
{
    /// <summary>
    /// Occurs when the current notification changes.
    /// </summary>
    event Action? OnChanged;

    /// <summary>
    /// Gets the active notifications in the order they were published.
    /// </summary>
    IReadOnlyList<StatusNotificationMessage> Notifications { get; }

    /// <summary>
    /// Shows a success notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    void ShowSuccess(string message);

    /// <summary>
    /// Shows an error notification.
    /// </summary>
    /// <param name="message">The message to display.</param>
    void ShowError(string message);

    /// <summary>
    /// Dismisses an active notification.
    /// </summary>
    /// <param name="id">The notification identifier.</param>
    void Dismiss(Guid id);

    /// <summary>
    /// Clears all active notifications.
    /// </summary>
    void Clear();
}

/// <summary>
/// Describes one transient user-facing status notification.
/// </summary>
/// <param name="Id">The notification identifier.</param>
/// <param name="Message">The message to display.</param>
/// <param name="IsError">A value indicating whether the notification represents an error.</param>
public sealed record StatusNotificationMessage(Guid Id, string Message, bool IsError);

/// <summary>
/// Maintains transient user-facing status notification state for one UI circuit.
/// </summary>
public sealed class StatusNotificationService : IStatusNotificationService
{
    private const int MaximumNotifications = 5;
    private readonly List<StatusNotificationMessage> _notifications = [];
    private readonly IReadOnlyList<StatusNotificationMessage> _readOnlyNotifications;

    /// <summary>
    /// Initializes a new notification service.
    /// </summary>
    public StatusNotificationService()
    {
        _readOnlyNotifications = _notifications.AsReadOnly();
    }

    /// <inheritdoc />
    public event Action? OnChanged;

    /// <inheritdoc />
    public IReadOnlyList<StatusNotificationMessage> Notifications => _readOnlyNotifications;

    /// <inheritdoc />
    public void ShowSuccess(string message) => Show(message, isError: false);

    /// <inheritdoc />
    public void ShowError(string message) => Show(message, isError: true);

    /// <inheritdoc />
    public void Dismiss(Guid id)
    {
        var removed = _notifications.RemoveAll(notification => notification.Id == id);
        if (removed > 0)
        {
            OnChanged?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_notifications.Count == 0)
        {
            return;
        }

        _notifications.Clear();
        OnChanged?.Invoke();
    }

    private void Show(string message, bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (_notifications.Count == MaximumNotifications)
        {
            _notifications.RemoveAt(0);
        }

        _notifications.Add(new StatusNotificationMessage(Guid.NewGuid(), message, isError));
        OnChanged?.Invoke();
    }
}

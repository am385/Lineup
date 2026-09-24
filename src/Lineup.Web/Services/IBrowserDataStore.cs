namespace Lineup.Web.Services;

/// <summary>
/// Provides typed access to browser-local application state.
/// </summary>
public interface IBrowserDataStore
{
    /// <summary>
    /// Reads and deserializes a browser-local value.
    /// </summary>
    /// <typeparam name="T">Stored value type.</typeparam>
    /// <param name="key">Stable browser storage key.</param>
    /// <param name="cancellationToken">Cancels the browser interop call.</param>
    /// <returns>The stored value, or the default value when the key is absent.</returns>
    ValueTask<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes and writes a browser-local value.
    /// </summary>
    /// <typeparam name="T">Stored value type.</typeparam>
    /// <param name="key">Stable browser storage key.</param>
    /// <param name="value">Value to persist.</param>
    /// <param name="cancellationToken">Cancels the browser interop call.</param>
    ValueTask WriteAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a browser-local value.
    /// </summary>
    /// <param name="key">Stable browser storage key.</param>
    /// <param name="cancellationToken">Cancels the browser interop call.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

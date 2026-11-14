using System.Text.Json;
using Microsoft.JSInterop;

namespace Lineup.App.Web.Services;

/// <summary>
/// Stores typed application state in the current browser's local storage.
/// </summary>
public sealed class BrowserDataStore : IBrowserDataStore
{
    private readonly IJSRuntime _jsRuntime;

    /// <summary>
    /// Initializes a browser-local data store.
    /// </summary>
    /// <param name="jsRuntime">Browser JavaScript runtime.</param>
    public BrowserDataStore(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>Reads the browser data store asynchronously.</summary>
    /// <param name="key">The key value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <typeparam name="T">Type represented by the generic parameter.</typeparam>
    /// <returns>The operation's result.</returns>
    public async ValueTask<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>Writes the browser data store asynchronously.</summary>
    /// <param name="key">The key value.</param>
    /// <param name="value">The value value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <typeparam name="T">Type represented by the generic parameter.</typeparam>
    /// <returns>A task that completes when the operation has finished.</returns>
    public ValueTask WriteAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, JsonSerializer.Serialize(value));
    }

    /// <summary>Removes the browser data store asynchronously.</summary>
    /// <param name="key">The key value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _jsRuntime.InvokeVoidAsync("localStorage.removeItem", cancellationToken, key);
    }
}

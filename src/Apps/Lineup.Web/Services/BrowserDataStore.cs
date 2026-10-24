using System.Text.Json;
using Microsoft.JSInterop;

namespace Lineup.Web.Services;

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

    /// <inheritdoc />
    public async ValueTask<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, JsonSerializer.Serialize(value));
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _jsRuntime.InvokeVoidAsync("localStorage.removeItem", cancellationToken, key);
    }
}

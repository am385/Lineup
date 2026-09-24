using Bunit;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies typed browser-local storage operations.
/// </summary>
public class BrowserDataStoreTests
{
    private const string StorageKey = "lineup-test-state";

    /// <summary>
    /// Verifies typed values round-trip through browser local storage JSON.
    /// </summary>
    [Fact]
    public async Task ReadAsync_StoredJson_ReturnsTypedValue()
    {
        // Arrange
        using var context = new BunitContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult("""{"Enabled":true}""");
        var store = new BrowserDataStore(context.Services.GetRequiredService<IJSRuntime>());

        // Act
        var value = await store.ReadAsync<TestState>(StorageKey, Xunit.TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(value);
        Assert.True(value.Enabled);
    }

    /// <summary>
    /// Verifies writes serialize typed values under the requested browser key.
    /// </summary>
    [Fact]
    public async Task WriteAsync_TypedValue_WritesJson()
    {
        // Arrange
        using var context = new BunitContext();
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        var store = new BrowserDataStore(context.Services.GetRequiredService<IJSRuntime>());

        // Act
        await store.WriteAsync(StorageKey, new TestState { Enabled = true }, Xunit.TestContext.Current.CancellationToken);

        // Assert
        var invocation = Assert.Single(context.JSInterop.Invocations);
        Assert.Equal("localStorage.setItem", invocation.Identifier);
        Assert.Equal(StorageKey, invocation.Arguments[0]);
        Assert.True(JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[1])).RootElement.GetProperty("Enabled").GetBoolean());
    }

    /// <summary>
    /// Verifies corrupt browser JSON remains an explicit deserialization failure.
    /// </summary>
    [Fact]
    public async Task ReadAsync_CorruptJson_ThrowsJsonException()
    {
        // Arrange
        using var context = new BunitContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult("{invalid");
        var store = new BrowserDataStore(context.Services.GetRequiredService<IJSRuntime>());

        // Act
        var action = async () => await store.ReadAsync<TestState>(StorageKey, Xunit.TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<JsonException>(action);
    }

    private sealed class TestState
    {
        public bool Enabled { get; init; }
    }
}

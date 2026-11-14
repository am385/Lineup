namespace Lineup.Integration.Streaming.Sweep;

/// <summary>Represents the browser lease.</summary>
/// <param name="playwright">The playwright value.</param>
/// <param name="context">Context used by the operation.</param>
internal sealed class BrowserLease(IPlaywright playwright, IBrowserContext context) : IAsyncDisposable
{
    /// <summary>Gets the context.</summary>
    public IBrowserContext Context { get; } = context;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        playwright.Dispose();
    }
}

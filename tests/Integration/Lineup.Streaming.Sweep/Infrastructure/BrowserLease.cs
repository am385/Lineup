namespace Lineup.Streaming.Sweep;

internal sealed class BrowserLease(IPlaywright playwright, IBrowserContext context) : IAsyncDisposable
{
    public IBrowserContext Context { get; } = context;

    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        playwright.Dispose();
    }
}

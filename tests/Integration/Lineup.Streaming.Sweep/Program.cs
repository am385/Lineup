using Lineup.Streaming.Sweep;

try
{
    await SweepApplication.RunAsync(args);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Sweep cancelled. Inspect the canonical checkpoint before resuming.");
    Environment.ExitCode = 130;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException
    or HttpRequestException or Microsoft.Playwright.PlaywrightException or TimeoutException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 3;
}

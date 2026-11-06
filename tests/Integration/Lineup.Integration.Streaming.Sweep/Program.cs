using Lineup.Integration.Streaming.Sweep;

try
{
    await SweepApplication.RunAsync(args);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or System.Text.Json.JsonException
    or HttpRequestException or Microsoft.Playwright.PlaywrightException or TimeoutException or OperationCanceledException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 3;
}

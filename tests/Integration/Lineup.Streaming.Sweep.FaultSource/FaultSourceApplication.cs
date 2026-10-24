using System.Collections.Concurrent;

namespace Lineup.Streaming.Sweep.FaultSource;

internal static class FaultSourceApplication
{
    private const string BaseUrl = "http://fault-source:8090";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:8090");
        var app = builder.Build();
        var streamRequests = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        app.MapGet("/discover.json", () => Results.Ok(new
        {
            FriendlyName = "Lineup Fault Source",
            ModelNumber = "TEST-4",
            FirmwareName = "lineup_fault_source",
            FirmwareVersion = "20261016",
            DeviceID = "FFFFFFFF",
            DeviceAuth = "fault-source",
            BaseURL = BaseUrl,
            LineupURL = $"{BaseUrl}/lineup.json",
            TunerCount = 4
        }));

        app.MapGet("/lineup.json", () => Results.Ok(CreateLineup()));
        app.MapGet("/auto/v{channel}", (HttpContext context, string channel) =>
        {
            streamRequests.AddOrUpdate(channel, 1, static (_, count) => count + 1);
            var diagnostic = channel switch
            {
                "811.1" => "811 Content Protection Required",
                "807.1" => "807 No Video Data",
                "806.1" => "806 No Signal",
                "805.1" => "805 All Tuners In Use",
                _ => "Synthetic source unavailable"
            };
            if (channel != "806.1")
            {
                context.Response.Headers["X-HDHomeRun-Error"] = diagnostic;
            }

            return Results.Text(diagnostic, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/metrics", () => Results.Ok(new
        {
            StreamRequests = streamRequests
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }));

        app.MapGet("/media/delayed-audio", async (HttpContext context) =>
        {
            streamRequests.AddOrUpdate("900.1", 1, static (_, count) => count + 1);
            context.Response.ContentType = "video/mp2t";
            await FfmpegMediaSource.WriteDelayedAudioAsync(context.Response.Body, context.RequestAborted);
        });
        app.MapGet("/media/video-only", async (HttpContext context) =>
        {
            streamRequests.AddOrUpdate("901.1", 1, static (_, count) => count + 1);
            context.Response.ContentType = "video/mp2t";
            await FfmpegMediaSource.WriteVideoOnlyAsync(context.Response.Body, context.RequestAborted);
        });
        app.MapGet("/health", () => Results.Ok(new { ready = true }));
        return app;
    }

    private static object[] CreateLineup()
    {
        return
        [
            CreateChannel("2.1", "Generic Failure", "AC3", false, $"{BaseUrl}/auto/v2.1"),
            CreateChannel("811.1", "Protected Content", "AC3", true, $"{BaseUrl}/auto/v811.1"),
            CreateChannel("807.1", "No Video Data", "AC3", false, $"{BaseUrl}/auto/v807.1"),
            CreateChannel("806.1", "No Signal", "AC3", false, $"{BaseUrl}/auto/v806.1"),
            CreateChannel("805.1", "All Tuners In Use", "AC3", false, $"{BaseUrl}/auto/v805.1"),
            CreateChannel("900.1", "Delayed Advertised Audio", "AC3", false, $"{BaseUrl}/media/delayed-audio"),
            CreateChannel("901.1", "Intentional Video Only", null, false, $"{BaseUrl}/media/video-only")
        ];
    }

    private static object CreateChannel(string number, string name, string? audioCodec, bool isProtected, string url)
    {
        return new
        {
            GuideNumber = number,
            GuideName = name,
            VideoCodec = "MPEG2",
            AudioCodec = audioCodec,
            DRM = isProtected ? 1 : 0,
            HD = 1,
            URL = url
        };
    }
}

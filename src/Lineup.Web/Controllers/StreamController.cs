using Lineup.Web.Services;
using Lineup.HDHomeRun.Device;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace Lineup.Web.Controllers;

/// <summary>
/// Proxies video streams from HDHomeRun devices to avoid mixed content issues.
/// The browser connects to this HTTPS endpoint which forwards the HTTP stream from the device.
/// Supports multiple output formats: direct proxy, HLS (disk), and fMP4 (memory).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppSettingsService _settingsService;
    private readonly ILogger<StreamController> _logger;

    // Track active HLS streams by session ID
    private static readonly ConcurrentDictionary<string, HlsSession> _hlsSessions = new();

    // Track active fMP4 streams
    private static readonly ConcurrentDictionary<string, FMp4Session> _fmp4Sessions = new();

    public StreamController(
        IHttpClientFactory httpClientFactory,
        IAppSettingsService settingsService,
        ILogger<StreamController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Proxies the live TV stream for a given channel.
    /// Uses transcoding to ensure browser-compatible codecs (H.264 + AAC).
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <param name="transcode">Transcode profile: none, mobile, heavy, internet720, internet480, internet360 (default: heavy)</param>
    /// <returns>MPEG-TS video stream</returns>
    [HttpGet("{channel}")]
    public async Task Stream(string channel, [FromQuery] string transcode = "heavy")
    {
        var deviceAddress = _settingsService.Settings.DeviceAddress;

        // Build stream URL with optional transcoding
        // Transcoding converts AC-3 audio to AAC which is browser-compatible
        // FLEX 4K profiles: mobile, heavy, internet720, internet480, internet360
        var streamUrl = $"http://{deviceAddress}:{DeviceEndpoints.StreamingPort}/auto/v{channel}";

        if (!string.IsNullOrEmpty(transcode) && transcode != "none")
        {
            streamUrl += $"?transcode={transcode}";
        }

        _logger.LogInformation("Proxying stream for channel {Channel} with transcode={Transcode}, URL: {Url}",
            channel, transcode, streamUrl);

        try
        {
            var httpClient = _httpClientFactory.CreateClient("StreamProxy");


            // Set a long timeout for live streams
            httpClient.Timeout = TimeSpan.FromHours(24);

            var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);

            // Stream the response directly without buffering
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to connect to stream: {StatusCode}", response.StatusCode);
                Response.StatusCode = (int)response.StatusCode;
                return;
            }

            // Set response headers for MPEG-TS stream
            Response.ContentType = "video/mp2t";
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            Response.Headers["Connection"] = "keep-alive";

            // Stream the content
            await using var sourceStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

            var buffer = new byte[64 * 1024]; // 64KB buffer
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer, HttpContext.RequestAborted)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - this is normal for live streams
            _logger.LogDebug("Stream closed for channel {Channel} (client disconnected)", channel);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to HDHomeRun device at {Url}", streamUrl);
            Response.StatusCode = 502; // Bad Gateway
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming channel {Channel}", channel);
            Response.StatusCode = 500;
        }
    }

    /// <summary>
    /// Test endpoint to verify the HDHomeRun device transcoding capability
    /// </summary>
    [HttpGet("test/{channel}")]
    public async Task<IActionResult> TestTranscode(string channel, [FromQuery] string transcode = "heavy")
    {
        var deviceAddress = _settingsService.Settings.DeviceAddress;
        var streamUrl = $"http://{deviceAddress}:{DeviceEndpoints.StreamingPort}/auto/v{channel}";

        if (!string.IsNullOrEmpty(transcode) && transcode != "none")
        {
            streamUrl += $"?transcode={transcode}";
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("StreamProxy");
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            // Read a small chunk to see if stream starts
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));

            return Ok(new
            {
                success = true,
                channel,
                transcode,
                deviceUrl = streamUrl,
                statusCode = (int)response.StatusCode,
                contentType = response.Content.Headers.ContentType?.ToString(),
                bytesReceived = bytesRead,
                message = bytesRead > 0 ? "Stream is responding" : "No data received"
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                channel,
                transcode,
                deviceUrl = streamUrl,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Streams video using fragmented MP4 (fMP4) directly to memory/pipe.
    /// No disk I/O required - FFmpeg outputs to stdout which is piped to the browser.
    /// Uses Media Source Extensions (MSE) compatible output.
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <returns>Fragmented MP4 video stream</returns>
    [HttpGet("fmp4/{channel}")]
    public async Task StreamFmp4(string channel)
    {
        var deviceAddress = _settingsService.Settings.DeviceAddress;
        var streamUrl = $"http://{deviceAddress}:{DeviceEndpoints.StreamingPort}/auto/v{channel}";

        _logger.LogInformation("Starting fMP4 stream for channel {Channel}", channel);

        Process? ffmpegProcess = null;
        var sessionId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            // FFmpeg command for fragmented MP4 output to stdout:
            // -movflags frag_keyframe+empty_moov+default_base_moof : Required for streaming fMP4
            // -frag_duration 1000000 : 1 second fragments
            // -min_frag_duration 500000 : Minimum 0.5 second fragments
            var ffmpegArgs = $"-fflags +genpts -i \"{streamUrl}\" " +
                "-c:v libx264 -preset ultrafast -tune zerolatency " +
                "-profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-flags +cgop -g 30 -b:v 2500k " +
                "-c:a aac -b:a 128k -ac 2 -ar 44100 " +
                "-f mp4 " +
                "-movflags frag_keyframe+empty_moov+default_base_moof " +
                "-frag_duration 1000000 " +
                "pipe:1";

            _logger.LogDebug("FFmpeg fMP4 command: ffmpeg {Args}", ffmpegArgs);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ffmpegProcess = Process.Start(startInfo);

            if (ffmpegProcess == null)
            {
                _logger.LogError("Failed to start FFmpeg process for fMP4");
                Response.StatusCode = 500;
                return;
            }

            var session = new FMp4Session
            {
                SessionId = sessionId,
                Channel = channel,
                Process = ffmpegProcess,
                StartTime = DateTime.UtcNow
            };
            _fmp4Sessions[sessionId] = session;

            // Log FFmpeg errors in background
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ffmpegProcess.HasExited)
                    {
                        var line = await ffmpegProcess.StandardError.ReadLineAsync();
                        if (line == null)
                        {
                            // End of stream reached
                            break;
                        }
                        if (!string.IsNullOrEmpty(line))
                        {
                            _logger.LogDebug("FFmpeg fMP4 [{SessionId}]: {Line}", sessionId, line);
                        }
                    }
                }
                catch { /* Ignore */ }
            });

            // Set response headers for fMP4 stream
            Response.ContentType = "video/mp4";
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Session-Id"] = sessionId;

            // Stream FFmpeg output directly to response
            var buffer = new byte[64 * 1024];
            int bytesRead;

            while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(
                buffer, HttpContext.RequestAborted)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("fMP4 stream closed for channel {Channel} (client disconnected)", channel);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError("FFmpeg not found for fMP4 streaming");
            Response.StatusCode = 500;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in fMP4 stream for channel {Channel}", channel);
            Response.StatusCode = 500;
        }
        finally
        {
            _fmp4Sessions.TryRemove(sessionId, out _);

            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.Kill(entireProcessTree: true);
                }
                catch { /* Ignore */ }
            }
            ffmpegProcess?.Dispose();
        }
    }

    /// <summary>
    /// Starts an HLS stream session and returns the playlist URL.
    /// HLS has native browser support - no JavaScript player library needed.
    /// Note: Uses disk for segment storage. For memory-only, use /fmp4/{channel}.
    /// </summary>
    /// <param name="channel">The channel number (e.g., "2.1", "5.1")</param>
    /// <returns>HLS playlist information</returns>
    [HttpPost("hls/start/{channel}")]
    public async Task<IActionResult> StartHlsStream(string channel)
    {
        var deviceAddress = _settingsService.Settings.DeviceAddress;
        var streamUrl = $"http://{deviceAddress}:{DeviceEndpoints.StreamingPort}/auto/v{channel}";

        // Generate unique session ID
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var hlsDir = Path.Combine(Path.GetTempPath(), "hdhomerun-hls", sessionId);

        // Clean up any existing session for this channel
        foreach (var existing in _hlsSessions.Values.Where(s => s.Channel == channel).ToList())
        {
            StopHlsSession(existing.SessionId);
        }

        Directory.CreateDirectory(hlsDir);

        _logger.LogInformation("Starting HLS stream for channel {Channel}, session {SessionId}, dir {Dir}",
            channel, sessionId, hlsDir);

        try
        {
            // FFmpeg HLS output command
            var playlistPath = Path.Combine(hlsDir, "stream.m3u8");

            // FFmpeg HLS command optimized for live streaming:
            // -fflags +genpts : Generate presentation timestamps
            // -flags +cgop : Use closed GOP for better seeking
            // -sc_threshold 0 : Disable scene change detection for consistent segments
            // -force_key_frames : Force keyframes for segment alignment
            // -hls_segment_type mpegts : Use MPEG-TS segments (better compatibility)
            // -hls_playlist_type event : Event playlist (segments accumulate)
            // -hls_init_time 0 : Start outputting segments immediately
            var ffmpegArgs = $"-fflags +genpts -i \"{streamUrl}\" " +
                "-c:v libx264 -preset ultrafast -tune zerolatency " +
                "-profile:v baseline -level 3.1 -pix_fmt yuv420p " +
                "-flags +cgop -sc_threshold 0 " +
                "-g 60 -keyint_min 60 -b:v 2500k " +
                "-c:a aac -b:a 128k -ac 2 -ar 44100 " +
                "-f hls " +
                "-hls_time 4 " +
                "-hls_list_size 10 " +
                "-hls_segment_type mpegts " +
                "-hls_flags delete_segments+append_list+omit_endlist " +
                "-hls_allow_cache 0 " +
                "-hls_start_number_source epoch " +
                $"\"{playlistPath}\"";

            _logger.LogInformation("FFmpeg command: ffmpeg {Args}", ffmpegArgs);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);

            if (process == null)
            {
                Directory.Delete(hlsDir, recursive: true);
                return StatusCode(500, new { error = "Failed to start FFmpeg process" });
            }

            var session = new HlsSession
            {
                SessionId = sessionId,
                Channel = channel,
                Process = process,
                HlsDirectory = hlsDir,
                PlaylistPath = playlistPath,
                StartTime = DateTime.UtcNow
            };

            _hlsSessions[sessionId] = session;

            // Capture FFmpeg stderr for error reporting
            var ffmpegErrors = new List<string>();

            // Log FFmpeg output in background
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line == null)
                        {
                            // End of stream reached
                            break;
                        }
                        if (!string.IsNullOrEmpty(line))
                        {
                            _logger.LogDebug("FFmpeg HLS [{SessionId}]: {Line}", sessionId, line);
                            if (ffmpegErrors.Count < 50) // Keep last 50 lines for error reporting
                            {
                                ffmpegErrors.Add(line);
                            }
                        }
                    }
                }
                catch { /* Ignore */ }
            });

            // Wait for playlist AND at least 2 segments to be created (up to 20 seconds)
            // This ensures the browser has enough content to start playing
            var segmentCount = 0;
            for (int i = 0; i < 200; i++)
            {
                // Check if process died
                if (process.HasExited)
                {
                    var exitCode = process.ExitCode;
                    StopHlsSession(sessionId);
                    _logger.LogError("FFmpeg exited with code {ExitCode}. Errors: {Errors}",
                        exitCode, string.Join("\n", ffmpegErrors.TakeLast(10)));
                    return StatusCode(500, new
                    {
                        error = $"FFmpeg exited with code {exitCode}",
                        ffmpegOutput = ffmpegErrors.TakeLast(10).ToList()
                    });
                }

                // Check for playlist and count segments
                if (System.IO.File.Exists(playlistPath))
                {
                    // Count .ts segment files
                    var tsFiles = Directory.GetFiles(hlsDir, "*.ts");
                    segmentCount = tsFiles.Length;


                    // Wait for at least 2 segments before returning
                    if (segmentCount >= 2)
                    {
                        _logger.LogInformation("HLS playlist ready with {SegmentCount} segments for session {SessionId}",
                            segmentCount, sessionId);
                        break;
                    }
                }
                await Task.Delay(100);
            }

            if (!System.IO.File.Exists(playlistPath) || segmentCount < 2)
            {
                StopHlsSession(sessionId);
                return StatusCode(500, new
                {
                    error = $"FFmpeg failed to create enough HLS segments (got {segmentCount}, need 2)",
                    ffmpegOutput = ffmpegErrors.TakeLast(10).ToList()
                });
            }

            return Ok(new
            {
                sessionId,
                channel,
                playlistUrl = $"/api/stream/hls/{sessionId}/stream.m3u8",
                segmentCount,
                message = "HLS stream started"
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            Directory.Delete(hlsDir, recursive: true);
            return StatusCode(500, new { error = "FFmpeg not found. Please install FFmpeg and add it to your PATH." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting HLS stream for channel {Channel}", channel);
            try { Directory.Delete(hlsDir, recursive: true); } catch { }
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Serves HLS playlist (.m3u8) files
    /// </summary>
    [HttpGet("hls/{sessionId}/{filename}")]
    public IActionResult GetHlsFile(string sessionId, string filename)
    {
        if (!_hlsSessions.TryGetValue(sessionId, out var session))
        {
            return NotFound(new { error = "Session not found" });
        }

        var filePath = Path.Combine(session.HlsDirectory, filename);

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        // Determine content type
        string contentType;
        if (filename.EndsWith(".m3u8"))
        {
            contentType = "application/vnd.apple.mpegurl";
        }
        else if (filename.EndsWith(".ts"))
        {
            contentType = "video/mp2t";
        }
        else
        {
            contentType = "application/octet-stream";
        }

        // Add CORS and caching headers for HLS
        Response.Headers["Access-Control-Allow-Origin"] = "*";
        Response.Headers["Cache-Control"] = "no-cache";

        return PhysicalFile(filePath, contentType);
    }

    /// <summary>
    /// Stops an HLS stream session
    /// </summary>
    [HttpPost("hls/stop/{sessionId}")]
    public IActionResult StopHls(string sessionId)
    {
        if (StopHlsSession(sessionId))
        {
            return Ok(new { message = "Session stopped" });
        }
        return NotFound(new { error = "Session not found" });
    }

    /// <summary>
    /// Lists active HLS sessions
    /// </summary>
    [HttpGet("hls/sessions")]
    public IActionResult GetHlsSessions()
    {
        var sessions = _hlsSessions.Values.Select(s => new
        {
            s.SessionId,
            s.Channel,
            startTime = s.StartTime,
            durationMinutes = (DateTime.UtcNow - s.StartTime).TotalMinutes,
            isRunning = s.Process is { HasExited: false }
        }).ToList();

        return Ok(sessions);
    }

    private bool StopHlsSession(string sessionId)
    {
        if (!_hlsSessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        _logger.LogInformation("Stopping HLS session {SessionId} for channel {Channel}", sessionId, session.Channel);

        try
        {
            if (session.Process is { HasExited: false })
            {
                // Send 'q' to FFmpeg stdin to quit gracefully (closes connections properly)
                try
                {
                    // On Windows, we can't easily send 'q' so we use Ctrl+C equivalent
                    // GenerateConsoleCtrlEvent doesn't work for processes without a console
                    // So we'll kill it but give it a moment to clean up
                    session.Process.Kill(entireProcessTree: false);

                    // Wait briefly for graceful shutdown
                    session.Process.WaitForExit(2000);

                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Force kill if graceful shutdown fails
                    try { session.Process.Kill(entireProcessTree: true); } catch { }
                }
            }
            session.Process?.Dispose();
        }
        catch { /* Ignore */ }

        // Clean up HLS files
        try
        {
            if (Directory.Exists(session.HlsDirectory))
            {
                Directory.Delete(session.HlsDirectory, recursive: true);
            }
        }
        catch { /* Ignore */ }

        return true;
    }


    private class HlsSession
    {
        public required string SessionId { get; init; }
        public required string Channel { get; init; }
        public required Process Process { get; init; }
        public required string HlsDirectory { get; init; }
        public required string PlaylistPath { get; init; }
        public DateTime StartTime { get; init; }
    }

    private class FMp4Session
    {
        public required string SessionId { get; init; }
        public required string Channel { get; init; }
        public required Process Process { get; init; }
        public DateTime StartTime { get; init; }
    }
}

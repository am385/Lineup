namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Verifies optional hardware encoding before a live input is consumed or output is published.</summary>
public static class HardwareEncodingRuntime
{
    /// <summary>Identifies encoder/device initialization failures that permit one explicitly enabled pre-publication software retry.</summary>
    public static bool IsInitializationFailure(string? diagnostics) =>
        diagnostics is not null && new[] { "No capable devices found", "Cannot load libcuda", "Cannot load nvcuda", "No NVENC capable devices", "OpenEncodeSessionEx failed",
            "Error initializing output stream", "Error while opening encoder", "Error opening encoder", "Failed to initialise VAAPI",
            "Failed to create a VAAPI device", "Device creation failed", "Error initializing an internal MFX session",
            "Error creating a MFX session", "Failed to initialise AMF", "AMF failed", "Cannot load libamfrt", "cannot create compression session" }
        .Any(marker => diagnostics.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Verifies a bounded synthetic encode, returning false only when software fallback was explicitly permitted.</summary>
    public static async Task<bool> VerifyAsync(HardwareEncodingSettings settings, IMediaProcessFactory factory, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.Encoder == HardwareVideoEncoder.Software)
        {
            return false;
        }
        try
        {
            if (!HardwareVideoEncodingPlanner.IsSupportedPlatform(settings.Encoder))
            {
                throw new HardwareEncodingException($"The {settings.Encoder} encoder is not supported on this operating system.");
            }
            if (OperatingSystem.IsLinux() && settings.Encoder == HardwareVideoEncoder.Qsv &&
                settings.Device.Length > 0 && !settings.Device.StartsWith("/dev/dri/renderD", StringComparison.Ordinal))
            {
                throw new HardwareEncodingException("Linux QSV requires a render-node path such as /dev/dri/renderD128, not a numeric GPU index.");
            }
            await RunProbeAsync(factory, ["-hide_banner", "-encoders"], HardwareVideoEncodingPlanner.GetEncoderName(settings.Encoder), cancellationToken);
            await RunProbeAsync(factory, HardwareVideoEncodingPlanner.CreateProbeArguments(settings), null, cancellationToken);
            logger.LogInformation("Hardware video encoder {Encoder} passed its startup encode check.", settings.Encoder);
            return true;
        }
        catch (HardwareEncodingException exception) when (settings.AllowSoftwareFallback)
        {
            logger.LogWarning(exception, "Requested hardware encoder {Encoder} is unavailable. Explicitly enabled software fallback will be used before publishing media.", settings.Encoder);
            return false;
        }
    }

    private static async Task RunProbeAsync(IMediaProcessFactory factory, IReadOnlyList<string> arguments, string? requiredEncoder, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = factory.Start(new MediaProcessRequest
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        });
        process.StandardInput.Close();
        var error = MediaProcessDiagnostics.ReadTailAsync(process.StandardError);
        var output = ContainsEncoderAsync(process.StandardOutput, requiredEncoder);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            // Cleanup failure must propagate, not be mistaken for permission to start a fallback.
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await MediaProcessAdmission.WaitForReleaseAsync(process).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(error, output).WaitAsync(TimeSpan.FromSeconds(10));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostic = await error;
        var found = await output;
        if (timedOut || process.ExitCode != 0 || !found)
        {
            throw new HardwareEncodingException(
                $"Hardware encoder startup verification failed{(timedOut ? " (timeout)" : $" (exit {process.ExitCode})")}. " +
                $"Check FFmpeg encoder support, GPU drivers/runtime, device permissions and container GPU mapping. {diagnostic}");
        }
    }

    private static async Task<bool> ContainsEncoderAsync(TextReader output, string? requiredEncoder)
    {
        var found = requiredEncoder is null;
        while (await output.ReadLineAsync() is { } line)
        {
            if (requiredEncoder is not null && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requiredEncoder, StringComparer.Ordinal))
            {
                found = true;
            }
        }
        return found;
    }
}

/// <summary>Reports a hardware encoder initialization failure after owned probe resources have been released.</summary>
public sealed class HardwareEncodingException(string message) : Exception(message);

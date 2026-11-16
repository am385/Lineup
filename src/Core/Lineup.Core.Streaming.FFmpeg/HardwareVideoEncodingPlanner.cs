using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Applies backend-specific H.264 encoding to an existing software-decoded media plan.</summary>
public static class HardwareVideoEncodingPlanner
{
    /// <summary>Reads the primary output video encoder, copy, or none from the arguments supplied to FFmpeg.</summary>
    public static string GetVideoEncoder(IReadOnlyList<string> arguments)
    {
        var encoder = "none";
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] is "-c" or "-c:v" or "-c:v:0")
            {
                encoder = arguments[index + 1];
            }
        }
        return encoder;
    }

    /// <summary>Gets the FFmpeg encoder name for a configured backend.</summary>
    public static string GetEncoderName(HardwareVideoEncoder encoder) => encoder switch
    {
        HardwareVideoEncoder.Software => "libx264",
        HardwareVideoEncoder.Qsv => "h264_qsv",
        HardwareVideoEncoder.Nvenc => "h264_nvenc",
        HardwareVideoEncoder.Vaapi => "h264_vaapi",
        HardwareVideoEncoder.Amf => "h264_amf",
        HardwareVideoEncoder.VideoToolbox => "h264_videotoolbox",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder))
    };

    /// <summary>Replaces software encoder options only when the existing plan already encodes video.</summary>
    public static IReadOnlyList<string> Apply(
        IReadOnlyList<string> softwareArguments,
        HardwareEncodingSettings settings,
        bool requiresClosedCaptions = false,
        bool preserveMinimumKeyframeInterval = false,
        MediaFrameRate? closedCaptionFrameRate = null
    )
    {
        var errors = HardwareEncodingValidation.Validate(settings);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }
        if (settings.Encoder == HardwareVideoEncoder.Software || !softwareArguments.Contains("libx264"))
        {
            return softwareArguments;
        }
        var repackCaptions = requiresClosedCaptions && settings.Encoder is HardwareVideoEncoder.Qsv or HardwareVideoEncoder.Vaapi;
        if (repackCaptions && !CanRepackCaptions(closedCaptionFrameRate))
        {
            throw new NotSupportedException($"Closed-caption normalization for {settings.Encoder} requires a supported output frame rate. Select Software or NVENC for this source.");
        }
        if (requiresClosedCaptions && !repackCaptions && settings.Encoder != HardwareVideoEncoder.Nvenc)
        {
            throw new NotSupportedException($"Closed-caption preservation has not been verified for {settings.Encoder}. Select Software or NVENC for this source.");
        }

        var arguments = softwareArguments.ToList();
        var codecIndex = arguments.IndexOf("libx264");
        var streamSuffix = arguments[codecIndex - 1] == "-c:v" ? ":v:0" :
            arguments[codecIndex - 1].StartsWith("-c:v:", StringComparison.Ordinal) ? arguments[codecIndex - 1][2..] : ":v:0";
        arguments[codecIndex] = GetEncoderName(settings.Encoder);
        for (var index = arguments.Count - 2; index >= 0; index--)
        {
            var name = arguments[index].Split(':')[0];
            if (name is "-preset" or "-tune" or "-crf" or "-x264-params" or "-sc_threshold" ||
                name == "-keyint_min" && !preserveMinimumKeyframeInterval ||
                name == "-a53cc" && settings.Encoder != HardwareVideoEncoder.Nvenc ||
                name == "-pix_fmt" && settings.Encoder == HardwareVideoEncoder.Vaapi)
            {
                arguments.RemoveRange(index, 2);
            }
        }
        var insertion = arguments.IndexOf(GetEncoderName(settings.Encoder)) + 1;
        var options = CreateEncoderOptions(settings, streamSuffix);
        if (repackCaptions && settings.Encoder == HardwareVideoEncoder.Qsv)
        {
            var depthOption = $"-async_depth{streamSuffix}";
            var depth = options.IndexOf(depthOption);
            if (depth >= 0 && options[depth + 1] != "1")
            {
                throw new NotSupportedException("Caption-preserving QSV encoding requires async_depth=1. Remove the conflicting advanced option or select Software or NVENC.");
            }
            if (depth < 0)
            {
                // Multiple in-flight caption payloads crash the bundled Intel runtime even after repacking.
                options.AddRange([depthOption, "1"]);
            }
        }
        arguments.InsertRange(insertion, options);
        var maximumRate = arguments.FindIndex(argument => argument.Split(':')[0] == "-maxrate");
        if (maximumRate >= 0)
        {
            arguments.InsertRange(insertion, [$"-b{streamSuffix}", arguments[maximumRate + 1]]);
        }
        var pixelFormat = arguments.IndexOf($"-pix_fmt{streamSuffix}");
        if (pixelFormat >= 0 && settings.Encoder is HardwareVideoEncoder.Qsv or HardwareVideoEncoder.Amf)
        {
            arguments[pixelFormat + 1] = "nv12";
        }
        var level = arguments.IndexOf($"-level{streamSuffix}");
        if (level >= 0 && arguments[level + 1] == "4.2")
        {
            arguments[level + 1] = "42";
        }
        var input = arguments.IndexOf("-i");
        arguments.InsertRange(input, CreateDeviceArguments(settings));
        if (repackCaptions)
        {
            // Rebuild rate-correct caption packets before Intel's bounded SEI payload handling.
            arguments.InsertRange(arguments.IndexOf(GetEncoderName(settings.Encoder)) + 1, settings.Encoder == HardwareVideoEncoder.Qsv
                ? [$"-a53cc{streamSuffix}", "1"]
                : [$"-sei{streamSuffix}", "+a53_cc"]);
        }
        var filter = settings.Encoder == HardwareVideoEncoder.Vaapi ? "format=nv12,hwupload" : "";
        if (repackCaptions)
        {
            filter = filter.Length == 0 ? "ccrepack" : $"ccrepack,{filter}";
        }
        if (filter.Length > 0)
        {
            AppendVideoFilter(arguments, streamSuffix, filter);
        }
        return arguments;
    }

    /// <summary>Creates a bounded synthetic encode using the same device, tuning, and advanced options as playback.</summary>
    public static IReadOnlyList<string> CreateProbeArguments(HardwareEncodingSettings settings) =>
        Apply(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=128x128:rate=25", "-frames:v", "2",
            "-c:v:0", "libx264", "-pix_fmt:v:0", "yuv420p", "-b:v:0", "1M", "-f", "null", "-"], settings);

    /// <summary>Returns whether the selected backend is meaningful on this operating system.</summary>
    public static bool IsSupportedPlatform(HardwareVideoEncoder encoder) => encoder switch
    {
        HardwareVideoEncoder.Software => true,
        HardwareVideoEncoder.Qsv or HardwareVideoEncoder.Nvenc => OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
        HardwareVideoEncoder.Vaapi => OperatingSystem.IsLinux(),
        HardwareVideoEncoder.Amf => OperatingSystem.IsWindows(),
        HardwareVideoEncoder.VideoToolbox => OperatingSystem.IsMacOS(),
        _ => false
    };

    private static IReadOnlyList<string> CreateDeviceArguments(HardwareEncodingSettings settings)
    {
        var device = settings.Device;
        return settings.Encoder switch
        {
            HardwareVideoEncoder.Vaapi => ["-init_hw_device", $"vaapi=lineup_hw:{(device.Length == 0 ? "/dev/dri/renderD128" : device)}", "-filter_hw_device", "lineup_hw"],
            HardwareVideoEncoder.Qsv when device.StartsWith("/dev/", StringComparison.Ordinal) =>
                ["-init_hw_device", $"vaapi=lineup_va:{device}", "-init_hw_device", "qsv=lineup_hw@lineup_va", "-filter_hw_device", "lineup_hw"],
            HardwareVideoEncoder.Qsv when OperatingSystem.IsLinux() =>
                ["-init_hw_device", "vaapi=lineup_va:/dev/dri/renderD128", "-init_hw_device", "qsv=lineup_hw@lineup_va", "-filter_hw_device", "lineup_hw"],
            HardwareVideoEncoder.Qsv =>
                ["-init_hw_device", $"qsv=lineup_hw:hw,child_device_type=d3d11va{(device.Length == 0 ? "" : $",child_device={device}")}", "-filter_hw_device", "lineup_hw"],
            HardwareVideoEncoder.Amf when device.Length > 0 => ["-init_hw_device", $"d3d11va=lineup_hw:{device}", "-filter_hw_device", "lineup_hw"],
            _ => []
        };
    }

    private static List<string> CreateEncoderOptions(HardwareEncodingSettings settings, string suffix)
    {
        var speed = settings.Tuning == HardwareEncoderTuning.Speed;
        var quality = settings.Tuning == HardwareEncoderTuning.Quality;
        Dictionary<string, string> values = settings.Encoder switch
        {
            HardwareVideoEncoder.Qsv => new() { ["preset"] = speed ? "veryfast" : quality ? "slow" : "medium", ["look_ahead"] = "0" },
            HardwareVideoEncoder.Nvenc => new() { ["preset"] = speed ? "p1" : quality ? "p6" : "p4", ["tune"] = "ll", ["rc"] = "vbr" },
            HardwareVideoEncoder.Vaapi => new() { ["rc_mode"] = "VBR" },
            HardwareVideoEncoder.Amf => new() { ["quality"] = speed ? "speed" : quality ? "quality" : "balanced", ["usage"] = "lowlatency", ["rc"] = "vbr_peak" },
            HardwareVideoEncoder.VideoToolbox => new() { ["realtime"] = quality ? "0" : "1", ["prio_speed"] = speed ? "1" : "0" },
            _ => []
        };
        foreach (var line in settings.AdvancedArguments.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            values[line[..separator]] = line[(separator + 1)..];
        }
        if (settings.Encoder == HardwareVideoEncoder.Nvenc && settings.Device.Length > 0)
        {
            values["gpu"] = settings.Device;
        }
        if (settings.Encoder == HardwareVideoEncoder.VideoToolbox)
        {
            values["allow_sw"] = "0";
        }
        List<string> arguments = [];
        foreach (var pair in values)
        {
            arguments.AddRange([$"-{pair.Key}{suffix}", pair.Value]);
        }
        return arguments;
    }

    private static bool CanRepackCaptions(MediaFrameRate? rate)
    {
        if (rate is not { IsUsable: true })
        {
            return false;
        }
        var numerator = (long)rate.Numerator;
        var denominator = (long)rate.Denominator;
        return numerator == 24 * denominator || numerator == 30 * denominator || numerator == 60 * denominator ||
            numerator * 1001 == 24000 * denominator || numerator * 1001 == 30000 * denominator || numerator * 1001 == 60000 * denominator;
    }

    private static void AppendVideoFilter(List<string> arguments, string suffix, string filter)
    {
        var complex = arguments.IndexOf("-filter_complex");
        if (complex >= 0)
        {
            if (!arguments[complex + 1].EndsWith("[v]", StringComparison.Ordinal))
            {
                throw new NotSupportedException("The hardware encoder requires a contained video filter output.");
            }
            arguments[complex + 1] = $"{arguments[complex + 1][..^3]}[lineup_cpu];[lineup_cpu]{filter}[v]";
            return;
        }
        var existingFilter = arguments.IndexOf($"-filter{suffix}");
        if (existingFilter >= 0)
        {
            arguments[existingFilter + 1] += $",{filter}";
            return;
        }
        var codec = arguments.FindIndex(argument => argument is "h264_vaapi" or "h264_qsv");
        arguments.InsertRange(codec + 1, [$"-filter{suffix}", filter]);
    }
}

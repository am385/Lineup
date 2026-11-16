using Lineup.Core.Common.Streaming;

namespace Lineup.Core.Extensibility.Streaming;

/// <summary>Validates portable hardware encoder configuration without accessing a GPU or loading drivers.</summary>
public static class HardwareEncodingValidation
{
    /// <summary>Returns structural errors independently of the current deployment's GPU availability.</summary>
    public static IReadOnlyList<string> Validate(HardwareEncodingSettings? settings)
    {
        if (settings is null)
        {
            return ["Hardware encoding settings are required."];
        }
        List<string> errors = [];
        if (!Enum.IsDefined(settings.Encoder) || !Enum.IsDefined(settings.Tuning))
        {
            errors.Add("The hardware encoder or tuning is invalid.");
        }
        if (settings.Encoder == HardwareVideoEncoder.Vaapi && settings.Tuning != HardwareEncoderTuning.Balanced)
        {
            errors.Add("VA-API preset scales are driver-specific. Select Balanced for the driver default, or use an advanced compression_level supported by your driver.");
        }
        if (settings.Device is null || settings.Device.Length > 256 || settings.Device.Any(char.IsControl))
        {
            errors.Add("The hardware device must contain at most 256 characters without control characters.");
        }
        else if (settings.Device.Length > 0)
        {
            var valid = settings.Encoder switch
            {
                HardwareVideoEncoder.Qsv => IsRenderNode(settings.Device) || IsIndex(settings.Device),
                HardwareVideoEncoder.Vaapi => IsRenderNode(settings.Device),
                HardwareVideoEncoder.Nvenc or HardwareVideoEncoder.Amf => IsIndex(settings.Device),
                _ => false
            };
            if (!valid)
            {
                errors.Add("Use a DRI render-node path for VA-API/Linux QSV, a numeric device index for NVENC/Windows QSV/AMF, or no device for Software/VideoToolbox.");
            }
        }
        if (settings.AdvancedArguments is null || settings.AdvancedArguments.Length > 4096 || settings.AdvancedArguments.Contains('\0'))
        {
            errors.Add("Advanced encoder arguments must contain at most 4096 characters without NUL characters.");
            return errors;
        }
        var lines = settings.AdvancedArguments.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 16)
        {
            errors.Add("At most sixteen advanced encoder options are allowed.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                errors.Add("Advanced encoder options must use one option=value pair per line.");
                continue;
            }
            var name = line[..separator];
            var value = line[(separator + 1)..];
            var allowed = settings.Encoder switch
            {
                HardwareVideoEncoder.Qsv => name is "preset" or "async_depth" or "look_ahead" or "global_quality" or "low_power",
                HardwareVideoEncoder.Nvenc => name is "preset" or "tune" or "rc" or "cq" or "spatial-aq" or "temporal-aq" or "rc-lookahead" or "surfaces",
                HardwareVideoEncoder.Vaapi => name is "rc_mode" or "qp" or "quality" or "compression_level" or "low_power",
                HardwareVideoEncoder.Amf => name is "quality" or "usage" or "rc" or "qp_i" or "qp_p" or "preanalysis",
                HardwareVideoEncoder.VideoToolbox => name is "realtime" or "prio_speed" or "power_efficient",
                _ => false
            };
            if (!allowed || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_') || !names.Add(name))
            {
                errors.Add($"Advanced encoder option '{name}' is unsupported, duplicated, or has an invalid value.");
            }
        }
        return errors;
    }

    private static bool IsIndex(string value) => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index is >= 0 and <= 255;

    private static bool IsRenderNode(string value) => value.StartsWith("/dev/dri/renderD", StringComparison.Ordinal) && value.Length > 16 && value[16..].All(char.IsAsciiDigit);
}

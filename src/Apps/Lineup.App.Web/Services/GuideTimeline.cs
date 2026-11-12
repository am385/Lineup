namespace Lineup.App.Web.Services;

/// <summary>Resolves guide date anchors and formats local labels without losing repeated-hour identity.</summary>
internal static class GuideTimeline
{
    /// <summary>Resolves a local anchor to its first occurrence, advancing past nonexistent local times.</summary>
    internal static DateTimeOffset ResolveLocalTime(DateTime localTime, TimeZoneInfo timeZone)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }
        var offset = timeZone.IsAmbiguousTime(localTime)
            ? timeZone.GetAmbiguousTimeOffsets(localTime).Max()
            : timeZone.GetUtcOffset(localTime);
        return new DateTimeOffset(localTime, offset).ToUniversalTime();
    }

    /// <summary>Formats a guide instant with its UTC offset when the local time occurs twice.</summary>
    internal static string FormatTime(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, timeZone);
        return timeZone.IsAmbiguousTime(local.DateTime)
            ? $"{local:h:mm tt} (UTC{local:zzz})"
            : $"{local:h:mm tt}";
    }
}

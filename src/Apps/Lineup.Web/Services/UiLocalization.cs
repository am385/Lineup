using System.Globalization;

namespace Lineup.Web.Services;

/// <summary>Defines and applies supported Web UI cultures without changing data-formatting culture.</summary>
public static class UiLocalization
{
    /// <summary>Gets the fallback Web UI culture.</summary>
    public const string DefaultCulture = "en";

    /// <summary>Gets the neutral Spanish Web UI culture.</summary>
    public const string SpanishCulture = "es";

    /// <summary>Gets the supported Web UI cultures.</summary>
    public static IReadOnlyList<string> SupportedCultures { get; } = [DefaultCulture, SpanishCulture];

    /// <summary>Normalizes a configured culture to one supported by this release.</summary>
    public static string Normalize(string? culture) =>
        SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)
            ? SupportedCultures.Single(item => string.Equals(item, culture, StringComparison.OrdinalIgnoreCase))
            : DefaultCulture;

    /// <summary>Applies the configured UI culture for the current request while preserving formatting culture.</summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var configuredCulture = context.RequestServices.GetRequiredService<IWebSettingsService>().Settings.UiCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Normalize(configuredCulture));
        try
        {
            await next(context);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}

using System.Globalization;

namespace Lineup.Web.Services;

/// <summary>Defines and applies supported Web UI cultures without changing data-formatting culture.</summary>
public static class UiLocalization
{
    /// <summary>Gets the fallback Web UI culture.</summary>
    public const string DefaultCulture = "en";

    /// <summary>Gets the neutral Spanish Web UI culture.</summary>
    public const string SpanishCulture = "es";

    /// <summary>Gets the neutral German Web UI culture.</summary>
    public const string GermanCulture = "de";

    /// <summary>Gets the neutral French Web UI culture.</summary>
    public const string FrenchCulture = "fr";

    /// <summary>Gets the Brazilian Portuguese Web UI culture.</summary>
    public const string BrazilianPortugueseCulture = "pt-BR";

    /// <summary>Gets the neutral Italian Web UI culture.</summary>
    public const string ItalianCulture = "it";

    /// <summary>Gets the neutral Dutch Web UI culture.</summary>
    public const string DutchCulture = "nl";

    /// <summary>Gets the neutral Polish Web UI culture.</summary>
    public const string PolishCulture = "pl";

    /// <summary>Gets the Simplified Chinese Web UI culture.</summary>
    public const string SimplifiedChineseCulture = "zh-Hans";

    /// <summary>Gets the neutral Japanese Web UI culture.</summary>
    public const string JapaneseCulture = "ja";

    /// <summary>Gets the supported Web UI cultures.</summary>
    public static IReadOnlyList<string> SupportedCultures { get; } =
    [
        DefaultCulture,
        SpanishCulture,
        GermanCulture,
        FrenchCulture,
        BrazilianPortugueseCulture,
        ItalianCulture,
        DutchCulture,
        PolishCulture,
        SimplifiedChineseCulture,
        JapaneseCulture
    ];

    /// <summary>Normalizes a configured culture to one supported by this release.</summary>
    public static string Normalize(string? culture) =>
        SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)
            ? SupportedCultures.Single(item => string.Equals(item, culture, StringComparison.OrdinalIgnoreCase))
            : DefaultCulture;

    /// <summary>Gets the culture's native display name for the language selector.</summary>
    public static string GetNativeName(string culture) => CultureInfo.GetCultureInfo(Normalize(culture)).NativeName;

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

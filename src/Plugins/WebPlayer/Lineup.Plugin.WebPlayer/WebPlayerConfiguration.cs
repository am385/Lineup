using System.Text.Json;
using System.Text.Json.Serialization;
using Lineup.Core.Common.Streaming;

namespace Lineup.Plugin.WebPlayer;

/// <summary>Configures shared Web Player behavior independently of viewer track selections.</summary>
public sealed record WebPlayerConfiguration
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Gets whether detected embedded captions are extracted and advertised to all Web Player viewers.</summary>
    public bool ExtractEmbeddedCaptions { get; init; }

    /// <summary>Reads validated adapter settings, leaving caption extraction disabled unless explicitly enabled.</summary>
    public static WebPlayerConfiguration Read(AdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(configuration.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase) ||
            configuration.Configuration.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Web Player configuration must be a JSON object for the web-player adapter.", nameof(configuration));
        }
        return configuration.Configuration.Deserialize<WebPlayerConfiguration>(_serializerOptions)
            ?? throw new ArgumentException("Web Player configuration is required.", nameof(configuration));
    }
}

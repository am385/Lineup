namespace Lineup.Core;

/// <summary>
/// Shared application-wide constants and default values.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Default SQLite database filename for EPG cache.
    /// </summary>
    public const string DefaultDatabaseFileName = "lineup_cache.db";

    /// <summary>
    /// Default HDHomeRun device hostname used when no address is configured.
    /// </summary>
    public const string DefaultDeviceAddress = "hdhomerun.local";

    /// <summary>
    /// Default output filename for the generated XMLTV file.
    /// </summary>
    public const string DefaultXmltvFileName = "epg.xml";

    /// <summary>
    /// Filename for persisted application settings.
    /// </summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Configuration key for the application data path.
    /// </summary>
    public const string AppDataPathConfigKey = "Lineup:AppDataPath";

    /// <summary>
    /// Environment variable name for the device address.
    /// </summary>
    public const string DeviceAddressEnvVar = "Lineup__DeviceAddress";

    /// <summary>
    /// Environment variable name for the database path.
    /// </summary>
    public const string DatabasePathEnvVar = "Lineup__DatabasePath";

    /// <summary>
    /// XMLTV episode numbering system identifier.
    /// </summary>
    public const string XmltvNsSystem = "xmltv_ns";
}

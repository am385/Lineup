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
    /// Default directory path for persistent application data.
    /// </summary>
    public const string DefaultAppDataPath = "/appdata";

    /// <summary>
    /// Default filename for the generated XMLTV file.
    /// </summary>
    public const string DefaultXmltvFileName = "epg.xml";

    /// <summary>
    /// Default directory path for generated guide outputs.
    /// </summary>
    public const string DefaultGuideOutputDirectoryPath = "/guide";

    /// <summary>
    /// Default directory path for transient stream artifacts.
    /// </summary>
    public const string DefaultTransientPath = "/transient";

    /// <summary>
    /// Filename for persisted application settings.
    /// </summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Filename used to persist a restart-safe factory-reset request.
    /// </summary>
    public const string FactoryResetRequestFileName = ".lineup-factory-reset.json";

    /// <summary>
    /// Application-owned directory for rolling log files.
    /// </summary>
    public const string LogDirectoryName = "logs";

    /// <summary>
    /// Application-owned directory for persistent ASP.NET Core Data Protection keys.
    /// </summary>
    public const string DataProtectionKeysDirectoryName = "data-protection-keys";

    /// <summary>
    /// Optional configuration key overriding the persistent application-data path.
    /// </summary>
    public const string AppDataPathConfigKey = "Lineup:AppDataPath";

    /// <summary>
    /// Optional configuration key overriding the TUI's independent application-data path.
    /// </summary>
    public const string TuiAppDataPathConfigKey = "Lineup:TuiAppDataPath";

    /// <summary>
    /// Configuration key for the default guide-output directory.
    /// </summary>
    public const string GuideOutputDirConfigKey = "Lineup:GuideOutputDir";

    /// <summary>
    /// Optional configuration key overriding the transient stream-artifact path.
    /// </summary>
    public const string TransientPathConfigKey = "Lineup:TransientPath";

    /// <summary>
    /// Optional configuration key overriding the trusted virtual-output worker package directory.
    /// </summary>
    public const string VirtualOutputWorkerPathConfigKey = "Lineup:VirtualOutputs:WorkerPath";

    /// <summary>
    /// Environment variable name for the TUI database path.
    /// </summary>
    public const string DatabasePathEnvVar = "Lineup__DatabasePath";

    /// <summary>
    /// Configuration key for the HTTP port.
    /// </summary>
    public const string HttpPortConfigKey = "Lineup:HttpPort";

    /// <summary>
    /// Configuration key for the HTTPS port.
    /// </summary>
    public const string HttpsPortConfigKey = "Lineup:HttpsPort";

    /// <summary>
    /// Configuration key for the optional HTTPS PFX path.
    /// </summary>
    public const string HttpsCertificatePathConfigKey = "Lineup:Https:CertificatePath";

    /// <summary>
    /// Configuration key for the optional HTTPS PFX password.
    /// </summary>
    public const string HttpsCertificatePasswordConfigKey = "Lineup:Https:CertificatePassword";

    /// <summary>
    /// Default HTTP port.
    /// </summary>
    public const int DefaultHttpPort = 8080;

    /// <summary>
    /// Default HTTPS port.
    /// </summary>
    public const int DefaultHttpsPort = 8443;

    /// <summary>
    /// XMLTV episode numbering system identifier.
    /// </summary>
    public const string XmltvNsSystem = "xmltv_ns";
}

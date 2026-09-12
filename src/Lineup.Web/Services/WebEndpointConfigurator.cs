using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lineup.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Describes optional HTTPS endpoint availability.
/// </summary>
internal enum HttpsEndpointStatus
{
    /// <summary>
    /// No HTTPS certificate was configured.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// HTTPS is enabled with a valid certificate.
    /// </summary>
    Enabled,

    /// <summary>
    /// HTTPS was configured but could not be enabled.
    /// </summary>
    Unavailable
}

/// <summary>
/// Holds validated HTTP and optional HTTPS endpoint configuration.
/// </summary>
internal sealed class WebEndpointConfiguration : IDisposable
{
    /// <summary>
    /// Initializes validated endpoint configuration.
    /// </summary>
    internal WebEndpointConfiguration(int httpPort, int httpsPort, HttpsEndpointStatus httpsStatus, X509Certificate2? httpsCertificate, string? warning)
    {
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        HttpsStatus = httpsStatus;
        HttpsCertificate = httpsCertificate;
        Warning = warning;
    }

    /// <summary>
    /// HTTP listener port.
    /// </summary>
    internal int HttpPort { get; }

    /// <summary>
    /// HTTPS listener port.
    /// </summary>
    internal int HttpsPort { get; }

    /// <summary>
    /// Validated HTTPS availability.
    /// </summary>
    internal HttpsEndpointStatus HttpsStatus { get; }

    /// <summary>
    /// Loaded HTTPS certificate, when available.
    /// </summary>
    internal X509Certificate2? HttpsCertificate { get; }

    /// <summary>
    /// Non-fatal HTTPS configuration warning.
    /// </summary>
    internal string? Warning { get; }

    /// <inheritdoc />
    public void Dispose() => HttpsCertificate?.Dispose();
}

/// <summary>
/// Loads, validates, and applies Lineup web listener configuration.
/// </summary>
internal static class WebEndpointConfigurator
{
    /// <summary>
    /// Loads and validates endpoint configuration without making optional HTTPS failures fatal.
    /// </summary>
    internal static WebEndpointConfiguration Load(IConfiguration configuration, DateTimeOffset? utcNow = null)
    {
        var httpPort = configuration.GetValue(AppConstants.HttpPortConfigKey, AppConstants.DefaultHttpPort);
        var httpsPort = configuration.GetValue(AppConstants.HttpsPortConfigKey, AppConstants.DefaultHttpsPort);
        var certificatePath = configuration[AppConstants.HttpsCertificatePathConfigKey];
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            return new WebEndpointConfiguration(httpPort, httpsPort, HttpsEndpointStatus.NotConfigured, null, null);
        }

        if (!File.Exists(certificatePath))
        {
            return Unavailable(httpPort, httpsPort, "configured certificate file was not found");
        }

        X509Certificate2 certificate;
        try
        {
            var password = configuration[AppConstants.HttpsCertificatePasswordConfigKey];
            var keyStorageFlags = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.Exportable
                : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password, keyStorageFlags);
        }
        catch (CryptographicException)
        {
            return Unavailable(httpPort, httpsPort, "configured certificate or password is invalid");
        }
        catch (IOException)
        {
            return Unavailable(httpPort, httpsPort, "configured certificate could not be read");
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(httpPort, httpsPort, "configured certificate is not accessible");
        }
        catch (ArgumentException)
        {
            return Unavailable(httpPort, httpsPort, "configured certificate is invalid");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            return Unavailable(httpPort, httpsPort, "configured certificate does not contain a private key");
        }

        var now = (utcNow ?? DateTimeOffset.UtcNow).UtcDateTime;
        if (now < certificate.NotBefore.ToUniversalTime())
        {
            certificate.Dispose();
            return Unavailable(httpPort, httpsPort, "configured certificate is not valid yet");
        }

        if (now > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            return Unavailable(httpPort, httpsPort, "configured certificate has expired");
        }

        return new WebEndpointConfiguration(httpPort, httpsPort, HttpsEndpointStatus.Enabled, certificate, null);
    }

    /// <summary>
    /// Applies validated listener configuration to Kestrel.
    /// </summary>
    internal static void Configure(KestrelServerOptions serverOptions, WebEndpointConfiguration configuration)
    {
        serverOptions.ListenAnyIP(configuration.HttpPort);
        if (configuration.HttpsCertificate != null)
        {
            serverOptions.ListenAnyIP(configuration.HttpsPort, listenOptions => listenOptions.UseHttps(configuration.HttpsCertificate));
        }
    }

    private static WebEndpointConfiguration Unavailable(int httpPort, int httpsPort, string warning) =>
        new(httpPort, httpsPort, HttpsEndpointStatus.Unavailable, null, warning);
}

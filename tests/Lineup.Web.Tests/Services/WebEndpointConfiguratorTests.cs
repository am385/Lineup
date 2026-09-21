using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies resilient HTTP and optional HTTPS endpoint configuration.
/// </summary>
public class WebEndpointConfiguratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies absent certificate configuration selects HTTP without a warning.
    /// </summary>
    [Fact]
    public void Load_NoCertificatePath_ConfiguresHttpOnly()
    {
        // Arrange
        var configuration = new ConfigurationManager();
        configuration["Lineup:HttpPort"] = "9080";
        configuration["Lineup:HttpsPort"] = "9443";

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(9080, endpoints.HttpPort);
        Assert.Equal(9443, endpoints.HttpsPort);
        Assert.Equal(HttpsEndpointStatus.NotConfigured, endpoints.HttpsStatus);
        Assert.Null(endpoints.HttpsCertificate);
        Assert.Null(endpoints.Warning);
    }

    /// <summary>
    /// Verifies a valid PFX containing a private key enables HTTPS.
    /// </summary>
    [Fact]
    public void Load_ValidPfx_EnablesHttps()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-https-");
        const string password = "test-password";
        var certificatePath = Path.Combine(root.FullName, "lineup.pfx");
        WritePfx(certificatePath, password, Now.AddDays(-1), Now.AddDays(1), includePrivateKey: true);
        var configuration = CreateCertificateConfiguration(certificatePath, password);

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(HttpsEndpointStatus.Enabled, endpoints.HttpsStatus);
        Assert.NotNull(endpoints.HttpsCertificate);
        Assert.True(endpoints.HttpsCertificate.HasPrivateKey);
        Assert.Null(endpoints.Warning);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a missing configured certificate falls back to HTTP.
    /// </summary>
    [Fact]
    public void Load_MissingCertificate_FallsBackToHttp()
    {
        // Arrange
        var configuration = CreateCertificateConfiguration(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pfx"), "secret");

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(HttpsEndpointStatus.Unavailable, endpoints.HttpsStatus);
        Assert.Null(endpoints.HttpsCertificate);
        Assert.Contains("not found", endpoints.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", endpoints.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an incorrect PFX password falls back without exposing either password.
    /// </summary>
    [Fact]
    public void Load_IncorrectPassword_FallsBackWithoutSecretDisclosure()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-https-");
        const string actualPassword = "actual-password";
        const string suppliedPassword = "wrong-password";
        var certificatePath = Path.Combine(root.FullName, "lineup.pfx");
        WritePfx(certificatePath, actualPassword, Now.AddDays(-1), Now.AddDays(1), includePrivateKey: true);
        var configuration = CreateCertificateConfiguration(certificatePath, suppliedPassword);

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(HttpsEndpointStatus.Unavailable, endpoints.HttpsStatus);
        Assert.Null(endpoints.HttpsCertificate);
        Assert.Contains("invalid", endpoints.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(actualPassword, endpoints.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain(suppliedPassword, endpoints.Warning, StringComparison.Ordinal);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a certificate without a private key cannot enable HTTPS.
    /// </summary>
    [Fact]
    public void Load_CertificateWithoutPrivateKey_FallsBackToHttp()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-https-");
        const string password = "test-password";
        var certificatePath = Path.Combine(root.FullName, "lineup.pfx");
        WritePfx(certificatePath, password, Now.AddDays(-1), Now.AddDays(1), includePrivateKey: false);
        var configuration = CreateCertificateConfiguration(certificatePath, password);

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(HttpsEndpointStatus.Unavailable, endpoints.HttpsStatus);
        Assert.Null(endpoints.HttpsCertificate);
        Assert.Contains("private key", endpoints.Warning, StringComparison.OrdinalIgnoreCase);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies certificates outside their validity period cannot enable HTTPS.
    /// </summary>
    /// <param name="notBeforeOffsetDays">Certificate start offset from the evaluation time.</param>
    /// <param name="notAfterOffsetDays">Certificate end offset from the evaluation time.</param>
    /// <param name="expectedWarning">Expected warning fragment.</param>
    [Theory]
    [InlineData(-3, -1, "expired")]
    [InlineData(1, 3, "not valid yet")]
    public void Load_CertificateOutsideValidityPeriod_FallsBackToHttp(int notBeforeOffsetDays, int notAfterOffsetDays, string expectedWarning)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-https-");
        const string password = "test-password";
        var certificatePath = Path.Combine(root.FullName, "lineup.pfx");
        WritePfx(
            certificatePath,
            password,
            Now.AddDays(notBeforeOffsetDays),
            Now.AddDays(notAfterOffsetDays),
            includePrivateKey: true);
        var configuration = CreateCertificateConfiguration(certificatePath, password);

        // Act
        using var endpoints = WebEndpointConfigurator.Load(configuration, Now);

        // Assert
        Assert.Equal(HttpsEndpointStatus.Unavailable, endpoints.HttpsStatus);
        Assert.Null(endpoints.HttpsCertificate);
        Assert.Contains(expectedWarning, endpoints.Warning, StringComparison.OrdinalIgnoreCase);
        root.Delete(recursive: true);
    }

    private static ConfigurationManager CreateCertificateConfiguration(string path, string password)
    {
        var configuration = new ConfigurationManager();
        configuration[AppConstants.HttpsCertificatePathConfigKey] = path;
        configuration[AppConstants.HttpsCertificatePasswordConfigKey] = password;
        return configuration;
    }

    private static void WritePfx(string path, string password, DateTimeOffset notBefore, DateTimeOffset notAfter, bool includePrivateKey)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Lineup Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificateWithKey = request.CreateSelfSigned(notBefore, notAfter);
        if (includePrivateKey)
        {
            File.WriteAllBytes(path, certificateWithKey.Export(X509ContentType.Pfx, password));
            return;
        }

        using var certificateWithoutKey = X509CertificateLoader.LoadCertificate(certificateWithKey.Export(X509ContentType.Cert));
        File.WriteAllBytes(path, certificateWithoutKey.Export(X509ContentType.Pfx, password));
    }
}

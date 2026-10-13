using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lineup.LineupLink.Physical;

namespace Lineup.LineupLink.Protocol;

/// <summary>Calls one authenticated remote Lineup Link virtual device.</summary>
public sealed class LineupLinkClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Guid _virtualDeviceId;

    /// <summary>Initializes a Lineup Link protocol client.</summary>
    public LineupLinkClient(LineupLinkPhysicalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!LineupLinkConfigurationValidation.TryNormalizeBaseUri(configuration.BaseUrl, configuration.TransportSecurity, out var baseUri))
        {
            throw new ArgumentException("The Lineup Link base URL does not match the selected transport security.", nameof(configuration));
        }
        if (configuration.VirtualDeviceId == Guid.Empty)
        {
            throw new ArgumentException("A remote virtual-device ID is required.", nameof(configuration));
        }
        if (string.IsNullOrWhiteSpace(configuration.AccessToken))
        {
            throw new ArgumentException("A Lineup Link access token is required.", nameof(configuration));
        }
        if (!LineupLinkConfigurationValidation.IsValidCertificateSha256(configuration.CertificateSha256))
        {
            throw new ArgumentException("The certificate fingerprint must contain 64 hexadecimal SHA-256 characters.", nameof(configuration));
        }

        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(configuration.CertificateSha256))
        {
            var expectedPin = LineupLinkConfigurationValidation.NormalizeCertificateSha256(configuration.CertificateSha256);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None && CertificateMatches(certificate, expectedPin);
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AccessToken);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Lineup-Link/1");
        _virtualDeviceId = configuration.VirtualDeviceId;
    }

    /// <summary>Gets and validates the remote manifest.</summary>
    public async Task<LineupLinkManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await GetJsonAsync<LineupLinkManifest>(LineupLinkProtocol.ManifestPath(_virtualDeviceId), cancellationToken);
        ValidateProtocol(manifest.ProtocolVersion);
        if (manifest.VirtualDeviceId != _virtualDeviceId)
        {
            throw new InvalidDataException("The remote Lineup Link manifest returned a different virtual-device identity.");
        }
        return manifest;
    }

    /// <summary>Gets the safe remote Lineup base URI without authorization data.</summary>
    public Uri BaseUri => _httpClient.BaseAddress!;

    /// <summary>Gets and validates the remote lineup.</summary>
    public async Task<LineupLinkLineup> GetLineupAsync(CancellationToken cancellationToken = default)
    {
        var lineup = await GetJsonAsync<LineupLinkLineup>(LineupLinkProtocol.LineupPath(_virtualDeviceId), cancellationToken);
        ValidateProtocol(lineup.ProtocolVersion);
        if (lineup.VirtualDeviceId != _virtualDeviceId)
        {
            throw new InvalidDataException("The remote Lineup Link lineup returned a different virtual-device identity.");
        }
        return lineup;
    }

    /// <summary>Gets and validates the remote guide.</summary>
    public async Task<LineupLinkGuide> GetGuideAsync(CancellationToken cancellationToken = default)
    {
        var guide = await GetJsonAsync<LineupLinkGuide>(LineupLinkProtocol.GuidePath(_virtualDeviceId), cancellationToken);
        ValidateProtocol(guide.ProtocolVersion);
        if (guide.VirtualDeviceId != _virtualDeviceId)
        {
            throw new InvalidDataException("The remote Lineup Link guide returned a different virtual-device identity.");
        }
        return guide;
    }

    /// <summary>Opens one non-buffered remote MPEG-TS stream.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string channelKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, LineupLinkProtocol.StreamPath(_virtualDeviceId, channelKey));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LineupLinkProtocol.MpegTsMediaType));
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowRemoteErrorAsync(response, cancellationToken);
        }
        if (response.Content.Headers.ContentType?.MediaType is not LineupLinkProtocol.MpegTsMediaType)
        {
            response.Dispose();
            throw new InvalidDataException("The remote Lineup Link stream did not return MPEG-TS content.");
        }
        return response;
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    /// <summary>Creates a safe remote stream URI without embedding authorization.</summary>
    public Uri CreateStreamUri(string channelKey) =>
        new(_httpClient.BaseAddress!, LineupLinkProtocol.StreamPath(_virtualDeviceId, channelKey));

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowRemoteErrorAsync(response, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken)
            ?? throw new InvalidDataException("The remote Lineup Link response was empty.");
    }

    private static void ValidateProtocol(int version)
    {
        if (version != LineupLinkProtocol.Version)
        {
            throw new NotSupportedException($"Remote Lineup Link protocol version {version} is not supported.");
        }
    }

    private static async Task ThrowRemoteErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        LineupLinkError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<LineupLinkError>(SerializerOptions, cancellationToken);
        }
        catch (JsonException)
        {
        }

        var status = response.StatusCode;
        response.Dispose();
        var message = error?.Message ?? $"Remote Lineup Link request failed with HTTP {(int)status}.";
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(message);
        }
        throw new HttpRequestException(message, inner: null, status);
    }

    private static bool CertificateMatches(X509Certificate2? certificate, string expectedPin)
    {
        if (certificate is null)
        {
            return false;
        }
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedPin));
    }
}

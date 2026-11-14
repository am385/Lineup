using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lineup.Core.Common.Guides;
using Lineup.Plugin.LineupLink.Physical;

namespace Lineup.Plugin.LineupLink.Protocol;

/// <summary>Calls one authenticated remote Lineup Link virtual device with bounded control requests and non-buffered live streams.</summary>
public sealed class LineupLinkClient : IDisposable
{
    private static readonly TimeSpan _controlRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly Guid _virtualDeviceId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a Lineup Link protocol client.</summary>
    public LineupLinkClient(LineupLinkPhysicalConfiguration configuration)
        : this(configuration, TimeProvider.System)
    {
    }

    /// <summary>Initializes a protocol client using the supplied clock for control-request deadlines.</summary>
    internal LineupLinkClient(LineupLinkPhysicalConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
        var guide = await GetJsonAsync<LineupLinkGuide>(LineupLinkProtocol.GuidePath(_virtualDeviceId), cancellationToken, GuideTransfer.MaximumBytes);
        ValidateProtocol(guide.ProtocolVersion);
        if (guide.VirtualDeviceId != _virtualDeviceId)
        {
            throw new InvalidDataException("The remote Lineup Link guide returned a different virtual-device identity.");
        }
        if (guide.Channels is null || guide.Programs is null || guide.Channels.Any(channel => channel is null) || guide.Programs.Any(program => program is null))
        {
            throw new InvalidDataException("The remote Lineup Link guide contains null collections or entries.");
        }
        GuideTransfer.ValidateCounts(guide.Channels.Count, guide.Programs.Count);
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
            using var failedResponse = response;
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

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken, int maximumBytes = 1024 * 1024)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(_controlRequestTimeout, _timeProvider);
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestLifetime.Token);
            if (!response.IsSuccessStatusCode)
            {
                await ThrowRemoteErrorAsync(response, requestLifetime.Token);
            }
            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new GuideTransferLimitException($"The remote Lineup Link response exceeds the {maximumBytes} byte UTF-8 limit.");
            }
            await using var content = await response.Content.ReadAsStreamAsync(requestLifetime.Token);
            return await GuideTransfer.ReadAsync<T>(content, requestLifetime.Token, maximumBytes: maximumBytes, enforceCollectionLimits: typeof(T) == typeof(LineupLinkGuide));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The Lineup Link control request was canceled.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The remote Lineup Link control request timed out after 30 seconds.", exception);
        }
        catch (JsonException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("The remote Lineup Link response was not valid protocol JSON.", exception);
        }
    }

    private static void ValidateProtocol(int version)
    {
        if (version != LineupLinkProtocol.Version)
        {
            throw new InvalidDataException($"Remote Lineup Link protocol version {version} is not supported.");
        }
    }

    private static async Task ThrowRemoteErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        LineupLinkError? error = null;
        try
        {
            if (response.Content.Headers.ContentLength > 64 * 1024)
            {
                throw new GuideTransferLimitException("The remote Lineup Link error response exceeds 65536 bytes.");
            }
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            error = await GuideTransfer.ReadAsync<LineupLinkError>(content, cancellationToken, maximumBytes: 64 * 1024, enforceCollectionLimits: false);
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
        throw new LineupLinkRemoteException(
            error?.Code ?? "remote_http_error",
            message,
            status,
            status is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout);
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

/// <summary>Represents one structured failure returned by a remote Lineup Link endpoint.</summary>
public sealed class LineupLinkRemoteException : IOException
{
    /// <summary>Initializes one remote Lineup Link failure.</summary>
    public LineupLinkRemoteException(string code, string message, HttpStatusCode statusCode, bool retryable)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        StatusCode = statusCode;
        Retryable = retryable;
    }

    /// <summary>Gets the stable remote failure code.</summary>
    public string Code { get; }

    /// <summary>Gets the remote HTTP status.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets whether retrying the remote source later may succeed.</summary>
    public bool Retryable { get; }
}

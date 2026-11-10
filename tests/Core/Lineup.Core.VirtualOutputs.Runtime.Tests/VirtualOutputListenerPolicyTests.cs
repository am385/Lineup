using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Characterizes shared listener TLS, trusted proxy, network and client-rate behavior over real sockets.</summary>
public sealed class VirtualOutputListenerPolicyTests
{
    /// <summary>Verifies trusted forwarding precedes the existing per-client rate and allowed-network checks.</summary>
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task Listener_ProxyNetworkAndRatePolicies_PreserveOrdering(bool trusted, HttpStatusCode expected)
    {
        // Arrange
        var port = AllocatePort();
        var policy = new VirtualOutputSecurityPolicyDefinition
        {
            Key = "test",
            Name = "Test",
            AllowForwardedHeaders = true,
            TrustedProxies = trusted ? ["127.0.0.1"] : ["192.0.2.1"],
            AllowedNetworks = ["198.51.100.0/24"],
            RequestsPerMinutePerClient = 1
        };
        await using var app = await StartListenerAsync(port, policy);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.20");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        // Act
        using var first = await client.GetAsync("/", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        if (trusted)
        {
            var response = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("198.51.100.20", response);
            Assert.Contains("https", response);
        }
    }

    /// <summary>Verifies TLS remains optional per binding and required client certificates cannot be bypassed.</summary>
    [Theory]
    [InlineData(VirtualOutputClientCertificateMode.None)]
    [InlineData(VirtualOutputClientCertificateMode.Allow)]
    [InlineData(VirtualOutputClientCertificateMode.Require)]
    public async Task Listener_HttpsBinding_PreservesClientCertificateRequirement(VirtualOutputClientCertificateMode mode)
    {
        // Arrange
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var policy = new VirtualOutputSecurityPolicyDefinition
        {
            Key = "test",
            Name = "Test",
            CertificateReference = "loopback-test",
            ClientCertificateMode = mode
        };
        var port = AllocatePort();
        await using var app = await StartListenerAsync(port, policy, certificate.Export(X509ContentType.Pkcs12));
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, received, _, _) => received?.Thumbprint == certificate.Thumbprint
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("https", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        });

        // Assert
        if (mode == VirtualOutputClientCertificateMode.Require)
        {
            Assert.IsType<HttpRequestException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    private static async Task<WebApplication> StartListenerAsync(int port, VirtualOutputSecurityPolicyDefinition security, byte[]? certificate = null)
    {
        var bootstrap = new VirtualOutputWorkerBootstrap
        {
            Endpoint = new VirtualOutputIpcEndpoint { Transport = VirtualOutputIpcTransport.NamedPipe, Address = "unused-listener-policy-test" },
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "Test",
                Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
                Hosting = new VirtualOutputHostingDefinition
                {
                    WorkerPolicyKey = "test",
                    Bindings =
                    [
                        new VirtualOutputBindingDefinition
                        {
                            Port = port,
                            Addresses = ["127.0.0.1"],
                            Transport = certificate is null ? VirtualOutputTransport.Http : VirtualOutputTransport.Https,
                            PublicBaseUri = $"{(certificate is null ? "http" : "https")}://127.0.0.1:{port}/",
                            SecurityPolicyKey = security.Key
                        }
                    ]
                }
            },
            SecurityPolicies = [security],
            CertificateMaterials = certificate is null ? [] : [new VirtualOutputCertificateMaterial { Reference = security.CertificateReference!, Pkcs12 = certificate }],
            WorkerPolicy = new VirtualOutputWorkerPolicyDefinition { Key = "test", Name = "Test" },
            WorkerId = "test",
            Generation = 1,
            BootstrapToken = "unused",
            BootstrapExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var policy = new VirtualOutputListenerPolicy(bootstrap);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        policy.AddServices(builder.Services);
        builder.WebHost.ConfigureKestrel(policy.Configure);
        var app = builder.Build();
        policy.UseForwardedHeaders(app);
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (!policy.Allows(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapGet("/", (HttpContext context) => Results.Text($"{context.Request.Scheme} {context.Connection.RemoteIpAddress}"));
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

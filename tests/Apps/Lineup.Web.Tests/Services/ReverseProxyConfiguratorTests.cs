using System.Net;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Lineup.VirtualOutputs.Runtime;
using Lineup.Web.Controllers;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies strict trusted reverse-proxy request metadata handling.</summary>
public sealed class ReverseProxyConfiguratorTests
{
    /// <summary>Verifies forwarded headers remain disabled when no known proxies are persisted.</summary>
    [Fact]
    public void Load_NoKnownProxies_ReturnsDisabledConfiguration()
    {
        // Arrange
        var settings = new LineupSettings();

        // Act
        var reverseProxy = ReverseProxyConfigurator.Load(settings);

        // Assert
        Assert.False(reverseProxy.Enabled);
        Assert.Equal(ForwardedHeaders.None, reverseProxy.Options.ForwardedHeaders);
    }

    /// <summary>Verifies a trusted proxy can restore the original HTTPS request metadata while preserving Host.</summary>
    [Fact]
    public async Task ForwardedHeaders_TrustedProxy_NormalizesOriginalRequest()
    {
        // Arrange
        var reverseProxy = Load("10.0.0.2");
        var context = CreateContext("10.0.0.2");
        context.Request.Host = new HostString("lineup.example.com");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.25";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example.com";

        // Act
        var observed = await InvokeAsync(reverseProxy, context);

        // Assert
        Assert.True(observed.IsHttps);
        Assert.Equal("https", observed.Scheme);
        Assert.Equal("lineup.example.com", observed.Host);
        Assert.Equal(IPAddress.Parse("203.0.113.25"), observed.RemoteAddress);
    }

    /// <summary>Verifies trusted CIDR configuration accepts proxies within the configured network.</summary>
    [Fact]
    public async Task ForwardedHeaders_TrustedNetwork_NormalizesOriginalRequest()
    {
        // Arrange
        var reverseProxy = Load("172.18.0.0/16");
        var context = CreateContext("172.18.4.8");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.12";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        var observed = await InvokeAsync(reverseProxy, context);

        // Assert
        Assert.True(observed.IsHttps);
        Assert.Equal(IPAddress.Parse("198.51.100.12"), observed.RemoteAddress);
    }

    /// <summary>Verifies an untrusted caller cannot spoof HTTPS or client address metadata.</summary>
    [Fact]
    public async Task ForwardedHeaders_UntrustedProxy_IgnoresSpoofedMetadata()
    {
        // Arrange
        var reverseProxy = Load("10.0.0.2");
        var context = CreateContext("10.0.0.3");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        var observed = await InvokeAsync(reverseProxy, context);

        // Assert
        Assert.False(observed.IsHttps);
        Assert.Equal("http", observed.Scheme);
        Assert.Equal(IPAddress.Parse("10.0.0.3"), observed.RemoteAddress);
    }

    /// <summary>Verifies only the nearest forwarded client hop is accepted from one trusted proxy.</summary>
    [Fact]
    public async Task ForwardedHeaders_ClientSuppliedLeftmostAddress_UsesNearestForwardedHop()
    {
        // Arrange
        var reverseProxy = Load("10.0.0.2");
        var context = CreateContext("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1, 203.0.113.25";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        var observed = await InvokeAsync(reverseProxy, context);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.25"), observed.RemoteAddress);
    }

    /// <summary>Verifies invalid persisted security-boundary values fail explicitly.</summary>
    [Fact]
    public void Load_InvalidKnownProxy_Throws()
    {
        // Arrange
        var settings = new LineupSettings { KnownProxies = ["proxy.internal"] };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => ReverseProxyConfigurator.Load(settings));

        // Assert
        Assert.Contains("proxy.internal", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a remote client forwarded by a trusted local proxy cannot inherit loopback enrollment access.</summary>
    [Fact]
    public async Task ForwardedHeaders_RemoteClientThroughLoopbackProxy_EnrollmentRemainsRestricted()
    {
        // Arrange
        var reverseProxy = Load("127.0.0.1");
        var settings = Substitute.For<ILineupSettingsService>();
        var enrollments = Substitute.For<IVirtualOutputExternalEnrollmentStore>();
        var controller = new VirtualOutputEnrollmentController(settings, enrollments);
        var context = CreateContext("127.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.25";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        ActionResult<VirtualOutputWorkerBootstrap>? result = null;

        // Act
        await InvokeAsync(reverseProxy, context, request =>
        {
            controller.ControllerContext = new ControllerContext { HttpContext = request };
            result = controller.TakeEnrollment(Guid.NewGuid());
        });

        // Assert
        Assert.IsType<NotFoundResult>(Assert.IsType<ActionResult<VirtualOutputWorkerBootstrap>>(result).Result);
        enrollments.DidNotReceive().Take(Arg.Any<VirtualDeviceId>());
    }

    /// <summary>Verifies dedicated listeners use the same trusted-chain behavior without inheriting management settings.</summary>
    [Fact]
    public async Task VirtualOutputForwardedHeaders_PerOutputPolicy_UsesNearestForwardedHop()
    {
        // Arrange
        var policy = new VirtualOutputSecurityPolicyDefinition
        {
            Key = "public",
            Name = "Public",
            AllowForwardedHeaders = true,
            TrustedProxies = ["10.0.0.2"]
        };
        var hosting = new VirtualOutputHostingDefinition
        {
            Bindings =
            [
                new VirtualOutputBindingDefinition
                {
                    Port = 5000,
                    SecurityPolicyKey = policy.Key
                }
            ]
        };
        var context = CreateContext("10.0.0.2");
        context.Connection.LocalPort = 5000;
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1, 203.0.113.25";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        var observed = await InvokeVirtualOutputAsync(hosting, policy, context);

        // Assert
        Assert.True(observed.IsHttps);
        Assert.Equal(IPAddress.Parse("203.0.113.25"), observed.RemoteAddress);
    }

    private static ReverseProxyConfiguration Load(params string[] knownProxies) =>
        ReverseProxyConfigurator.Load(new LineupSettings { KnownProxies = knownProxies });

    private static DefaultHttpContext CreateContext(string remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("internal", 8080);
        return context;
    }

    private static async Task<ObservedRequest> InvokeAsync(ReverseProxyConfiguration reverseProxy, HttpContext context, Action<HttpContext>? endpoint = null)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var application = new ApplicationBuilder(services);
        ObservedRequest? observed = null;
        application.UseForwardedHeaders(reverseProxy.Options);
        application.Run(request =>
        {
            endpoint?.Invoke(request);
            observed = Observe(request);
            return Task.CompletedTask;
        });
        await application.Build()(context);
        return Assert.IsType<ObservedRequest>(observed);
    }

    private static async Task<ObservedRequest> InvokeVirtualOutputAsync(
        VirtualOutputHostingDefinition hosting,
        VirtualOutputSecurityPolicyDefinition policy,
        HttpContext context)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var application = new ApplicationBuilder(services);
        ObservedRequest? observed = null;
        application.UseVirtualOutputForwardedHeaders(
            hosting,
            new Dictionary<string, VirtualOutputSecurityPolicyDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [policy.Key] = policy
            });
        application.Run(request =>
        {
            observed = Observe(request);
            return Task.CompletedTask;
        });
        await application.Build()(context);
        return Assert.IsType<ObservedRequest>(observed);
    }

    private static ObservedRequest Observe(HttpContext request) =>
        new(
            request.Request.IsHttps,
            request.Request.Scheme,
            request.Request.Host.Value ?? string.Empty,
            request.Connection.RemoteIpAddress);

    private sealed record ObservedRequest(bool IsHttps, string Scheme, string Host, IPAddress? RemoteAddress);
}

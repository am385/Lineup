using System.Net;
using System.Text;
using System.Text.Json;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Xunit;

namespace Lineup.LineupLink.Tests;

/// <summary>Verifies structured Lineup Link streaming failures survive the remote protocol boundary.</summary>
public sealed class LineupLinkFailureTests
{
    /// <summary>Verifies a remote source-unavailable response preserves its stable code and retryability.</summary>
    [Fact]
    public async Task OpenStreamAsync_SourceUnavailable_ThrowsStructuredRemoteFailure()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var payload = JsonSerializer.Serialize(
                new LineupLinkError
                {
                    Code = "source_unavailable",
                    Message = "Every equivalent physical source is currently unavailable."
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);
        using var client = new LineupLinkClient(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = $"http://127.0.0.1:{port}/",
            VirtualDeviceId = virtualDeviceId,
            AccessToken = "client-secret",
            TransportSecurity = LineupLinkTransportSecurity.Http
        });

        // Act
        var exception = await Assert.ThrowsAsync<LineupLinkRemoteException>(
            () => client.OpenStreamAsync("2.1", TestContext.Current.CancellationToken));
        await responseTask;

        // Assert
        Assert.Equal("source_unavailable", exception.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.True(exception.Retryable);
        Assert.Equal("Every equivalent physical source is currently unavailable.", exception.Message);
    }

    /// <summary>Verifies a remote no-signal response preserves its distinct stable code and retryability.</summary>
    [Fact]
    public async Task OpenStreamAsync_NoSignal_ThrowsStructuredRemoteFailure()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var payload = JsonSerializer.Serialize(
                new LineupLinkError
                {
                    Code = "source_no_signal",
                    Message = "Every equivalent physical source is currently unable to lock a usable signal."
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);
        using var client = new LineupLinkClient(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = $"http://127.0.0.1:{port}/",
            VirtualDeviceId = virtualDeviceId,
            AccessToken = "client-secret",
            TransportSecurity = LineupLinkTransportSecurity.Http
        });

        // Act
        var exception = await Assert.ThrowsAsync<LineupLinkRemoteException>(
            () => client.OpenStreamAsync("2.1", TestContext.Current.CancellationToken));
        await responseTask;

        // Assert
        Assert.Equal("source_no_signal", exception.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.True(exception.Retryable);
        Assert.Equal("Every equivalent physical source is currently unable to lock a usable signal.", exception.Message);
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

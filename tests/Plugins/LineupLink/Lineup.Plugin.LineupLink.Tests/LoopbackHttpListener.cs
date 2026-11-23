using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Lineup.Plugin.LineupLink.Tests;

/// <summary>Starts owned test listeners without treating a released TCP port as an HTTP prefix reservation.</summary>
internal static class LoopbackHttpListener
{
    private const int MaximumAttempts = 10;

    /// <summary>Returns a listening loopback endpoint, retrying only address conflicts with fresh listener instances.</summary>
    /// <param name="port">The port owned by the successfully started listener.</param>
    /// <param name="selectPort">An optional candidate-port selector for deterministic collision regressions.</param>
    /// <returns>The started listener, which the caller must dispose.</returns>
    internal static HttpListener Start(out int port, Func<int>? selectPort = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var listener = new HttpListener();
            var started = false;
            try
            {
                port = (selectPort ?? GetAvailablePort)();
                if (port is <= 0 or > IPEndPoint.MaxPort)
                {
                    throw new ArgumentOutOfRangeException(nameof(selectPort), port, "A loopback HTTP listener requires a valid nonzero port.");
                }
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                started = true;
                return listener;
            }
            catch (HttpListenerException exception) when (attempt < MaximumAttempts && IsAddressConflict(exception))
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Retrying loopback HTTP listener after address conflict (attempt {attempt}/{MaximumAttempts}): {exception.Message}");
            }
            catch (SocketException exception) when (attempt < MaximumAttempts && exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Retrying loopback HTTP listener after address conflict (attempt {attempt}/{MaximumAttempts}): {exception.Message}");
            }
            finally
            {
                if (!started)
                {
                    listener.Close();
                }
            }
        }
    }

    // HTTP.sys reports native sharing/registration conflicts; the managed listener reports duplicate prefixes as 400.
    private static bool IsAddressConflict(HttpListenerException exception) =>
        OperatingSystem.IsWindows() ? exception.ErrorCode is 32 or 183 or 10048 : exception.ErrorCode == 400;

    private static int GetAvailablePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

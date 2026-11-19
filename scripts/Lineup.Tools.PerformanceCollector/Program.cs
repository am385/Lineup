using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Lineup.Tools.PerformanceCollector;

/// <summary>Exposes explicit local serve/status/capture/stop commands without a general elevated execution surface.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The temporary WPR collector requires Windows.");
            return 1;
        }
        try
        {
            return await RunWindowsAsync(args);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or Win32Exception or UnauthorizedAccessException or
            SecurityException or JsonException or OperationCanceledException or FormatException or OverflowException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync(string[] args)
    {
        if (args.Length < 3 || args[0] is not ("serve" or "status" or "capture" or "stop"))
        {
            throw new ArgumentException("Usage: serve|status|capture|stop --session <guid> [capture: --root <pid> --duration <1-300> --scenario <label> --gpu --dotnet --io --output <directory>].");
        }
        var action = args[0];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        List<int> roots = [];
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--gpu" or "--dotnet" or "--io")
            {
                if (!flags.Add(name) || action != "capture")
                {
                    throw new ArgumentException("Provider flags are unique capture-only options.");
                }
                continue;
            }
            if (name is not ("--session" or "--owner-sid" or "--root" or "--duration" or "--scenario" or "--output") || ++index >= args.Length)
            {
                throw new ArgumentException("Unknown collector option or missing value.");
            }
            if (name != "--session" && !(action == "serve" && name == "--owner-sid") &&
                !(action == "capture" && name is "--root" or "--duration" or "--scenario" or "--output"))
            {
                throw new ArgumentException("Option is not valid for the selected operation.");
            }
            if (name == "--root")
            {
                roots.Add(int.Parse(args[index], System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (!values.TryAdd(name, args[index]))
            {
                throw new ArgumentException("Collector options may not be repeated.");
            }
        }
        if (!values.TryGetValue("--session", out var sessionId))
        {
            throw new ArgumentException("A collector session is required.");
        }
        CollectorPlatform.ValidateSessionId(sessionId);
        if (action == "serve")
        {
            await ServeAsync(sessionId, values.GetValueOrDefault("--owner-sid"));
            return 0;
        }
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Run the collector client unelevated; only the fixed-purpose server needs administrator access.");
            }
        }
        var selected = roots.Select(id =>
        {
            using var process = Process.GetProcessById(id);
            return new CollectorRoot(id, process.StartTime.ToUniversalTime().Ticks);
        }).ToArray();
        var request = new CollectorRequest
        {
            Action = action,
            Roots = selected,
            Scenario = values.GetValueOrDefault("--scenario", "profile"),
            DurationSeconds = int.Parse(values.GetValueOrDefault("--duration", "30"), System.Globalization.CultureInfo.InvariantCulture),
            Gpu = flags.Contains("--gpu"),
            DotNet = flags.Contains("--dotnet"),
            Io = flags.Contains("--io")
        };
        request.Validate();
        var output = values.GetValueOrDefault("--output") ??
            Path.Combine(Environment.CurrentDirectory, "out", "performance", $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{request.Scenario}-{Guid.NewGuid():N}");
        return await CallAsync(sessionId, request, output);
    }

    [SupportedOSPlatform("windows")]
    private static async Task ServeAsync(string sessionId, string? expectedUser)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Start the collector with an explicit Windows UAC approval. No trace was started.");
        }
        var user = identity.User ?? throw new UnauthorizedAccessException("Collector user identity is unavailable.");
        if (expectedUser is null || !string.Equals(user.Value, expectedUser, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Collector must be approved by the same Windows account that started it; alternate administrator credentials are not supported.");
        }
        // Keep the approved tool image immutable while it provides elevated IPC; no repository scripts are executed by this process.
        var locks = Directory.EnumerateFiles(AppContext.BaseDirectory, "Lineup.Tools.PerformanceCollector.*")
            .Select(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)).ToArray();
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var spool = CollectorPlatform.CreateSpool(sessionId, user);
            await new CollectorServer(sessionId, spool, user, new WprRecorder()).RunAsync(shutdown.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            foreach (var handle in locks)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>Performs an authenticated local call and exports artifacts using only the caller's ordinary filesystem privileges.</summary>
    [SupportedOSPlatform("windows")]
    internal static async Task<int> CallAsync(string sessionId, CollectorRequest request, string outputDirectory)
    {
        var spoolRoot = CollectorPlatform.GetSpoolRoot(sessionId);
        var session = JsonSerializer.Deserialize<CollectorSession>(await File.ReadAllTextAsync(Path.Combine(spoolRoot, "session.json")), CollectorWire.Json)
            ?? throw new InvalidDataException("Collector session metadata is missing.");
        if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Collector session metadata does not match.");
        }
        await using var pipe = new NamedPipeClientStream(".", $"Lineup.Performance.{sessionId}", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(connect.Token);
        CollectorPlatform.ValidateServer(pipe, session);
        return await ExchangeAsync(pipe, request, spoolRoot, outputDirectory);
    }

    /// <summary>Exchanges a fixed request, verifies each exported file, then acknowledges only that generated run.</summary>
    [SupportedOSPlatform("windows")]
    internal static async Task<int> ExchangeAsync(Stream pipe, CollectorRequest request, string spoolRoot, string outputDirectory)
    {
        request.Validate();
        using var operation = new CancellationTokenSource(request.Action == "capture" ? TimeSpan.FromSeconds(request.DurationSeconds) + TimeSpan.FromMinutes(33) : TimeSpan.FromSeconds(30));
        await CollectorWire.WriteAsync(pipe, request, operation.Token);
        var reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, operation.Token);
        if (reply.RunId is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(reply, CollectorWire.Json));
            return reply.Success ? 0 : 1;
        }
        if (request.Action != "capture" || !Guid.TryParseExact(reply.RunId, "N", out _) || reply.Artifacts is null || reply.Artifacts.Length > CollectorServer.ArtifactNames.Count)
        {
            throw new InvalidDataException("Collector returned invalid export metadata.");
        }
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new export directory; existing artifacts are not overwritten.");
        }
        Directory.CreateDirectory(outputDirectory);
        var sourceRoot = Path.Combine(spoolRoot, "out", reply.RunId);
        foreach (var artifact in reply.Artifacts)
        {
            if (artifact is null || !CollectorServer.ArtifactNames.Contains(artifact.Name) || artifact.Length < 0 || artifact.Sha256 is null || artifact.Sha256.Length != 64)
            {
                throw new InvalidDataException("Collector returned an invalid artifact name, length or checksum.");
            }
            var source = Path.Combine(sourceRoot, artifact.Name);
            var destination = Path.Combine(outputDirectory, artifact.Name);
            File.Copy(source, destination, overwrite: false);
            await using var stream = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, operation.Token));
            if (stream.Length != artifact.Length || !string.Equals(hash, artifact.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Export checksum failed. Private source artifacts were not acknowledged or deleted.");
            }
        }
        await CollectorWire.WriteAsync(pipe, new CollectorAcknowledgment(reply.RunId), operation.Token);
        var completion = await CollectorWire.ReadAsync<CollectorReply>(pipe, operation.Token);
        if (!completion.Success || completion.State != "exported")
        {
            throw new IOException($"Capture was exported to {outputDirectory}, but collector cleanup requires attention.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { reply.Success, reply.State, reply.Message, OutputDirectory = outputDirectory }, CollectorWire.Json));
        return reply.Success ? 0 : 1;
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Lineup.TestSupport;
using Xunit;

namespace Lineup.Tools.PerformanceCollector.Tests;

/// <summary>Exercises real local IPC and fake trace artifacts without launching WPR or requesting administrator rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class CollectorLifecycleTests
{
    /// <summary>Enforces the actual per-session capture ceiling before invoking the recorder again.</summary>
    [Fact]
    public async Task Server_RejectsTwentyFirstCaptureWithoutRecording()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture();
        for (var index = 0; index < 20; index++)
        {
            await using var pipe = await fixture.ConnectAsync();
            var output = Path.Combine(fixture.TemporaryRoot, $"export-{index}");
            Assert.Equal(0, await Program.ExchangeAsync(pipe, CaptureRequest(), fixture.Spool, output));
        }
        var rejectedOutput = Path.Combine(fixture.TemporaryRoot, "rejected");
        int exitCode;

        // Act
        await using (var pipe = await fixture.ConnectAsync())
        {
            exitCode = await Program.ExchangeAsync(pipe, CaptureRequest(), fixture.Spool, rejectedOutput);
        }
        await fixture.StopAsync();

        // Assert
        Assert.Equal(1, exitCode);
        Assert.Equal(20, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(rejectedOutput));
    }

    /// <summary>Prevents an ordinary creator-owner token from recovering write access to private capture files through implicit WRITE_DAC.</summary>
    [Fact]
    public void SpoolSecurity_RestrictsUserAndCreatorOwnerWhilePreservingAdministratorAccess()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows access descriptors.");
        using var identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);

        // Act
        var security = CollectorPlatform.CreateSpoolSecurity(user);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

        // Assert
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), security.GetOwner(typeof(SecurityIdentifier)));
        var userRule = Assert.Single(rules, rule => rule.IdentityReference == user);
        Assert.Equal((FileSystemRights)0, userRule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete));
        var ownerRule = Assert.Single(rules, rule => rule.IdentityReference.Value == "S-1-3-4");
        Assert.Equal(FileSystemRights.ReadPermissions | FileSystemRights.Synchronize, ownerRule.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, ownerRule.InheritanceFlags);
    }

    /// <summary>Rejects an impersonated peer whose SID does not match the approving account.</summary>
    [Fact]
    public async Task Server_RejectsWrongPeerAccount()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var name = $"Lineup.Peer.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var pending = server.WaitForConnectionAsync(watchdog.Token);
        await client.ConnectAsync(watchdog.Token);
        await pending;

        // Act
        var exception = Record.Exception(() => CollectorPlatform.ValidateClient(server, new SecurityIdentifier(WellKnownSidType.NullSid, null)));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
    }

    /// <summary>Rejects a connected client that never sends its bounded request without letting it hold the elevated process indefinitely.</summary>
    [Fact]
    public async Task Server_ReadDeadlineRejectsSilentClient()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        var clock = new ControlledTimeProvider();
        await using var fixture = new Fixture(clock: clock);
        CollectorReply reply;
        await using (var pipe = await fixture.ConnectAsync())
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(10), fixture.Watchdog.Token);

            // Act
            clock.Advance(TimeSpan.FromSeconds(10));
            reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, fixture.Watchdog.Token);
        }
        await fixture.StopAsync();

        // Assert
        Assert.False(reply.Success);
        Assert.Equal(0, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(fixture.Spool));
    }

    /// <summary>Expires an unused collector using its controlled elapsed-time clock, not a short real sleep.</summary>
    [Fact]
    public async Task Server_IdleExpiryRemovesReadinessAndEmptySpool()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        var clock = new ControlledTimeProvider();
        await using var fixture = new Fixture(clock: clock);
        await clock.WaitForTimerAsync(TimeSpan.FromMinutes(10), fixture.Watchdog.Token);

        // Act
        clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.ServerTask.WaitAsync(fixture.Watchdog.Token);

        // Assert
        Assert.Equal(0, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(fixture.Spool));
    }

    /// <summary>Keeps the total lease bounded even when valid clients repeatedly refresh the idle deadline.</summary>
    [Fact]
    public async Task Server_TotalExpiryCannotBeExtendedByHealthRequests()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        var clock = new ControlledTimeProvider();
        await using var fixture = new Fixture(clock: clock);
        await clock.WaitForTimerAsync(TimeSpan.FromMinutes(10), fixture.Watchdog.Token);
        for (var index = 0; index < 9; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(9));
            await using (var pipe = await fixture.ConnectAsync())
            {
                await Program.ExchangeAsync(pipe, new CollectorRequest(), fixture.Spool, Path.Combine(fixture.TemporaryRoot, "unused"));
            }
            await clock.WaitForTimerAsync(TimeSpan.FromMinutes(10), fixture.Watchdog.Token);
        }

        // Act
        clock.Advance(TimeSpan.FromMinutes(9));
        await fixture.ServerTask.WaitAsync(fixture.Watchdog.Token);

        // Assert
        Assert.Equal(0, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(fixture.Spool));
    }

    /// <summary>Checks sequential connections, account verification, normal export and shutdown cleanup.</summary>
    [Fact]
    public async Task Server_ExportsAcknowledgedCaptureAndStops()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture();
        var output = Path.Combine(fixture.TemporaryRoot, "export");

        // Act
        await using (var status = await fixture.ConnectAsync())
        {
            Assert.Equal(0, await Program.ExchangeAsync(status, new CollectorRequest(), fixture.Spool, output));
        }
        await using (var capture = await fixture.ConnectAsync())
        {
            Assert.Equal(0, await Program.ExchangeAsync(capture, CaptureRequest(), fixture.Spool, output));
        }
        await fixture.StopAsync();

        // Assert
        Assert.Equal("Fake trace: not an ETW recording.", await File.ReadAllTextAsync(Path.Combine(output, "system.etl"), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(fixture.Spool));
    }

    /// <summary>Rejects stale process births before the recorder is invoked, then remains usable.</summary>
    [Fact]
    public async Task Server_RejectsStaleRootWithoutRecording()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture();
        var request = CaptureRequest();
        request = request with { Roots = [request.Roots[0] with { StartedUtcTicks = request.Roots[0].StartedUtcTicks - 1 }] };

        // Act
        CollectorReply reply;
        await using (var pipe = await fixture.ConnectAsync())
        {
            await CollectorWire.WriteAsync(pipe, request, fixture.Watchdog.Token);
            reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, fixture.Watchdog.Token);
        }
        await fixture.StopAsync();

        // Assert
        Assert.False(reply.Success);
        Assert.Equal(0, fixture.Recorder.Captures);
        Assert.False(Directory.Exists(fixture.Spool));
    }

    /// <summary>Preserves a recording after a mismatched acknowledgment rather than deleting a caller-chosen run.</summary>
    [Fact]
    public async Task Server_PreservesUnacknowledgedCapture()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture();
        string? runId;

        // Act
        await using (var pipe = await fixture.ConnectAsync())
        {
            await CollectorWire.WriteAsync(pipe, CaptureRequest(), fixture.Watchdog.Token);
            var reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, fixture.Watchdog.Token);
            runId = reply.RunId;
            await CollectorWire.WriteAsync(pipe, new CollectorAcknowledgment(Guid.NewGuid().ToString("N")), fixture.Watchdog.Token);
        }
        await fixture.StopAsync();

        // Assert
        Assert.NotNull(runId);
        Assert.True(File.Exists(Path.Combine(fixture.Spool, "out", runId, "system.etl")));
        Assert.False(File.Exists(Path.Combine(fixture.Spool, "session.json")));
    }

    /// <summary>Stops accepting work after an unconfirmed cleanup even when the requesting client disconnects.</summary>
    [Fact]
    public async Task Server_FailedCleanupExpiresWithoutAnotherRequest()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture(cleanupSucceeded: false);

        // Act
        string? runId;
        await using (var pipe = await fixture.ConnectAsync())
        {
            await CollectorWire.WriteAsync(pipe, CaptureRequest(), fixture.Watchdog.Token);
            var reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, fixture.Watchdog.Token);
            Assert.False(reply.Success);
            runId = reply.RunId;
        }
        await fixture.ServerTask.WaitAsync(fixture.Watchdog.Token);

        // Assert
        Assert.Equal(1, fixture.Recorder.Captures);
        Assert.NotNull(runId);
        Assert.True(File.Exists(Path.Combine(fixture.Spool, "out", runId, "system.etl")));
        Assert.False(File.Exists(Path.Combine(fixture.Spool, "session.json")));
    }

    /// <summary>Refuses a checksum-mismatched export without acknowledging deletion of the private recording.</summary>
    [Fact]
    public async Task Client_RejectsCorruptExportWithoutAcknowledging()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        var root = Path.Combine(Path.GetTempPath(), $"Lineup.Collector.Corruption.{Guid.NewGuid():N}");
        var runId = Guid.NewGuid().ToString("N");
        var source = Path.Combine(root, "spool", "out", runId);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "system.etl"), "corrupted fake artifact", TestContext.Current.CancellationToken);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipeName = $"Lineup.Collector.Test.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(watchdog.Token);
        await client.ConnectAsync(watchdog.Token);
        await connected;
        var exchange = Program.ExchangeAsync(client, CaptureRequest(), Path.Combine(root, "spool"), Path.Combine(root, "export"));
        try
        {
            await CollectorWire.ReadAsync<CollectorRequest>(server, watchdog.Token);
            var artifact = new CollectorArtifact("system.etl", 1, Convert.ToHexString(SHA256.HashData([1])));

            // Act
            await CollectorWire.WriteAsync(server, new CollectorReply(true, "captured", RunId: runId, Artifacts: [artifact]), watchdog.Token);
            var exception = await Record.ExceptionAsync(() => exchange);

            // Assert
            Assert.IsType<InvalidDataException>(exception);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<EndOfStreamException>(() => CollectorWire.ReadAsync<CollectorAcknowledgment>(server, watchdog.Token));
            Assert.True(File.Exists(Path.Combine(source, "system.etl")));
        }
        finally
        {
            await client.DisposeAsync();
            await ((Task)exchange).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Rejects server PID reuse or endpoint substitution even when the pipe is reachable.</summary>
    [Fact]
    public async Task Client_RejectsWrongPublishedServerLifetime()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires local Windows named pipes.");
        await using var fixture = new Fixture();
        await using var pipe = await fixture.ConnectAsync();
        using var self = Process.GetCurrentProcess();
        var wrong = new CollectorSession(fixture.SessionId, self.Id, self.StartTime.ToUniversalTime().Ticks - 1);

        // Act
        var exception = Record.Exception(() => CollectorPlatform.ValidateServer(pipe, wrong));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
    }

    private static CollectorRequest CaptureRequest()
    {
        using var self = Process.GetCurrentProcess();
        return new CollectorRequest { Action = "capture", Roots = [new(self.Id, self.StartTime.ToUniversalTime().Ticks)] };
    }

    [SupportedOSPlatform("windows")]
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>Gets the unique owned endpoint identifier.</summary>
        internal string SessionId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Gets the precisely scoped temporary test directory.</summary>
        internal string TemporaryRoot { get; } = Path.Combine(Path.GetTempPath(), $"Lineup.Collector.Tests.{Guid.NewGuid():N}");

        /// <summary>Gets the fake private recording root.</summary>
        internal string Spool { get; }

        /// <summary>Gets the fake recorder; this fixture never starts WPR.</summary>
        internal FakeRecorder Recorder { get; }

        /// <summary>Gets the bounded test watchdog, not a lifecycle ordering mechanism.</summary>
        internal CancellationTokenSource Watchdog { get; } = new(TimeSpan.FromSeconds(30));

        /// <summary>Gets the owned server lifetime.</summary>
        internal Task ServerTask { get; }
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Starts a local same-account server with fake recording behavior.</summary>
        internal Fixture(bool cleanupSucceeded = true, TimeProvider? clock = null)
        {
            Spool = Directory.CreateDirectory(Path.Combine(TemporaryRoot, "spool")).FullName;
            Recorder = new FakeRecorder(cleanupSucceeded);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Test identity unavailable.");
            ServerTask = new CollectorServer(SessionId, Spool, user, Recorder, clock).RunAsync(_shutdown.Token);
        }

        /// <summary>Connects and verifies the actual owning server process.</summary>
        internal async Task<NamedPipeClientStream> ConnectAsync()
        {
            var pipe = new NamedPipeClientStream(".", $"Lineup.Performance.{SessionId}", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(Watchdog.Token);
                using var self = Process.GetCurrentProcess();
                CollectorPlatform.ValidateServer(pipe, new CollectorSession(SessionId, self.Id, self.StartTime.ToUniversalTime().Ticks));
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        /// <summary>Requests normal shutdown and observes it before teardown.</summary>
        internal async Task StopAsync()
        {
            await using (var pipe = await ConnectAsync())
            {
                await CollectorWire.WriteAsync(pipe, new CollectorRequest { Action = "stop" }, Watchdog.Token);
                var reply = await CollectorWire.ReadAsync<CollectorReply>(pipe, Watchdog.Token);
                Assert.True(reply.Success);
            }
            await ServerTask.WaitAsync(Watchdog.Token);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                _shutdown.Cancel();
                await ServerTask.WaitAsync(Watchdog.Token);
            }
            finally
            {
                _shutdown.Dispose();
                Watchdog.Dispose();
                Directory.Delete(TemporaryRoot, recursive: true);
            }
        }
    }

    private sealed class FakeRecorder(bool cleanupSucceeded) : ICollectorRecorder
    {
        /// <summary>Gets the number of admitted fake observations.</summary>
        internal int Captures { get; private set; }

        /// <inheritdoc />
        public async Task<CollectorRecording> CaptureAsync(CollectorRequest request, string directory, CancellationToken cancellationToken)
        {
            Captures++;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "system.etl"), "Fake trace: not an ETW recording.", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(new { fake = true }), cancellationToken);
            return new CollectorRecording(cleanupSucceeded, cleanupSucceeded, cleanupSucceeded ? null : "Intentional fake cleanup failure.");
        }
    }
}

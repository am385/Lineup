using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies shared portable producer ownership against the trusted deployment admission service.</summary>
public sealed class MediaResourceTests
{
    /// <summary>Processing is rejected before launch and remains charged after caller disposal until actual OS exit.</summary>
    [Fact]
    public async Task ProcessFactory_Admission_OwnsActualProcessLifetimeAndReleasesFailedLaunch()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProcessingOperations = 1 });
        var factory = new MediaProcessFactory(admission);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
        var process = factory.Start(new MediaProcessRequest { FileName = "dotnet", Arguments = [executable, "hold"], RedirectStandardInput = true });
        using var observer = System.Diagnostics.Process.GetProcessById(process.Id);
        try
        {
            // Act
            process.Dispose();
            var failure = Assert.Throws<MediaResourceLimitException>(() => factory.Start(new MediaProcessRequest { FileName = "lineup-nonexistent-processor", Arguments = [] }));
            var beforeExit = admission.GetDiagnostics().Processing;
            observer.Kill(entireProcessTree: true);
            await observer.WaitForExitAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (admission.GetDiagnostics().Processing != 0) { await Task.Delay(10, timeout.Token); }
            Assert.Throws<MediaProcessException>(() => factory.Start(new MediaProcessRequest { FileName = "lineup-nonexistent-processor", Arguments = [] }));

            // Assert
            Assert.Equal("media_processing_limit", failure.Code);
            Assert.Equal(1, beforeExit);
            Assert.Equal(0, admission.GetDiagnostics().Processing);
        }
        finally
        {
            if (!observer.HasExited) { observer.Kill(entireProcessTree: true); await observer.WaitForExitAsync(CancellationToken.None); }
            process.Dispose();
        }
    }

    /// <summary>Equivalent viewers join at saturation, active viewers defeat reclamation, and only the final viewer makes retention reclaimable.</summary>
    [Fact]
    public async Task AcquireAsync_SaturatedSharedOutput_PreservesIndependentActiveViewers()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        await using var coordinator = new CmafOutputCoordinator(mediaWork: admission);
        var key = new StreamTransformKey("cmaf", "generation-A");
        var disposals = 0;
        var starts = 0;
        ValueTask<CmafOutputRegistration> Start(CancellationToken token)
        {
            starts++;
            var owner = new Owner(() => { disposals++; return Task.CompletedTask; });
            return ValueTask.FromResult(new CmafOutputRegistration(owner, owner));
        }
        var first = await coordinator.AcquireAsync(key, StreamSessionId.New(), Start, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var second = await coordinator.AcquireAsync(key, StreamSessionId.New(), Start, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Act
        await first.DisposeAsync();
        await admission.ReclaimIdleAsync(TestContext.Current.CancellationToken);
        var retained = coordinator.GetDiagnostics();
        await second.DisposeAsync();
        await using var replacement = await coordinator.AcquireAsync(new("cmaf", "generation-B"), StreamSessionId.New(), Start, TimeSpan.Zero, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, Assert.Single(retained).ViewerCount);
        Assert.Equal(2, starts);
        Assert.Equal(1, disposals);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
        Assert.NotEqual(first.OutputId, replacement.OutputId);
    }

    /// <summary>A rejoin racing pressure waits an already-retiring owner instead of over-admitting or spuriously rejecting.</summary>
    [Fact]
    public async Task AcquireAsync_RejoinDuringCleanup_WaitsForTerminalOwnershipRelease()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        await using var coordinator = new CmafOutputCoordinator(mediaWork: admission);
        var key = new StreamTransformKey("cmaf", "same-generation");
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Owner(async () => { disposing.TrySetResult(); await finish.Task; });
        var first = await coordinator.AcquireAsync(key, StreamSessionId.New(), _ => ValueTask.FromResult(new CmafOutputRegistration(owner, owner)),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        await first.DisposeAsync();

        // Act
        var reclaiming = admission.ReclaimIdleAsync(TestContext.Current.CancellationToken);
        await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var rejoining = coordinator.AcquireAsync(key, StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(new Owner(() => Task.CompletedTask), new object())), TimeSpan.Zero, TestContext.Current.CancellationToken).AsTask();
        var premature = rejoining.IsCompleted;
        finish.TrySetResult();
        await reclaiming;
        await using var replacement = await rejoining.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(premature);
        Assert.NotEqual(first.OutputId, replacement.OutputId);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
    }

    /// <summary>Acquisition and presentation producers share the same limit and processing has a separate watermark.</summary>
    [Fact]
    public async Task AcquireAsync_ProcessingPressure_ReclaimsWarmCmafOwner()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProcessingOperations = 1 });
        await using var coordinator = new CmafOutputCoordinator(mediaWork: admission);
        var process = await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken);
        var output = await coordinator.AcquireAsync(new("cmaf", "warm"), StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(process, process)), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        await output.DisposeAsync();

        // Act
        await using var replacement = await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(coordinator.GetDiagnostics());
        Assert.Equal(0, admission.GetDiagnostics().Producers);
        Assert.Equal(1, admission.GetDiagnostics().Processing);
    }

    private sealed class Owner(Func<Task> dispose) : IAsyncDisposable
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync() => new(dispose());
    }
}

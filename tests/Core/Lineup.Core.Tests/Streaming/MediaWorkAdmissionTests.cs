using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies deployment-wide media admission and ownership, independently of logical viewer limits.</summary>
public sealed class MediaWorkAdmissionTests
{
    /// <summary>Verifies admission uses the longest complete mount boundary and enforces the selected volume's exact MiB threshold.</summary>
    /// <param name="workspace">Workspace relative to the test filesystem root.</param>
    /// <param name="mount">The volume that must supply available space.</param>
    /// <param name="trailingSeparator">Whether the configured media mount ends with a separator.</param>
    [Theory]
    [InlineData("media-cache/session", "", false)]
    [InlineData("media/session", "media", false)]
    [InlineData("media", "media", false)]
    [InlineData("media", "media", true)]
    [InlineData("media/nested/session", "media/nested", false)]
    public void CheckWorkspace_MountBoundaries_UsesOnlyMatchingVolume(string workspace, string mount, bool trailingSeparator)
    {
        // Arrange
        var root = Path.Combine(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!, "lineup-mounts");
        var selected = new List<string>();
        const long mib = 1024L * 1024L;
        IEnumerable<(string Root, Func<long> AvailableFreeSpace)> GetVolumes() =>
        [
            (root, () => { selected.Add(""); return 2 * mib; }),
            (Path.Combine(root, "media") + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""), () => { selected.Add("media"); return mib; }),
            (Path.Combine(root, "media", "nested"), () => { selected.Add("media/nested"); return mib; })
        ];
        var path = Path.Combine(root, workspace.Replace('/', Path.DirectorySeparatorChar));

        // Act
        var failure = Record.Exception(() => MediaWorkspaceAdmission.Check(path, 2, GetVolumes));

        // Assert
        Assert.Equal(mount, Assert.Single(selected));
        if (mount.Length == 0)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("media_storage_limit", Assert.IsType<MediaResourceLimitException>(failure).Code);
        }
    }

    /// <summary>Verifies missing or unreadable selected disk data fails explicitly, while a zero watermark does not query disks.</summary>
    [Fact]
    public void CheckWorkspace_DiskDataFailure_IsNotUnlimitedSpace()
    {
        // Arrange
        var path = Path.GetFullPath(Path.GetTempPath());
        var failure = new IOException("Selected volume cannot be read.");
        IEnumerable<(string Root, Func<long> AvailableFreeSpace)> GetUnreadableVolume() => [(path, () => throw failure)];

        // Act
        var unreadable = Record.Exception(() => MediaWorkspaceAdmission.Check(path, 1, GetUnreadableVolume));
        var missing = Record.Exception(() => MediaWorkspaceAdmission.Check(path, 1, () => []));
        var disabled = Record.Exception(() => MediaWorkspaceAdmission.Check(path, 0, () => throw failure));

        // Assert
        Assert.Same(failure, unreadable);
        Assert.IsType<IOException>(missing);
        Assert.Null(disabled);
    }

    /// <summary>Enforces producer and processing counts independently and recovers after idempotent terminal release.</summary>
    [Fact]
    public async Task AcquireAsync_Limits_AreIndependentAndReleasedExactlyOnce()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1, MaximumProcessingOperations = 1 });
        var producer = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);
        await using var processing = await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var producerFailure = await Assert.ThrowsAsync<MediaResourceLimitException>(() => admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var processingFailure = await Assert.ThrowsAsync<MediaResourceLimitException>(() => admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await producer.DisposeAsync();
        await producer.DisposeAsync();
        await using var replacement = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("media_producer_limit", producerFailure.Code);
        Assert.Equal("media_processing_limit", processingFailure.Code);
        Assert.Equal(new MediaWorkDiagnostics(1, 1, 1, 1, 0), admission.GetDiagnostics());
    }

    /// <summary>Reservations remain charged while an idle owner's actual cleanup is incomplete, without serializing unrelated releases.</summary>
    [Fact]
    public async Task AcquireAsync_IdleReclamation_AwaitsCleanupBeforeReadmission()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable? owner = null;
        owner = await admission.AcquireAsync(MediaWorkKind.Producer, async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            await owner!.DisposeAsync();
            return true;
        }, TestContext.Current.CancellationToken);

        // Act
        var opening = admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var whileDraining = admission.GetDiagnostics();
        var premature = opening.IsCompleted;
        finish.TrySetResult();
        await using var replacement = await opening;

        // Assert
        Assert.False(premature);
        Assert.Equal(1, whileDraining.Producers);
        Assert.Equal(1, admission.GetDiagnostics().Producers);
    }

    /// <summary>Concurrent reservations never over-admit and zero limits preserve unlimited deployments.</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(3, 3)]
    public async Task AcquireAsync_ConcurrentPressure_NeverExceedsLimit(int limit, int expected)
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = limit });

        // Act
        var reservations = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            try { return await admission.AcquireAsync(MediaWorkKind.Producer); }
            catch (MediaResourceLimitException) { return null; }
        }));
        var usage = admission.GetDiagnostics();
        foreach (var reservation in reservations.OfType<IAsyncDisposable>())
        {
            await reservation.DisposeAsync();
        }

        // Assert
        Assert.Equal(expected, reservations.Count(reservation => reservation is not null));
        Assert.Equal(expected, usage.Producers);
        Assert.Equal(0, admission.GetDiagnostics().Producers);
    }

    /// <summary>Free-space rejection checks the real workspace volume, including paths not created yet; zero disables checks.</summary>
    [Fact]
    public void CheckWorkspace_InsufficientSpace_ExplicitlyRejectsActualVolume()
    {
        // Arrange
        var workspace = Path.Combine(Path.GetTempPath(), "lineup-workspace-admission", Guid.NewGuid().ToString("N"));
        var admission = new MediaWorkAdmission(new() { MinimumMediaFreeSpaceMib = int.MaxValue });

        // Act
        var failure = Assert.Throws<MediaResourceLimitException>(() => admission.CheckWorkspace(workspace));
        new MediaWorkAdmission(new()).CheckWorkspace(workspace);

        // Assert
        Assert.Equal("media_storage_limit", failure.Code);
        Assert.False(Directory.Exists(workspace));
    }
}

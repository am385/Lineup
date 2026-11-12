using Bunit;
using Lineup.App.Web.Components.Settings;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>Verifies explicit worker recovery uses saved runtime ownership and preserves operator diagnostics.</summary>
public sealed class VirtualOutputWorkerRecoveryPanelTests
{
    /// <summary>Verifies in-flight retry is single-flight, retains the prior failure, and refreshes reported readiness.</summary>
    [Fact]
    public async Task RetryAsync_PendingRecovery_DisablesDuplicatesAndRefreshesReadiness()
    {
        // Arrange
        using var context = new BunitContext();
        var status = new VirtualOutputRuntimeStatus();
        var snapshot = new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = VirtualDeviceId.New(),
            State = VirtualOutputRuntimeState.Failed,
            LastFailure = "Original quarantined package failure."
        };
        status.Set(snapshot);
        var completion = new TaskCompletionSource<VirtualOutputWorkerRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = Substitute.For<IVirtualOutputWorkerRecovery>();
        recovery.RetryAsync(snapshot.VirtualDeviceId, Arg.Any<CancellationToken>()).Returns(completion.Task);
        context.Services.AddSingleton<IVirtualOutputRuntimeStatus>(status);
        context.Services.AddSingleton(recovery);
        var cut = context.Render<VirtualOutputWorkerRecoveryPanel>(parameters => parameters.Add(component => component.VirtualDeviceId, snapshot.VirtualDeviceId));

        // Act
        var click = cut.Find(".retry-worker").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.True(cut.Find(".retry-worker").HasAttribute("disabled")));
        var starting = snapshot with { State = VirtualOutputRuntimeState.Starting, PreviousFailure = snapshot.LastFailure, LastFailure = null };
        status.Set(starting);
        completion.SetResult(new(true, starting));
        await click;
        var previousFailure = cut.Find(".worker-previous-failure").TextContent;
        status.Set(starting with { State = VirtualOutputRuntimeState.Healthy });
        cut.Find(".refresh-worker-status").Click();

        // Assert
        await recovery.Received(1).RetryAsync(snapshot.VirtualDeviceId, Arg.Any<CancellationToken>());
        Assert.Contains("saved settings, not unsaved edits", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(snapshot.LastFailure, previousFailure, StringComparison.Ordinal);
        Assert.Contains("The worker is ready.", cut.Find(".worker-readiness").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".retry-worker"));
        Assert.Empty(cut.FindAll(".worker-failure"));
    }

    /// <summary>Verifies failed revalidation remains visibly unsuccessful and retains both old and new diagnostics.</summary>
    [Fact]
    public async Task RetryAsync_RevalidationFails_ShowsFailureRatherThanSuccess()
    {
        // Arrange
        using var context = new BunitContext();
        var status = new VirtualOutputRuntimeStatus();
        var snapshot = new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = VirtualDeviceId.New(),
            State = VirtualOutputRuntimeState.Failed,
            LastFailure = "Old executable signature failure."
        };
        status.Set(snapshot);
        var recovery = Substitute.For<IVirtualOutputWorkerRecovery>();
        recovery.RetryAsync(snapshot.VirtualDeviceId, Arg.Any<CancellationToken>()).Returns(new VirtualOutputWorkerRecoveryResult(true, snapshot with
        {
            LastFailure = "Replacement executable checksum failed.",
            PreviousFailure = snapshot.LastFailure
        }));
        context.Services.AddSingleton<IVirtualOutputRuntimeStatus>(status);
        context.Services.AddSingleton(recovery);
        var cut = context.Render<VirtualOutputWorkerRecoveryPanel>(parameters => parameters.Add(component => component.VirtualDeviceId, snapshot.VirtualDeviceId));

        // Act
        await cut.Find(".retry-worker").ClickAsync(new());

        // Assert
        Assert.Contains("Replacement executable checksum failed.", cut.Find(".worker-failure").TextContent, StringComparison.Ordinal);
        Assert.Contains(snapshot.LastFailure, cut.Find(".worker-previous-failure").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find(".retry-worker").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".worker-recovery-result"));
        Assert.Empty(cut.FindAll(".worker-readiness"));
    }

    /// <summary>Verifies an output that becomes healthy before acceptance is not retried and exposes the rejection.</summary>
    [Fact]
    public async Task RetryAsync_WorkerBecameHealthy_ShowsRejectionWithoutRetryButton()
    {
        // Arrange
        using var context = new BunitContext();
        var status = new VirtualOutputRuntimeStatus();
        var snapshot = new VirtualOutputRuntimeSnapshot { VirtualDeviceId = VirtualDeviceId.New(), State = VirtualOutputRuntimeState.Failed };
        status.Set(snapshot);
        var recovery = Substitute.For<IVirtualOutputWorkerRecovery>();
        recovery.RetryAsync(snapshot.VirtualDeviceId, Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputWorkerRecoveryResult(false, snapshot with { State = VirtualOutputRuntimeState.Healthy }));
        context.Services.AddSingleton<IVirtualOutputRuntimeStatus>(status);
        context.Services.AddSingleton(recovery);
        var cut = context.Render<VirtualOutputWorkerRecoveryPanel>(parameters => parameters.Add(component => component.VirtualDeviceId, snapshot.VirtualDeviceId));

        // Act
        await cut.Find(".retry-worker").ClickAsync(new());

        // Assert
        Assert.Contains("Running workers are never restarted.", cut.Find(".worker-recovery-result").TextContent, StringComparison.Ordinal);
        Assert.Equal("alert", cut.Find(".worker-recovery-result").GetAttribute("role"));
        Assert.Empty(cut.FindAll(".retry-worker"));
    }

    /// <summary>Verifies navigation cancels a recovery request that has not yet been accepted.</summary>
    [Fact]
    public async Task Dispose_PendingRecovery_CancelsRequest()
    {
        // Arrange
        using var context = new BunitContext();
        var status = new VirtualOutputRuntimeStatus();
        var snapshot = new VirtualOutputRuntimeSnapshot { VirtualDeviceId = VirtualDeviceId.New(), State = VirtualOutputRuntimeState.Failed };
        status.Set(snapshot);
        var recovery = Substitute.For<IVirtualOutputWorkerRecovery>();
        var requestToken = CancellationToken.None;
        recovery.RetryAsync(snapshot.VirtualDeviceId, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            requestToken = call.ArgAt<CancellationToken>(1);
            await Task.Delay(Timeout.InfiniteTimeSpan, requestToken);
            return new VirtualOutputWorkerRecoveryResult(false, null);
        });
        context.Services.AddSingleton<IVirtualOutputRuntimeStatus>(status);
        context.Services.AddSingleton(recovery);
        var cut = context.Render<VirtualOutputWorkerRecoveryPanel>(parameters => parameters.Add(component => component.VirtualDeviceId, snapshot.VirtualDeviceId));
        var click = cut.Find(".retry-worker").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.True(cut.Find(".retry-worker").HasAttribute("disabled")));

        // Act
        await cut.InvokeAsync(cut.Instance.Dispose);
        await click;

        // Assert
        Assert.True(requestToken.IsCancellationRequested);
    }
}

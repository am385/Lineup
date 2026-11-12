using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.AspNetCore.Components;

namespace Lineup.App.Web.Components.Settings;

/// <summary>Provides explicit recovery and retained diagnostics for one saved isolated output.</summary>
public partial class VirtualOutputWorkerRecoveryPanel : IDisposable
{
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>Gets or sets the saved virtual-output identifier.</summary>
    [Parameter, EditorRequired]
    public VirtualDeviceId VirtualDeviceId { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private IVirtualOutputRuntimeStatus? _status;
    private IVirtualOutputWorkerRecovery? _recovery;
    private VirtualOutputRuntimeSnapshot? _snapshot;
    private bool _busy;
    private bool _accepted;
    private bool _rejected;
    private bool _disposed;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _status = Services.GetService<IVirtualOutputRuntimeStatus>();
        _recovery = Services.GetService<IVirtualOutputWorkerRecovery>();
    }

    /// <inheritdoc />
    protected override void OnParametersSet() => Refresh();

    private void Refresh() => _snapshot = _status?.GetSnapshots().SingleOrDefault(snapshot => snapshot.VirtualDeviceId == VirtualDeviceId);

    private async Task RetryAsync()
    {
        if (_busy)
        {
            return;
        }
        var deviceId = VirtualDeviceId;
        _busy = true;
        _accepted = false;
        _rejected = false;
        try
        {
            var result = await _recovery!.RetryAsync(deviceId, _lifetime.Token);
            if (!_disposed && VirtualDeviceId == deviceId)
            {
                _snapshot = result.Snapshot;
                _accepted = result.Accepted;
                _rejected = !result.Accepted;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _busy = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

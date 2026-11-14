using System.Text.Json;
using Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner;

namespace Lineup.App.Web.Tests;

/// <summary>
/// Supplies configurable physical-tuner creation metadata to Web tests.
/// </summary>
internal sealed class TestPhysicalTunerDriver : IPhysicalTunerDriver, IPhysicalDeviceProbe
{
    private readonly string _defaultAddress;

    /// <summary>Gets or sets the probe behavior.</summary>
    public Func<AdapterConfiguration, CancellationToken, ValueTask<PhysicalDeviceProbeResult>> Probe { get; set; } =
        (_, _) => ValueTask.FromResult(new PhysicalDeviceProbeResult
        {
            SuggestedName = "Discovered test device",
            Details = [new PhysicalDeviceProbeDetail { Label = "Device ID", Value = "TEST-DEVICE" }]
        });

    /// <summary>Gets the configuration supplied to the most recent probe.</summary>
    public AdapterConfiguration? ProbedConfiguration { get; private set; }

    /// <summary>Initializes a test physical tuner driver.</summary>
    public TestPhysicalTunerDriver(
        string kind = HDHomeRunPhysicalTunerDriver.DriverKind,
        string displayName = "HDHomeRun",
        string defaultAddress = HDHomeRunPhysicalTunerDriver.DefaultAddress,
        bool supportsUserCreation = true
    )
    {
        Kind = kind;
        DisplayName = displayName;
        _defaultAddress = defaultAddress;
        SupportsUserCreation = supportsUserCreation;
    }

    /// <summary>Gets the kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the supports user creation.</summary>
    public bool SupportsUserCreation { get; }

    /// <summary>Gets the capabilities.</summary>
    public DeviceCapabilities Capabilities { get; } = new();

    /// <summary>Creates default configuration.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <returns>The operation's result.</returns>
    public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
        new(Kind, string.IsNullOrEmpty(_defaultAddress)
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(new { Address = _defaultAddress }));

    /// <summary>Validates the test physical tuner driver.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

    /// <summary>Creates the test physical tuner driver asynchronously.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <param name="name">Name used by the operation.</param>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IPhysicalTunerDevice> CreateAsync(PhysicalDeviceId deviceId, string name, AdapterConfiguration configuration, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IPhysicalTunerDevice>(new NotSupportedException("Web tests do not create physical tuner runtimes."));

    /// <summary>Executes probe asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<PhysicalDeviceProbeResult> ProbeAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ProbedConfiguration = configuration;
        return Probe(configuration, cancellationToken);
    }
}

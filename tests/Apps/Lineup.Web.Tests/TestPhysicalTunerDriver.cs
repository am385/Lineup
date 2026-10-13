using System.Text.Json;
using Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

namespace Lineup.Web.Tests;

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
        bool supportsUserCreation = true)
    {
        Kind = kind;
        DisplayName = displayName;
        _defaultAddress = defaultAddress;
        SupportsUserCreation = supportsUserCreation;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public bool SupportsUserCreation { get; }

    /// <inheritdoc />
    public DeviceCapabilities Capabilities { get; } = new();

    /// <inheritdoc />
    public AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId) =>
        new(Kind, string.IsNullOrEmpty(_defaultAddress)
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(new { Address = _defaultAddress }));

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

    /// <inheritdoc />
    public ValueTask<IPhysicalTunerDevice> CreateAsync(
        PhysicalDeviceId deviceId,
        string name,
        AdapterConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IPhysicalTunerDevice>(new NotSupportedException("Web tests do not create physical tuner runtimes."));

    /// <inheritdoc />
    public ValueTask<PhysicalDeviceProbeResult> ProbeAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ProbedConfiguration = configuration;
        return Probe(configuration, cancellationToken);
    }
}

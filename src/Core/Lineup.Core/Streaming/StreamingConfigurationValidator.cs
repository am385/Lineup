using Lineup.Common.Guides;
using Lineup.Common.Networking;
using Lineup.Extensibility.Guides;
using System.Net;

namespace Lineup.Core.Streaming;

/// <summary>
/// Validates provider-neutral streaming topology before runtime activation.
/// </summary>
public static class StreamingConfigurationValidator
{
    /// <summary>
    /// Returns every configuration error without mutating the supplied configuration.
    /// </summary>
    public static IReadOnlyList<string> Validate(StreamingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        if (configuration.SchemaVersion != StreamingConfiguration.CurrentSchemaVersion)
        {
            errors.Add($"Streaming settings schema {configuration.SchemaVersion} is incompatible with required schema {StreamingConfiguration.CurrentSchemaVersion}.");
        }

        ValidateBitRate(configuration.PhysicalDefaults.MaximumVideoBitRateMbps, "PhysicalDefaults", errors);
        ValidateBitRate(configuration.VirtualDefaults.MaximumVideoBitRateMbps, "VirtualDefaults", errors);
        ValidateQuality(configuration.PhysicalDefaults.VideoQuality, "PhysicalDefaults", errors);
        ValidateQuality(configuration.VirtualDefaults.VideoQuality, "VirtualDefaults", errors);
        if (configuration.KeepWarmPeriodSeconds is < 0 or > 300)
        {
            errors.Add("Keep-warm period must be between 0 and 300 seconds.");
        }
        var physicalIds = new HashSet<PhysicalDeviceId>();
        foreach (var device in configuration.PhysicalDevices)
        {
            if (!physicalIds.Add(device.Id))
            {
                errors.Add($"Physical device ID {device.Id} is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                errors.Add($"Physical device {device.Id} requires a name.");
            }
            if (string.IsNullOrWhiteSpace(device.Adapter.Kind))
            {
                errors.Add($"Physical device {device.Id} requires an adapter kind.");
            }
            if (device.TranscodeOverrides.MaximumVideoBitRateMbps is int bitRate)
            {
                ValidateBitRate(bitRate, $"Physical device {device.Id}", errors);
            }
            if (device.TranscodeOverrides.VideoQuality is int quality)
            {
                ValidateQuality(quality, $"Physical device {device.Id}", errors);
            }
        }

        var guideIds = new HashSet<GuideSourceId>();
        var guideNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guide in configuration.GuideSources)
        {
            if (!guideIds.Add(guide.Id))
            {
                errors.Add($"Guide source ID {guide.Id} is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(guide.Name))
            {
                errors.Add($"Guide source {guide.Id} requires a name.");
            }
            else if (!guideNames.Add(guide.Name.Trim()))
            {
                errors.Add($"Guide source name '{guide.Name.Trim()}' is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(guide.Adapter.Kind))
            {
                errors.Add($"Guide source {guide.Id} requires an adapter kind.");
            }
            if (guide.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected)
            {
                foreach (var selectedId in guide.Subscription.SelectedDeviceIds.Where(id => !physicalIds.Contains(id)))
                {
                    errors.Add($"Guide source {guide.Id} selects unknown physical device {selectedId}.");
                }
            }
        }

        var virtualIds = new HashSet<VirtualDeviceId>();
        ValidateVirtualOutputPolicies(configuration, errors);
        foreach (var device in configuration.VirtualDevices)
        {
            if (!virtualIds.Add(device.Id))
            {
                errors.Add($"Virtual device ID {device.Id} is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                errors.Add($"Virtual device {device.Id} requires a name.");
            }
            if (string.IsNullOrWhiteSpace(device.Adapter.Kind))
            {
                errors.Add($"Virtual device {device.Id} requires an adapter kind.");
            }
            if (device.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected)
            {
                foreach (var selectedId in device.Subscription.SelectedDeviceIds.Where(id => !physicalIds.Contains(id)))
                {
                    errors.Add($"Virtual device {device.Id} selects unknown physical device {selectedId}.");
                }
            }
            if (device.TranscodeOverrides.MaximumVideoBitRateMbps is int bitRate)
            {
                ValidateBitRate(bitRate, $"Virtual device {device.Id}", errors);
            }
            if (device.TranscodeOverrides.VideoQuality is int quality)
            {
                ValidateQuality(quality, $"Virtual device {device.Id}", errors);
            }
            ValidateVirtualOutputHosting(configuration, device, errors);
        }
        ValidateVirtualOutputBindingConflicts(configuration.VirtualDevices, errors);

        var outputIds = new HashSet<GuideOutputId>();
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in configuration.GuideOutputs)
        {
            if (!outputIds.Add(output.Id))
            {
                errors.Add($"Guide output ID {output.Id} is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(output.Name))
            {
                errors.Add($"Guide output {output.Id} requires a name.");
            }
            else if (!outputNames.Add(output.Name.Trim()))
            {
                errors.Add($"Guide output name '{output.Name.Trim()}' is duplicated.");
            }
            if (!GuideOutputKey.IsValid(output.Key))
            {
                errors.Add($"Guide output {output.Id} key '{output.Key}' must start with a lowercase letter and contain only lowercase letters, digits, or underscores.");
            }
            else if (!outputKeys.Add(output.Key))
            {
                errors.Add($"Guide output key '{output.Key}' is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(output.Adapter.Kind))
            {
                errors.Add($"Guide output {output.Id} requires an adapter kind.");
            }
            if (output.VirtualDeviceSelectionMode == GuideOutputSelectionMode.Selected)
            {
                foreach (var selectedId in output.SelectedVirtualDeviceIds.Where(id => !virtualIds.Contains(id)))
                {
                    errors.Add($"Guide output {output.Id} selects unknown virtual device {selectedId}.");
                }
            }
            if (output.GuideSourceSelectionMode == GuideOutputSelectionMode.Selected)
            {
                foreach (var selectedId in output.SelectedGuideSourceIds.Where(id => !guideIds.Contains(id)))
                {
                    errors.Add($"Guide output {output.Id} selects unknown guide source {selectedId}.");
                }
            }
        }

        return errors;
    }

    private static void ValidateBitRate(int bitRate, string owner, List<string> errors)
    {
        if (bitRate is < 1 or > 200)
        {
            errors.Add($"{owner} maximum video bitrate must be between 1 and 200 Mbps.");
        }
    }

    private static void ValidateQuality(int quality, string owner, List<string> errors)
    {
        if (quality is < 16 or > 30)
        {
            errors.Add($"{owner} video quality must be between 16 and 30.");
        }
    }

    private static void ValidateVirtualOutputPolicies(StreamingConfiguration configuration, List<string> errors)
    {
        var securityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in configuration.VirtualOutputSecurityPolicies)
        {
            if (!IsPolicyKeyValid(policy.Key))
            {
                errors.Add($"Virtual output security policy key '{policy.Key}' must start with a lowercase letter and contain only lowercase letters, digits, hyphens, or underscores.");
            }
            else if (!securityKeys.Add(policy.Key))
            {
                errors.Add($"Virtual output security policy key '{policy.Key}' is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(policy.Name))
            {
                errors.Add($"Virtual output security policy '{policy.Key}' requires a name.");
            }
            if (policy.MaximumConnections < 0)
            {
                errors.Add($"Virtual output security policy '{policy.Key}' maximum connections cannot be negative.");
            }
            if (policy.RequestsPerMinutePerClient < 0)
            {
                errors.Add($"Virtual output security policy '{policy.Key}' requests per minute cannot be negative.");
            }
            try
            {
                _ = IpNetworkSet.Parse(policy.AllowedNetworks, $"Virtual output security policy '{policy.Key}' allowed networks");
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
            if (!policy.AllowForwardedHeaders && policy.TrustedProxies.Count > 0)
            {
                errors.Add($"Virtual output security policy '{policy.Key}' cannot configure trusted proxies when forwarded headers are disabled.");
            }
            if (policy.AllowForwardedHeaders && policy.TrustedProxies.Count == 0)
            {
                errors.Add($"Virtual output security policy '{policy.Key}' must configure at least one trusted proxy when forwarded headers are enabled.");
            }
            try
            {
                _ = TrustedProxySet.Parse(policy.TrustedProxies, $"Virtual output security policy '{policy.Key}' trusted proxies");
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        var workerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in configuration.VirtualOutputWorkerPolicies)
        {
            if (!IsPolicyKeyValid(policy.Key))
            {
                errors.Add($"Virtual output worker policy key '{policy.Key}' must start with a lowercase letter and contain only lowercase letters, digits, hyphens, or underscores.");
            }
            else if (!workerKeys.Add(policy.Key))
            {
                errors.Add($"Virtual output worker policy key '{policy.Key}' is duplicated.");
            }
            if (string.IsNullOrWhiteSpace(policy.Name))
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' requires a name.");
            }
            if (policy.MaximumRestarts < 0)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' maximum restarts cannot be negative.");
            }
            if (policy.RestartWindowSeconds is < 1 or > 86_400)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' restart window must be between 1 and 86400 seconds.");
            }
            if (policy.DrainTimeoutSeconds is < 1 or > 300)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' drain timeout must be between 1 and 300 seconds.");
            }
            if (policy.LeaseGracePeriodSeconds is < 1 or > 300)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' lease grace period must be between 1 and 300 seconds.");
            }
            if (policy.MaximumOutstandingBrokerCalls is < 1 or > 4096)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' maximum outstanding broker calls must be between 1 and 4096.");
            }
            if (policy.MaximumMemoryMib < 0)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' maximum memory cannot be negative.");
            }
            if (policy.MaximumCpuPercent is < 0 or > 100)
            {
                errors.Add($"Virtual output worker policy '{policy.Key}' maximum CPU percentage must be between 0 and 100.");
            }
        }
    }

    private static void ValidateVirtualOutputHosting(StreamingConfiguration configuration, VirtualDeviceDefinition device, List<string> errors)
    {
        if (device.Hosting is not { } hosting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(hosting.WorkerPolicyKey) ||
            !configuration.VirtualOutputWorkerPolicies.Any(policy => string.Equals(policy.Key, hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Virtual device {device.Id} references unknown worker policy '{hosting.WorkerPolicyKey}'.");
        }
        if (hosting.Bindings.Count == 0)
        {
            errors.Add($"Virtual device {device.Id} requires at least one output binding.");
        }
        foreach (var duplicatePort in hosting.Bindings
                     .GroupBy(binding => binding.Port)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"Virtual device {device.Id} output bindings must use unique ports; port {duplicatePort} is configured more than once.");
        }

        foreach (var binding in hosting.Bindings)
        {
            if (binding.Port is < 1 or > 65_535)
            {
                errors.Add($"Virtual device {device.Id} output binding port must be between 1 and 65535.");
            }
            if (binding.Addresses.Count == 0)
            {
                errors.Add($"Virtual device {device.Id} output binding on port {binding.Port} requires at least one address.");
            }
            foreach (var address in binding.Addresses)
            {
                if (!IsBindingAddressValid(address))
                {
                    errors.Add($"Virtual device {device.Id} output binding address '{address}' must be an IP address, '*', or 'localhost'.");
                }
            }

            var policy = configuration.VirtualOutputSecurityPolicies.SingleOrDefault(
                candidate => string.Equals(candidate.Key, binding.SecurityPolicyKey, StringComparison.OrdinalIgnoreCase));
            if (policy is null)
            {
                errors.Add($"Virtual device {device.Id} output binding references unknown security policy '{binding.SecurityPolicyKey}'.");
            }
            else
            {
                if (binding.Transport == VirtualOutputTransport.Https && string.IsNullOrWhiteSpace(policy.CertificateReference))
                {
                    errors.Add($"Virtual device {device.Id} HTTPS output binding requires a certificate reference in security policy '{policy.Key}'.");
                }
                if (binding.Transport == VirtualOutputTransport.Http && policy.ClientCertificateMode != VirtualOutputClientCertificateMode.None)
                {
                    errors.Add($"Virtual device {device.Id} HTTP output binding cannot use client certificates from security policy '{policy.Key}'.");
                }
            }

            if (!Uri.TryCreate(binding.PublicBaseUri, UriKind.Absolute, out var publicUri) ||
                (!string.Equals(publicUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(publicUri.Query) ||
                !string.IsNullOrEmpty(publicUri.Fragment))
            {
                errors.Add($"Virtual device {device.Id} output binding public base URI must be an absolute HTTP or HTTPS URI without a query or fragment.");
            }
        }
    }

    private static void ValidateVirtualOutputBindingConflicts(IReadOnlyList<VirtualDeviceDefinition> devices, List<string> errors)
    {
        var bindings = devices
            .Where(device => device.Enabled && device.Hosting is not null)
            .SelectMany(device => device.Hosting!.Bindings.SelectMany(binding =>
                binding.Addresses.Select(address => new OutputBinding(device.Id, NormalizeBindingAddress(address), binding.Port))))
            .ToArray();

        for (var leftIndex = 0; leftIndex < bindings.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < bindings.Length; rightIndex++)
            {
                var left = bindings[leftIndex];
                var right = bindings[rightIndex];
                if (left.Port == right.Port && AddressesOverlap(left.Address, right.Address))
                {
                    errors.Add($"Virtual devices {left.DeviceId} and {right.DeviceId} have conflicting output bindings on port {left.Port} ({left.Address} and {right.Address}).");
                }
            }
        }
    }

    private static bool IsPolicyKeyValid(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] is < 'a' or > 'z')
        {
            return false;
        }
        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    }

    private static bool IsBindingAddressValid(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        (address is "*" or "localhost" || IPAddress.TryParse(address, out _));

    private static string NormalizeBindingAddress(string address)
    {
        var normalized = address.Trim().ToLowerInvariant();
        if (normalized == "localhost")
        {
            return "localhost";
        }
        if (normalized == "*" || normalized == "0.0.0.0" || normalized == "::")
        {
            return "*";
        }
        return IPAddress.TryParse(normalized, out var parsed) ? parsed.ToString() : normalized;
    }

    private static bool AddressesOverlap(string left, string right)
    {
        if (left == "*" || right == "*" || left == right)
        {
            return true;
        }
        return left == "localhost" && (right is "127.0.0.1" or "::1") ||
            right == "localhost" && (left is "127.0.0.1" or "::1");
    }

    private sealed record OutputBinding(VirtualDeviceId DeviceId, string Address, int Port);
}

using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies policy preflight requires real delegated enforcement rather than ordinary writable directories.</summary>
public sealed class ResourcePolicyPreflightTests
{
    /// <summary>Verifies Windows can configure the requested native job limits without launching or attaching a process.</summary>
    [Theory]
    [InlineData(64, 0)]
    [InlineData(0, 25)]
    [InlineData(64, 25)]
    public void Preflight_WindowsRequestedLimits_ConfiguresNativeJob(int memoryMib, int cpuPercent)
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires native Windows job objects.");
        var policy = new VirtualOutputWorkerPolicyDefinition { Key = "limited", Name = "Limited", MaximumMemoryMib = memoryMib, MaximumCpuPercent = cpuPercent };

        // Act
        var error = Record.Exception(() => VirtualOutputProcessLimits.Preflight(policy));

        // Assert
        Assert.Null(error);
    }

    /// <summary>Verifies deployments without delegated controllers fail before launch and leave no probe directories behind.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreflightLinux_MissingEnforcement_RejectsAndCleansUp(bool pretendCgroup)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-cgroup-preflight-");
        try
        {
            if (pretendCgroup)
            {
                File.WriteAllText(Path.Combine(root.FullName, "cgroup.controllers"), "memory cpu");
            }
            var policy = new VirtualOutputWorkerPolicyDefinition { Key = "limited", Name = "Limited", MaximumMemoryMib = 64 };

            // Act
            var failure = Record.Exception(() => VirtualOutputProcessLimits.PreflightLinux(policy, root.FullName));

            // Assert
            Assert.NotNull(failure);
            Assert.Empty(root.EnumerateDirectories());
            if (pretendCgroup)
            {
                Assert.IsAssignableFrom<IOException>(failure);
            }
            else
            {
                Assert.IsType<PlatformNotSupportedException>(failure);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies unlimited opt-in policy fields do not require platform enforcement capabilities.</summary>
    [Fact]
    public void Preflight_NoRequestedLimits_DoesNotRequireHostPrivileges()
    {
        // Arrange
        var policy = new VirtualOutputWorkerPolicyDefinition { Key = "default", Name = "Default" };

        // Act
        var error = Record.Exception(() => VirtualOutputProcessLimits.Preflight(policy));

        // Assert
        Assert.Null(error);
    }
}

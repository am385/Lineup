using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Exercises the production Web host with incompatible persisted settings and no streaming workers.</summary>
public sealed class RecoveryStartupTests
{
    /// <summary>Verifies legacy settings leave recovery reachable without overwriting persisted data or activating normal APIs.</summary>
    /// <param name="settingsFile">The incompatible settings artifact that requires explicit recovery.</param>
    [Theory]
    [InlineData("settings.json")]
    [InlineData("web-settings.json")]
    public async Task LegacySettings_HostStartsAndServesRecoveryWithoutChangingData(string settingsFile)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-web-recovery-");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory);
        var binary = Path.Combine(configuration.Parent!.Parent!.FullName, "Lineup.App.Web", configuration.Name, "Lineup.App.Web.dll");
        var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
        var settingsPath = Path.Combine(data.FullName, settingsFile);
        const string legacySettings = """{"IsSetupComplete":true}""";
        await File.WriteAllTextAsync(settingsPath, legacySettings, deadline.Token);
        var retainedPath = Path.Combine(data.FullName, "retained-data.txt");
        await File.WriteAllTextAsync(retainedPath, "Existing operator data", deadline.Token);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(binary)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { binary, "--urls", "http://127.0.0.1:0" },
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["Lineup__AppDataPath"] = data.FullName,
                ["Lineup__TransientPath"] = Path.Combine(root.FullName, "transient"),
                ["Lineup__GuideOutputDir"] = Path.Combine(root.FullName, "guide"),
                ["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information"
            }
        };
        Process? process = null;
        Task? output = null;
        Task<string>? error = null;
        var startup = new StringBuilder();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Act
            process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned recovery host.");
            var reader = process.StandardOutput;
            error = Task.Factory.StartNew(() => process.StandardError.ReadToEnd(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            output = Task.Factory.StartNew(() =>
            {
                while (reader.ReadLine() is { } line)
                {
                    startup.AppendLine(line);
                    const string prefix = "Now listening on: ";
                    var index = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        listening.TrySetResult(new Uri(line[(index + prefix.Length)..].Trim()));
                    }
                }
                listening.TrySetException(new InvalidOperationException($"The recovery host exited before listening.{Environment.NewLine}{startup}{error.GetAwaiter().GetResult()}"));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var address = await listening.Task.WaitAsync(deadline.Token);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
            using var live = await client.GetAsync("/health/live", deadline.Token);
            using var ready = await client.GetAsync("/health/ready", deadline.Token);
            using var settings = await client.GetAsync("/settings", deadline.Token);
            var page = await settings.Content.ReadAsStringAsync(deadline.Token);
            using var api = await client.GetAsync("/api/v1/status", deadline.Token);
            using var stream = await client.GetAsync("/api/stream", deadline.Token);
            using var dashboard = await client.GetAsync("/", deadline.Token);

            // Assert
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            using var readiness = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(deadline.Token));
            Assert.Equal("factoryResetRequired", readiness.RootElement.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            Assert.Contains("factoryResetRequired", page, StringComparison.Ordinal);
            Assert.Contains("showFactoryResetConfirmation", page, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stream.StatusCode);
            Assert.Equal(HttpStatusCode.Found, dashboard.StatusCode);
            Assert.Equal("/settings", dashboard.Headers.Location?.OriginalString);
            Assert.Equal(legacySettings, await File.ReadAllTextAsync(settingsPath, deadline.Token));
            Assert.Equal("Existing operator data", await File.ReadAllTextAsync(retainedPath, deadline.Token));
            Assert.False(File.Exists(Path.Combine(data.FullName, AppConstants.DefaultDatabaseFileName)));
        }
        finally
        {
            try
            {
                if (process is not null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync(CancellationToken.None);
                    if (output is not null) await output;
                    if (error is not null) await error;
                }
            }
            finally
            {
                process?.Dispose();
                root.Delete(recursive: true);
            }
        }
    }
}

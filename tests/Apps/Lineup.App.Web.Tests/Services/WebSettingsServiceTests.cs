using System.Text.Json;
using Lineup.App.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies Web settings are published only after an atomic persistence operation succeeds.</summary>
public sealed class WebSettingsServiceTests
{
    /// <summary>Verifies a partially applied mutation cannot alter the live settings or their nested collections.</summary>
    [Fact]
    public async Task UpdateAsync_MutationThrows_PreservesPublishedAndPersistedSettings()
    {
        // Arrange
        using var fixture = new Fixture();
        var original = fixture.Service.Settings;
        var persisted = File.ReadAllText(fixture.Path);
        var failure = new InvalidOperationException("The update could not be completed.");
        var notifications = 0;
        fixture.Service.OnSettingsChanged += () => notifications++;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateAsync(settings =>
        {
            settings.Theme = "dark";
            settings.LogCategoryOverrides[0].Level = "Debug";
            settings.LogCategoryOverrides.Add(new WebLogCategoryLevelSetting { Category = "Lineup.Added" });
            throw failure;
        }));

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(original, fixture.Service.Settings);
        Assert.Equal("light", original.Theme);
        Assert.Equal("Warning", Assert.Single(original.LogCategoryOverrides).Level);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        Assert.Equal(0, notifications);
        Assert.False(File.Exists($"{fixture.Path}.bak"));
    }

    /// <summary>Verifies a failed disk write cannot leak changes into memory or a later successful save.</summary>
    [Fact]
    public async Task UpdateAsync_PersistenceFails_SubsequentUpdateExcludesFailedChanges()
    {
        // Arrange
        using var fixture = new Fixture();
        var original = fixture.Service.Settings;
        var persisted = File.ReadAllText(fixture.Path);
        var notifications = 0;
        fixture.Service.OnSettingsChanged += () => notifications++;
        fixture.FailWrites = true;

        // Act
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.UpdateAsync(settings =>
        {
            settings.Theme = "dark";
            settings.RedactApiClientAddresses = false;
            settings.LogCategoryOverrides[0].Level = "Debug";
        }));
        var afterFailure = fixture.Service.Settings;
        var persistedAfterFailure = File.ReadAllText(fixture.Path);
        var notificationsAfterFailure = notifications;
        fixture.FailWrites = false;
        await fixture.Service.UpdateAsync(settings => settings.EnableFileLogging = true);
        var reloaded = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance);

        // Assert
        Assert.Same(original, afterFailure);
        Assert.Equal(persisted, persistedAfterFailure);
        Assert.Equal(0, notificationsAfterFailure);
        Assert.Equal(1, notifications);
        Assert.NotSame(original, fixture.Service.Settings);
        Assert.True(reloaded.Settings.EnableFileLogging);
        Assert.Equal("light", reloaded.Settings.Theme);
        Assert.True(reloaded.Settings.RedactApiClientAddresses);
        Assert.Equal("Warning", Assert.Single(reloaded.Settings.LogCategoryOverrides).Level);
        Assert.Equal(persisted, File.ReadAllText($"{fixture.Path}.bak"));
    }

    /// <summary>Verifies readers retain the prior snapshot during persistence and observers see only the committed snapshot.</summary>
    [Fact]
    public async Task UpdateAsync_WritePending_PublishesAndNotifiesAfterCommit()
    {
        // Arrange
        using var fixture = new Fixture();
        var original = fixture.Service.Settings;
        var persisted = File.ReadAllText(fixture.Path);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeWrite = async () =>
        {
            writing.TrySetResult();
            await commit.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var notifications = 0;
        WebSettings? observed = null;
        string? observedDocument = null;
        fixture.Service.OnSettingsChanged += () =>
        {
            notifications++;
            observed = fixture.Service.Settings;
            observedDocument = File.ReadAllText(fixture.Path);
        };

        // Act
        var updating = fixture.Service.UpdateAsync(settings =>
        {
            settings.Theme = "dark";
            settings.LogCategoryOverrides[0].Level = "Debug";
        });
        await writing.Task.WaitAsync(TestContext.Current.CancellationToken);
        var pending = fixture.Service.Settings;
        var pendingDocument = File.ReadAllText(fixture.Path);
        var pendingNotifications = notifications;
        commit.SetResult();
        await updating;

        // Assert
        Assert.Same(original, pending);
        Assert.Equal("light", original.Theme);
        Assert.Equal("Warning", Assert.Single(original.LogCategoryOverrides).Level);
        Assert.Equal(persisted, pendingDocument);
        Assert.Equal(0, pendingNotifications);
        Assert.Equal(1, notifications);
        Assert.Same(fixture.Service.Settings, observed);
        Assert.NotSame(original, observed);
        Assert.NotSame(original.LogCategoryOverrides, observed!.LogCategoryOverrides);
        Assert.Equal("dark", observed.Theme);
        Assert.Equal("Debug", Assert.Single(observed.LogCategoryOverrides).Level);
        Assert.Equal("dark", JsonSerializer.Deserialize<WebSettings>(observedDocument!)!.Theme);
    }

    /// <summary>Verifies notification failures cannot misreport a committed Web save or prevent other subscribers from running.</summary>
    [Fact]
    public async Task UpdateAsync_SubscriberThrows_PreservesSuccessfulCommitAndNotifiesOtherSubscribers()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Service.OnSettingsChanged += () => throw new InvalidOperationException("Controlled subscriber failure.");
        var notified = false;
        fixture.Service.OnSettingsChanged += () => notified = true;

        // Act
        await fixture.Service.UpdateAsync(settings => settings.Theme = "dark");

        // Assert
        Assert.True(notified);
        Assert.Equal("dark", fixture.Service.Settings.Theme);
        Assert.Equal("dark", JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(fixture.Path))!.Theme);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-web-settings-");

        /// <summary>Creates isolated persisted settings and a controllable atomic-write boundary.</summary>
        internal Fixture()
        {
            Store = new AppDataStore(_root.FullName);
            Path = Store.GetPath("web-settings.json");
            File.WriteAllText(Path, JsonSerializer.Serialize(new WebSettings
            {
                Theme = "light",
                LogCategoryOverrides = [new WebLogCategoryLevelSetting { Category = "Lineup", Level = "Warning" }]
            }));
            var storage = Substitute.For<IAppDataStore>();
            storage.GetPath(Arg.Any<string>()).Returns(call => Store.GetPath(call.Arg<string>()));
            storage.FileExists(Arg.Any<string>()).Returns(call => Store.FileExists(call.Arg<string>()));
            storage.ReadAllText(Arg.Any<string>()).Returns(call => Store.ReadAllText(call.Arg<string>()));
            storage.WriteAtomicallyAsync(
                Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (BeforeWrite is not null)
                    {
                        await BeforeWrite();
                    }
                    if (FailWrites)
                    {
                        throw new IOException("The settings storage is unavailable.");
                    }
                    await Store.WriteAtomicallyAsync(
                        call.ArgAt<string>(0), call.ArgAt<Func<Stream, CancellationToken, Task>>(1), call.ArgAt<string?>(2), call.ArgAt<bool>(3), call.ArgAt<CancellationToken>(4));
                });
            Service = new WebSettingsService(storage, NullLogger<WebSettingsService>.Instance);
        }

        /// <summary>Gets the underlying real file store.</summary>
        internal AppDataStore Store { get; }

        /// <summary>Gets the primary Web settings path.</summary>
        internal string Path { get; }

        /// <summary>Gets the service under test.</summary>
        internal WebSettingsService Service { get; }

        /// <summary>Gets or sets whether persistence fails before replacing the destination.</summary>
        internal bool FailWrites { get; set; }

        /// <summary>Gets or sets an asynchronous barrier before the actual write.</summary>
        internal Func<Task>? BeforeWrite { get; set; }

        /// <inheritdoc />
        public void Dispose() => _root.Delete(recursive: true);
    }
}

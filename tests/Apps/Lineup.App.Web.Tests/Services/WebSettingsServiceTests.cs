using System.Text.Json;
using Lineup.App.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies Web settings are published only after an atomic persistence operation succeeds.</summary>
public sealed class WebSettingsServiceTests
{
    /// <summary>Verifies invalid primary or backup-only Web documents require recovery without replacing persisted state.</summary>
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{}", true)]
    [InlineData("""{"SchemaVersion":0}""", false)]
    [InlineData("""{"SchemaVersion":2}""", false)]
    [InlineData("""{"SchemaVersion":"1"}""", false)]
    [InlineData("""{"SchemaVersion":1.5}""", false)]
    [InlineData("""{"SchemaVersion":1,"Theme":{}}""", false)]
    [InlineData("""{"SchemaVersion":1""", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    public async Task CheckStartup_InvalidWebDocuments_RequiresRecoveryAndBlocksWrites(string json, bool backupOnly)
    {
        // Arrange
        using var fixture = new Fixture();
        File.Delete(fixture.Path);
        var path = backupOnly ? $"{fixture.Path}.bak" : fixture.Path;
        File.WriteAllText(path, json);
        if (!backupOnly)
        {
            File.WriteAllText($"{fixture.Path}.bak", json);
        }
        var filesBefore = Directory.GetFiles(fixture.Store.RootPath).Order().ToArray();
        var mutationInvoked = false;

        // Act
        var state = WebSettingsService.CheckStartup(fixture.Store);
        var service = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance, state);
        var failure = await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.UpdateAsync(settings =>
        {
            mutationInvoked = true;
            settings.Theme = "dark";
        }));

        // Assert
        Assert.True(state.IsFactoryResetRequired);
        Assert.Contains("Web settings", state.Reason, StringComparison.Ordinal);
        Assert.Equal(state.Reason, failure.Message);
        Assert.False(mutationInvoked);
        Assert.Equal(filesBefore, Directory.GetFiles(fixture.Store.RootPath).Order().ToArray());
        Assert.All(filesBefore, file => Assert.Equal(json, File.ReadAllText(file)));
    }

    /// <summary>Verifies absent Web settings allow first-run defaults and normal persistence.</summary>
    [Fact]
    public async Task CheckStartup_AbsentWebDocuments_AllowsFirstRunAndSave()
    {
        // Arrange
        using var fixture = new Fixture();
        File.Delete(fixture.Path);

        // Act
        var state = WebSettingsService.CheckStartup(fixture.Store);
        var service = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance);
        var setupComplete = service.Settings.IsSetupComplete;
        await service.UpdateAsync(settings => settings.Theme = "dark");

        // Assert
        Assert.False(state.IsFactoryResetRequired);
        Assert.False(setupComplete);
        var saved = JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(fixture.Path))!;
        Assert.Equal(WebSettings.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal("dark", saved.Theme);
        Assert.False(File.Exists($"{fixture.Path}.bak"));
    }

    /// <summary>Verifies backup recovery rejects invalid schemas and preserves the valid backup on the first successful save.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"SchemaVersion":2}""")]
    [InlineData("""{"SchemaVersion":1""")]
    public async Task Load_ValidBackup_RecoversAndPreservesBackupOnSave(string? primary)
    {
        // Arrange
        using var fixture = new Fixture();
        File.Delete(fixture.Path);
        if (primary is not null)
        {
            File.WriteAllText(fixture.Path, primary);
        }
        var backup = JsonSerializer.Serialize(new WebSettings { Theme = "dark", FileLogRetentionDays = 19 });
        File.WriteAllText($"{fixture.Path}.bak", backup);

        // Act
        var state = WebSettingsService.CheckStartup(fixture.Store);
        var service = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance);
        var recoveredTheme = service.Settings.Theme;
        var primaryBeforeSave = File.Exists(fixture.Path) ? File.ReadAllText(fixture.Path) : null;
        await service.UpdateAsync(settings => settings.Theme = "light");
        var backupAfterRecovery = File.ReadAllText($"{fixture.Path}.bak");
        var firstSave = File.ReadAllText(fixture.Path);
        await service.UpdateAsync(settings => settings.Theme = "auto");

        // Assert
        Assert.False(state.IsFactoryResetRequired);
        Assert.Equal(primary, primaryBeforeSave);
        Assert.Equal("dark", recoveredTheme);
        Assert.Equal(19, service.Settings.FileLogRetentionDays);
        Assert.Equal(backup, backupAfterRecovery);
        Assert.Equal(firstSave, File.ReadAllText($"{fixture.Path}.bak"));
        Assert.Equal("auto", JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(fixture.Path))!.Theme);
    }

    /// <summary>Verifies an invalid unused backup does not force recovery when the primary is valid.</summary>
    [Fact]
    public void CheckStartup_ValidPrimaryWithInvalidBackup_UsesPrimary()
    {
        // Arrange
        using var fixture = new Fixture();
        const string backup = """{"SchemaVersion":2}""";
        File.WriteAllText($"{fixture.Path}.bak", backup);

        // Act
        var state = WebSettingsService.CheckStartup(fixture.Store);
        var service = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance);

        // Assert
        Assert.False(state.IsFactoryResetRequired);
        Assert.Equal("light", service.Settings.Theme);
        Assert.Equal(backup, File.ReadAllText($"{fixture.Path}.bak"));
    }

    /// <summary>Verifies Core recovery also blocks writes to otherwise valid Web settings.</summary>
    [Fact]
    public async Task UpdateAsync_CoreRecoveryRequired_PreservesValidWebSettings()
    {
        // Arrange
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.SettingsPath, "{}");
        var persisted = File.ReadAllText(fixture.Path);

        // Act
        var state = WebSettingsService.CheckStartup(fixture.Store);
        var service = new WebSettingsService(fixture.Store, NullLogger<WebSettingsService>.Instance, state);
        await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.UpdateAsync(settings => settings.Theme = "dark"));

        // Assert
        Assert.True(state.IsFactoryResetRequired);
        Assert.Equal(StartupSchemaGate.Check(fixture.Store), state);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        Assert.Equal("{}", File.ReadAllText(fixture.Store.SettingsPath));
        Assert.False(File.Exists($"{fixture.Path}.bak"));
    }

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

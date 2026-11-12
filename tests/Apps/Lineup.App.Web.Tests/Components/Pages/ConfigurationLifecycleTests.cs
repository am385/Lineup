using System.Text.Json;
using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Serilog.Events;
using Microsoft.Extensions.Localization;
using Xunit;
using TestContext = Xunit.TestContext;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>Verifies configuration lifecycle outcomes using real serialized stores and controlled persistence failures.</summary>
public sealed class ConfigurationLifecycleTests
{
    /// <summary>Exercises the application command without a rendered editor, including a failed second commit and its successful retry.</summary>
    [Fact]
    public async Task Command_WebWriteFails_PreservesCommitAndSupportsNonComponentRetry()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var command = CreateCommand(fixture, context);
        var draft = CreateDraft(fixture, context);
        fixture.FailWebWrites = true;

        // Act
        var partial = await command.SaveAsync(draft, []);
        var savedCore = File.ReadAllText(fixture.Store.SettingsPath);
        fixture.FailWebWrites = false;
        var completed = await command.SaveAsync(draft with { CoreRevision = Assert.IsType<string>(partial.CoreRevision) }, []);

        // Assert
        Assert.IsType<IOException>(partial.Failure);
        Assert.Null(partial.WebRevision);
        Assert.Equal(48, JsonSerializer.Deserialize<LineupSettings>(savedCore)!.EpgHistoryRetentionHours);
        Assert.NotNull(partial.Configuration);
        Assert.Null(completed.Failure);
        Assert.NotNull(completed.WebRevision);
        Assert.Empty(completed.ApplyErrors);
        Assert.False(fixture.Web.Settings.RedactApiClientAddresses);
    }

    /// <summary>Validates raw values at the command boundary before clamping setters or either store can hide invalid input.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(721)]
    public async Task Command_InvalidRetention_RejectsWithoutWriting(int hours)
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var command = CreateCommand(fixture, context);
        var draft = CreateDraft(fixture, context) with { EpgHistoryRetentionHours = hours };

        // Act
        var result = await command.SaveAsync(draft, []);

        // Assert
        Assert.Single(result.ValidationErrors);
        Assert.Null(result.CoreRevision);
        Assert.Null(result.WebRevision);
        Assert.Equal(0, fixture.CoreWriteCount);
        Assert.Equal(0, fixture.WebWriteCount);
        Assert.Equal(24, fixture.Core.Settings.EpgHistoryRetentionHours);
    }

    /// <summary>Checks Web conflicts inside the second serialized write even when a non-component caller invokes the command.</summary>
    [Fact]
    public async Task Command_WebChangesDuringCoreWrite_ReturnsPartialConflict()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var command = CreateCommand(fixture, context);
        var draft = CreateDraft(fixture, context);
        fixture.BeforeCoreWrite = () => fixture.Web.UpdateAsync(settings => settings.RedactApiDeviceAddresses = true);

        // Act
        var result = await command.SaveAsync(draft, []);

        // Assert
        Assert.IsType<SettingsConflictException>(result.Failure);
        Assert.NotNull(result.CoreRevision);
        Assert.Null(result.WebRevision);
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.True(fixture.Web.Settings.RedactApiDeviceAddresses);
        Assert.True(fixture.Web.Settings.RedactApiClientAddresses);
    }

    /// <summary>Verifies a failed second store reports the committed Core settings and supports a successful retry.</summary>
    [Fact]
    public async Task Save_WebWriteFails_ReportsPartialCommitAndRetries()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        component.Find("#redactApiClientAddresses").Change(false);
        fixture.FailWebWrites = true;

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());
        var partial = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        var coreAfterFailure = File.ReadAllText(fixture.Store.SettingsPath);
        var webAfterFailure = File.ReadAllText(fixture.WebPath);
        fixture.FailWebWrites = false;
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.True(partial.IsError);
        Assert.Contains("Core settings were saved, but Web settings were not saved.", partial.Message);
        Assert.Equal(48, JsonSerializer.Deserialize<LineupSettings>(coreAfterFailure)!.EpgHistoryRetentionHours);
        Assert.True(JsonSerializer.Deserialize<WebSettings>(webAfterFailure)!.RedactApiClientAddresses);
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.False(JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(fixture.WebPath))!.RedactApiClientAddresses);
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => !notification.IsError && notification.Message == "Settings saved successfully!");
    }

    /// <summary>Verifies a retry cannot overwrite another operator's newer Core configuration after a partial commit.</summary>
    [Fact]
    public async Task Save_PartialRetryAfterConcurrentChange_RejectsStaleDraft()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        component.Find("#redactApiClientAddresses").Change(false);
        fixture.FailWebWrites = true;
        await component.Find("button.btn-primary").ClickAsync(new());
        await fixture.Core.UpdateAsync(settings => settings.MaximumConcurrentStreams = 2);
        var persisted = File.ReadAllText(fixture.Store.SettingsPath);
        fixture.FailWebWrites = false;

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(persisted, File.ReadAllText(fixture.Store.SettingsPath));
        Assert.Equal(2, fixture.Core.Settings.MaximumConcurrentStreams);
        Assert.True(fixture.Web.Settings.RedactApiClientAddresses);
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError && notification.Message.Contains("Nothing was saved."));
    }

    /// <summary>Verifies two open editors cannot silently replace each other's saved configuration.</summary>
    [Fact]
    public async Task Save_TwoOpenEditors_RejectsSecondDraft()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var first = context.Render<Settings>();
        var second = context.Render<Settings>();
        first.Find("#epgHistoryRetentionHours").Change(48);
        second.Find("#epgHistoryRetentionHours").Change(72);
        await first.Find("button.btn-primary").ClickAsync(new());
        var persisted = File.ReadAllText(fixture.Store.SettingsPath);

        // Act
        await second.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.Equal(persisted, File.ReadAllText(fixture.Store.SettingsPath));
        Assert.Equal("72", second.Find("#epgHistoryRetentionHours").GetAttribute("value"));
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError && notification.Message.Contains("Nothing was saved."));
    }

    /// <summary>Verifies revision comparison occurs after a queued writer commits, not just before entering the store.</summary>
    [Fact]
    public async Task Save_CoreWriterAlreadyPending_ChecksRevisionInsideSerializedUpdate()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeCoreWrite = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var externalSave = fixture.Core.UpdateAsync(settings => settings.MaximumConcurrentStreams = 2);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var saving = component.Find("button.btn-primary").ClickAsync(new());
        try
        {
            Assert.False(saving.IsCompleted);
            Assert.Equal(0, fixture.Core.Settings.MaximumConcurrentStreams);
        }
        finally
        {
            release.TrySetResult();
            await externalSave;
            await saving;
        }

        // Assert
        Assert.Equal(2, fixture.Core.Settings.MaximumConcurrentStreams);
        Assert.Equal(24, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.Equal(1, fixture.CoreWriteCount);
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError && notification.Message.Contains("Nothing was saved."));
    }

    /// <summary>Verifies a Web update between the preflight and the second commit produces an honest partial conflict.</summary>
    [Fact]
    public async Task Save_WebChangesDuringCoreWrite_PreservesNewerWebSettings()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        fixture.BeforeCoreWrite = () => fixture.Web.UpdateAsync(settings => settings.RedactApiDeviceAddresses = true);

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.True(fixture.Web.Settings.RedactApiDeviceAddresses);
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.True(notification.IsError);
        Assert.Contains("Core settings were saved, but Web settings changed concurrently", notification.Message);
    }

    /// <summary>Unexpected second-store failures cannot hide a Core commit already reported by the command.</summary>
    [Fact]
    public async Task Save_UnexpectedWebFailure_StillReportsCommittedCore()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        fixture.WebWriteFailure = new Exception("Controlled unexpected second-store failure.");

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.True(notification.IsError);
        Assert.Contains("Core settings were saved, but Web settings were not saved.", notification.Message);
        Assert.DoesNotContain("Nothing was saved.", notification.Message);
    }

    /// <summary>Verifies a failed lineup result prevents setup completion and terminal success.</summary>
    [Fact]
    public async Task Save_SetupLineupFails_DoesNotCompleteSetupOrReportSuccess()
    {
        // Arrange
        using var fixture = new Fixture(setupComplete: false);
        using var context = fixture.CreateContext();
        var statuses = Substitute.For<IPhysicalDeviceStatusService>();
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns(
            [new PhysicalLineupRefreshResult { DeviceId = PhysicalDeviceId.New(), Succeeded = false, Error = "Unavailable device." }]);
        context.Services.AddSingleton(statuses);
        context.Services.AddSingleton(lineups);
        var component = context.Render<Settings>();

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.False(fixture.Web.Settings.IsSetupComplete);
        Assert.False(JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(fixture.WebPath))!.IsSetupComplete);
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.True(notification.IsError);
        Assert.Contains("Settings were saved, but", notification.Message);
        Assert.Contains("not fully applied", notification.Message);
    }

    /// <summary>Verifies initial setup remains incomplete until the captured topology has actually finished applying.</summary>
    [Theory]
    [InlineData(TopologyApplicationPhase.Applied)]
    [InlineData(TopologyApplicationPhase.Degraded)]
    public async Task Save_SetupApplicationPending_WaitsBeforeCompleting(TopologyApplicationPhase phase)
    {
        // Arrange
        using var fixture = new Fixture(setupComplete: false);
        using var context = fixture.CreateContext();
        context.Services.AddSingleton(Substitute.For<IPhysicalDeviceStatusService>());
        var runtime = Substitute.For<IStreamingTopologyRuntime>();
        runtime.Application.Returns(new StreamingTopologyApplication { DesiredRevision = "pending", Phase = TopologyApplicationPhase.Applying });
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StreamingTopologyApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.WaitForApplicationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            entered.TrySetResult(call.Arg<string>());
            return release.Task;
        });
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();

        // Act
        var saving = component.Find("button.btn-primary").ClickAsync(new());
        var revision = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(saving.IsCompleted);
            Assert.False(fixture.Web.Settings.IsSetupComplete);
            Assert.Empty(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        }
        finally
        {
            release.TrySetResult(new StreamingTopologyApplication
            {
                DesiredRevision = revision,
                AttemptedRevision = revision,
                AppliedRevision = phase == TopologyApplicationPhase.Applied ? revision : null,
                Phase = phase
            });
            await saving;
        }

        // Assert
        Assert.Equal(phase == TopologyApplicationPhase.Applied, fixture.Web.Settings.IsSetupComplete);
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.Equal(phase == TopologyApplicationPhase.Degraded, notification.IsError);
        if (phase == TopologyApplicationPhase.Applied)
        {
            Assert.Equal("Settings saved successfully!", notification.Message);
        }
        else
        {
            Assert.Contains("not fully applied", notification.Message);
        }
    }

    /// <summary>Verifies ordinary topology application states do not display a persistent settings notice or revision hashes.</summary>
    [Theory]
    [InlineData(TopologyApplicationPhase.Pending)]
    [InlineData(TopologyApplicationPhase.Applying)]
    [InlineData(TopologyApplicationPhase.Applied)]
    public void Settings_NonDegradedTopology_DoesNotDisplayApplicationNotice(TopologyApplicationPhase phase)
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var runtime = Substitute.For<IStreamingTopologyRuntime>();
        runtime.Application.Returns(new StreamingTopologyApplication
        {
            DesiredRevision = "desired-revision",
            AttemptedRevision = "desired-revision",
            AppliedRevision = "applied-revision",
            Phase = phase
        });
        context.Services.AddSingleton(runtime);

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Empty(component.FindAll("#topologyApplication"));
        Assert.DoesNotContain("desired-revision", component.Markup);
        Assert.DoesNotContain("applied-revision", component.Markup);
    }

    /// <summary>Verifies failed or partial output application remains actionable without exposing revision hashes or rewriting settings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_DegradedTopology_RequestsRuntimeApplicationWithoutSaving(bool partial)
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var runtime = Substitute.For<IStreamingTopologyRuntime>();
        runtime.Application.Returns(new StreamingTopologyApplication
        {
            DesiredRevision = "desired-revision",
            AttemptedRevision = "desired-revision",
            AppliedRevision = "previous-revision",
            Phase = TopologyApplicationPhase.Degraded,
            Outputs =
            [
                new GuideOutputPublicationResult
                {
                    OutputId = GuideOutputId.New(),
                    Name = "Unavailable guide",
                    Succeeded = partial,
                    SourceDiagnostics = partial
                        ? [new GuideSourceDiagnostic { SourceId = GuideSourceId.New(), SourceName = "Unavailable source", Code = GuideSourceDiagnosticCode.AdapterUnavailable, Message = "Adapter unavailable." }]
                        : []
                }
            ]
        });
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();

        // Act
        await component.Find("#retryTopologyApplication").ClickAsync(new());

        // Assert
        Assert.Contains("not fully applied", component.Find("#topologyApplicationPhase").TextContent);
        Assert.Contains("Unavailable guide", component.Find("#topologyApplication").TextContent);
        Assert.Contains("Configuration status", component.Find("#topologyApplication h2").TextContent);
        Assert.Contains("alert-warning", component.Find("#topologyApplication").ClassList);
        Assert.DoesNotContain("desired-revision", component.Markup);
        Assert.DoesNotContain("previous-revision", component.Markup);
        runtime.Received(1).Retry();
        Assert.Equal(0, fixture.CoreWriteCount);
        Assert.Equal(0, fixture.WebWriteCount);
    }

    /// <summary>Verifies runtime events reveal actionable failures and remove the notice when application succeeds.</summary>
    [Fact]
    public async Task Settings_TopologyApplicationChanges_ShowsOnlyFailures()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var runtime = Substitute.For<IStreamingTopologyRuntime>();
        var application = new StreamingTopologyApplication { DesiredRevision = "desired-revision", Phase = TopologyApplicationPhase.Applied };
        runtime.Application.Returns(application);
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();

        // Act
        await component.InvokeAsync(() =>
        {
            runtime.Application.Returns(application with { Phase = TopologyApplicationPhase.Degraded });
            runtime.ApplicationChanged += Raise.Event<Action>();
        });

        // Assert
        component.WaitForAssertion(() => Assert.Single(component.FindAll("#topologyApplication")));

        // Act
        await component.InvokeAsync(() =>
        {
            runtime.Application.Returns(application);
            runtime.ApplicationChanged += Raise.Event<Action>();
        });

        // Assert
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("#topologyApplication")));
    }

    /// <summary>Verifies synchronized header theme changes do not cause false conflicts or discard other pending edits.</summary>
    [Fact]
    public async Task Save_ExternalHeaderThemeChanges_PreservesThemeAndOtherDraftEdits()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);

        // Act
        await component.InvokeAsync(() => fixture.Web.UpdateAsync(settings => settings.Theme = "dark"));
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal("dark", fixture.Web.Settings.Theme);
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.DoesNotContain(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError);
    }

    /// <summary>Preserves independently synchronized theme changes while the command awaits Core persistence.</summary>
    [Fact]
    public async Task Save_HeaderThemeChangesDuringCoreWrite_PreservesNewTheme()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var component = context.Render<Settings>();
        component.Find("#epgHistoryRetentionHours").Change(48);
        fixture.BeforeCoreWrite = () => fixture.Web.UpdateAsync(settings => settings.Theme = "dark");

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal("dark", fixture.Web.Settings.Theme);
        Assert.Equal(48, fixture.Core.Settings.EpgHistoryRetentionHours);
        Assert.DoesNotContain(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => notification.IsError);
    }

    /// <summary>Verifies a failed file-sink application can be retried after both settings documents have already committed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_FileLoggingApplyFails_RetriesPendingApplication(bool reload)
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        var logPath = fixture.Store.LogDirectoryPath;
        File.WriteAllText(logPath, "Blocks directory creation.");
        var fileSettings = new FileLoggingSettings(false, LogEventLevel.Information, 7, 50);
        using var manager = new FileLogManager(logPath, fileSettings, "{Message:lj}{NewLine}");
        var runtime = new LoggingRuntimeState(fixture.Store, fileSettings, [], manager);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));
        var component = context.Render<Settings>();
        component.Find("#enableFileLogging").Change(true);

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());
        var partial = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        var persistedEnabled = fixture.Web.Settings.EnableFileLogging;
        var initiallyApplied = runtime.FileSettings.Enabled;
        File.Delete(logPath);
        if (reload)
        {
            component = context.Render<Settings>();
        }
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.True(persistedEnabled);
        Assert.False(initiallyApplied);
        Assert.True(partial.IsError);
        Assert.Contains("Settings were saved, but", partial.Message);
        Assert.True(runtime.FileSettings.Enabled);
        Assert.True(Directory.Exists(logPath));
        Assert.Contains(context.Services.GetRequiredService<IStatusNotificationService>().Notifications, notification => !notification.IsError && notification.Message == "Settings saved successfully!");
    }

    /// <summary>Verifies missing status results cannot make an enabled physical device appear connected during setup.</summary>
    [Fact]
    public async Task Save_SetupDeviceHasNoStatus_DoesNotCompleteSetup()
    {
        // Arrange
        using var fixture = new Fixture(setupComplete: false);
        using var context = fixture.CreateContext();
        context.Services.AddSingleton(Substitute.For<IPhysicalDeviceStatusService>());
        var lineups = Substitute.For<IPhysicalLineupService>();
        context.Services.AddSingleton(lineups);
        var driver = context.Services.GetServices<IPhysicalTunerDriver>().First();
        var id = PhysicalDeviceId.New();
        await fixture.Core.UpdateAsync(settings => settings.Streaming = settings.Streaming with
        {
            PhysicalDevices = [new PhysicalDeviceDefinition { Id = id, Name = "Missing device", Adapter = driver.CreateDefaultConfiguration(id) }]
        });
        var component = context.Render<Settings>();

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.False(fixture.Web.Settings.IsSetupComplete);
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.True(notification.IsError);
        Assert.Contains("Missing device", notification.Message);
        await lineups.DidNotReceive().RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies reloading after a partial save cannot lose a required proxy-pipeline restart.</summary>
    [Fact]
    public async Task Save_PartialProxySaveThenReload_RestartsForUnappliedTrustChanges()
    {
        // Arrange
        using var fixture = new Fixture();
        using var context = fixture.CreateContext();
        context.Services.AddSingleton(ReverseProxyConfigurator.Load(fixture.Core.Settings));
        var restart = Substitute.For<IApplicationRestartService>();
        context.Services.AddSingleton(restart);
        var component = context.Render<Settings>();
        component.Find("#knownProxies").Change("127.0.0.1");
        fixture.FailWebWrites = true;
        await component.Find("button.btn-primary").ClickAsync(new());
        await restart.DidNotReceive().RestartAsync(Arg.Any<CancellationToken>());
        fixture.FailWebWrites = false;
        var reloaded = context.Render<Settings>();

        // Act
        await reloaded.Find("button.btn-primary").ClickAsync(new());

        // Assert
        Assert.Equal("127.0.0.1", Assert.Single(fixture.Core.Settings.KnownProxies));
        await restart.Received(1).RestartAsync(Arg.Any<CancellationToken>());
    }

    private static SettingsSaveCommand CreateCommand(Fixture fixture, BunitContext context) => new(
        fixture.Core, fixture.Web, context.Services.GetRequiredService<IStringLocalizer<UiStrings>>(),
        context.Services.GetServices<IGuideSourceDriver>(), context.Services.GetServices<IPhysicalTunerDriver>(),
        context.Services.GetServices<IVirtualTunerDriver>(), context.Services.GetServices<IGuideOutputDriver>());

    private static SettingsSaveDraft CreateDraft(Fixture fixture, BunitContext context) => new()
    {
        Topology = new TopologySettingsEditor().Edit(fixture.Core.Settings.Streaming, fixture.Core.ConfiguredXmltvOutputPath, context.Services.GetServices<IGuideOutputDriver>()),
        CoreRevision = ConfigurationRevision.ForSettings(fixture.Core.Settings),
        WebRevision = SettingsSaveCommand.GetWebRevision(fixture.Web.Settings),
        EpgHistoryRetentionHours = 48,
        DeviceRefreshIntervalMinutes = fixture.Core.Settings.DeviceRefreshIntervalMinutes,
        TunerRefreshIntervalSeconds = fixture.Core.Settings.TunerRefreshIntervalSeconds,
        ActiveStreamRefreshIntervalSeconds = fixture.Web.Settings.ActiveStreamRefreshIntervalSeconds,
        XmltvOutputPath = fixture.Core.Settings.XmltvOutputPath,
        RedactApiClientAddresses = false
    };

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-configuration-lifecycle-");

        /// <summary>Creates real Core and Web stores with controlled write boundaries.</summary>
        internal Fixture(bool setupComplete = true)
        {
            Store = new AppDataStore(_root.FullName);
            WebPath = Store.GetPath("web-settings.json");
            File.WriteAllText(WebPath, JsonSerializer.Serialize(new WebSettings { IsSetupComplete = setupComplete }));
            var storage = Substitute.For<IAppDataStore>();
            storage.RootPath.Returns(Store.RootPath);
            storage.SettingsPath.Returns(Store.SettingsPath);
            storage.SettingsBackupPath.Returns(Store.SettingsBackupPath);
            storage.DatabasePath.Returns(Store.DatabasePath);
            storage.FactoryResetRequestPath.Returns(Store.FactoryResetRequestPath);
            storage.GetPath(Arg.Any<string>()).Returns(call => Store.GetPath(call.Arg<string>()));
            storage.FileExists(Arg.Any<string>()).Returns(call => Store.FileExists(call.Arg<string>()));
            storage.ReadAllText(Arg.Any<string>()).Returns(call => Store.ReadAllText(call.Arg<string>()));
            storage.ReadAllTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Store.ReadAllTextAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
            storage.WriteAtomicallyAsync(
                Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (call.ArgAt<string>(0) == Store.SettingsPath)
                    {
                        CoreWriteCount++;
                        if (BeforeCoreWrite is not null)
                        {
                            await BeforeCoreWrite();
                        }
                    }
                    else
                    {
                        WebWriteCount++;
                        if (WebWriteFailure is { } failure)
                        {
                            throw failure;
                        }
                        if (FailWebWrites)
                        {
                            throw new IOException("Controlled Web settings write failure.");
                        }
                    }
                    await Store.WriteAtomicallyAsync(
                        call.ArgAt<string>(0), call.ArgAt<Func<Stream, CancellationToken, Task>>(1), call.ArgAt<string?>(2), call.ArgAt<bool>(3), call.ArgAt<CancellationToken>(4));
                });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppConstants.GuideOutputDirConfigKey] = _root.FullName
            }).Build();
            Core = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, storage, configuration, []);
            Web = new WebSettingsService(storage, NullLogger<WebSettingsService>.Instance);
        }

        /// <summary>Gets the underlying real file store.</summary>
        internal AppDataStore Store { get; }

        /// <summary>Gets the Web settings document path.</summary>
        internal string WebPath { get; }

        /// <summary>Gets the Core settings service.</summary>
        internal LineupSettingsService Core { get; }

        /// <summary>Gets the Web settings service.</summary>
        internal WebSettingsService Web { get; }

        /// <summary>Gets or sets the controlled Web write failure.</summary>
        internal bool FailWebWrites { get; set; }

        /// <summary>Gets or sets an unexpected failure at the second-store persistence boundary.</summary>
        internal Exception? WebWriteFailure { get; set; }

        /// <summary>Gets or sets a controlled asynchronous Core write boundary.</summary>
        internal Func<Task>? BeforeCoreWrite { get; set; }

        /// <summary>Gets the number of attempted Core writes.</summary>
        internal int CoreWriteCount { get; private set; }

        /// <summary>Gets the number of attempted Web writes.</summary>
        internal int WebWriteCount { get; private set; }

        /// <summary>Creates the settings editor host.</summary>
        internal BunitContext CreateContext()
        {
            var context = new BunitContext();
            var timeZone = Substitute.For<ITimeZoneService>();
            timeZone.TimeZone.Returns(TimeZoneInfo.Utc);
            context.Services.AddSingleton<ILineupSettingsService>(Core);
            context.Services.AddSingleton<IWebSettingsService>(Web);
            context.Services.AddSingleton(timeZone);
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            return context;
        }

        /// <inheritdoc />
        public void Dispose() => _root.Delete(recursive: true);
    }
}

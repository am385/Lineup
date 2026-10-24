using System.Reflection;
using Lineup.Core;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.LineupLink;
using Lineup.SiliconDust.GuideSource;
using Lineup.SiliconDust.HDHomeRun.DiscoveryRuntime;
using Lineup.SiliconDust.HDHomeRun.PhysicalTuner;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.WebPlayer;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.Core.Hosting;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Protocol;
using Lineup.VirtualOutputs.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spectre.Console;

namespace Lineup.Tui;

/// <summary>
/// Represents program.
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable(AppConstants.DatabasePathEnvVar);
        var builder = Host.CreateApplicationBuilder(args);
        var appDataPath = builder.Configuration[AppConstants.TuiAppDataPathConfigKey];
        var resolvedAppDataPath = string.IsNullOrWhiteSpace(appDataPath)
            ? Path.Combine(Environment.CurrentDirectory, ".lineup", "tui")
            : appDataPath.Trim();
        var appDataStore = new AppDataStore(resolvedAppDataPath);
        var transientDataStore = new TransientDataStore(Path.Combine(resolvedAppDataPath, "transient"));
        var resolvedDatabasePath = databasePath ?? appDataStore.DatabasePath;
        var startupState = StartupSchemaGate.Check(appDataStore.SettingsPath, appDataStore.SettingsBackupPath, resolvedDatabasePath);

        if (startupState.IsFactoryResetRequired)
        {
            var resetState = RequestFactoryReset(startupState, appDataStore, resolvedDatabasePath);
            if (resetState is null)
            {
                return 2;
            }
            startupState = resetState;
        }

        // Configure Serilog from appsettings.json
        builder.Services.AddSerilog(config => config
            .ReadFrom.Configuration(builder.Configuration));

        builder.Services.AddSingleton<IAppDataStore>(appDataStore);
        builder.Services.AddSingleton(appDataStore);
        builder.Services.AddSingleton<ITransientDataStore>(transientDataStore);
        builder.Services.AddSingleton(transientDataStore);
        builder.Services.AddSingleton(startupState);
        builder.Services.AddEpgCore(resolvedDatabasePath, appDataStore);
        builder.Services.AddSingleton<ITopologyGuideService, TopologyGuideService>();
        builder.Services.AddHttpClient();
        AddStreamingAdapters(builder.Services);
        builder.Services.AddSingleton<LineupSettingsService>();
        builder.Services.AddSingleton<ILineupSettingsService>(provider => provider.GetRequiredService<LineupSettingsService>());
        builder.Services.AddLineupMpegTsStreaming();
        builder.Services.AddLineupStreamShutdownWorker();
        builder.Services.AddLineupVirtualOutputRuntime(CreateVirtualOutputRuntimeOptions(builder.Configuration, transientDataStore));
        builder.Services.AddHdHomeRunDiscoveryRuntime();

        using var host = builder.Build();
        await host.StartAsync();

        var guides = host.Services.GetRequiredService<IGuideOrchestrator>();
        var guideQueries = host.Services.GetRequiredService<IGuideQueryService>();
        var topologyGuides = host.Services.GetRequiredService<ITopologyGuideService>();
        var lineups = host.Services.GetRequiredService<IPhysicalLineupService>();
        var settings = host.Services.GetRequiredService<ILineupSettingsService>();
        var adapters = host.Services.GetRequiredService<IStreamingAdapterRegistry>();
        var topologyErrors = await ApplyTopologyIfValidAsync(lineups, guides, settings.Settings.Streaming);

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Lineup").Color(Color.Blue));

            var configuration = settings.Settings.Streaming;
            if (topologyErrors.Count == 0)
            {
                await DisplayStatsAsync(guideQueries, configuration);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Streaming configuration is invalid and could not be applied.[/]");
                foreach (var error in topologyErrors)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
                }
                AnsiConsole.MarkupLine("[yellow]Use Configure Sources to correct the topology, or exit without changing saved settings.[/]");
            }

            var choices = topologyErrors.Count == 0
                ? new[]
                {
                    "Configure Sources", "View Cache Statistics", "Refresh Physical Channels", "Fetch EPG Data", "Generate Guide Outputs",
                    "Export XMLTV", "View Channels", "View Programs", "About", "Exit"
                }
                : ["Configure Sources", "Exit"];

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold blue]What would you like to do?[/]")
                    .PageSize(10)
                    .AddChoices(choices));

            switch (choice)
            {
                case "Configure Sources":
                    await TuiAdapterSettingsEditor.ConfigureAsync(
                        settings,
                        host.Services.GetServices<IPhysicalTunerDriver>().ToArray(),
                        host.Services.GetServices<IVirtualTunerDriver>().ToArray(),
                        host.Services.GetServices<IGuideSourceDriver>().ToArray(),
                        host.Services.GetServices<IGuideOutputDriver>().ToArray());
                    topologyErrors = await ApplyTopologyIfValidAsync(lineups, guides, settings.Settings.Streaming);
                    break;
                case "View Cache Statistics":
                    await ViewStatsAsync(guideQueries, configuration);
                    break;
                case "Refresh Physical Channels":
                    await RefreshChannelsAsync(lineups, guides, configuration);
                    break;
                case "Fetch EPG Data":
                    await FetchDataAsync(guides);
                    break;
                case "Generate Guide Outputs":
                    await GenerateGuideOutputsAsync(topologyGuides);
                    break;
                case "Export XMLTV":
                    await ExportXmltvAsync(settings.Settings.Streaming, adapters);
                    break;
                case "View Channels":
                    await ViewChannelsAsync(guideQueries, configuration);
                    break;
                case "View Programs":
                    await ViewProgramsAsync(guideQueries, configuration);
                    break;
                case "About":
                    ShowAbout();
                    break;
                case "Exit":
                    await host.StopAsync();
                    return 0;
            }

        }
    }

    private static VirtualOutputRuntimeOptions CreateVirtualOutputRuntimeOptions(
        IConfiguration configuration,
        ITransientDataStore transientDataStore)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? new VirtualOutputIpcEndpoint
            {
                Transport = VirtualOutputIpcTransport.NamedPipe,
                Address = $"lineup-virtual-outputs-{Environment.ProcessId}"
            }
            : new VirtualOutputIpcEndpoint
            {
                Transport = VirtualOutputIpcTransport.UnixDomainSocket,
                Address = Path.Combine(transientDataStore.RootPath, $"virtual-outputs-{Environment.ProcessId}.sock")
            };
        var configuredWorkerPath = configuration[AppConstants.VirtualOutputWorkerPathConfigKey];
        return new VirtualOutputRuntimeOptions
        {
            WorkerPackageRoot = string.IsNullOrWhiteSpace(configuredWorkerPath)
                ? Path.Combine(AppContext.BaseDirectory, "virtual-output-workers")
                : Path.GetFullPath(configuredWorkerPath),
            IpcEndpoint = endpoint
        };
    }

    /// <summary>Registers every physical, virtual, and guide adapter supported by the TUI.</summary>
    /// <param name="services">Application service collection.</param>
    internal static void AddStreamingAdapters(IServiceCollection services)
    {
        services.AddHDHomeRunPhysicalTuner();
        services.AddXmltvGuideOutput();
        services.AddJsonGuideOutput();
        services.AddSiliconDustGuideSource();
        services.AddLineupLinkPhysicalTuner();
        services.AddLineupLinkVirtualTuner();
        services.AddLineupLinkGuideSource();
        services.AddSingleton<HdHomeRunVirtualTunerDriver>();
        services.AddSingleton<IVirtualTunerDriver>(provider => provider.GetRequiredService<HdHomeRunVirtualTunerDriver>());
        services.AddSingleton<WebPlayerVirtualTunerDriver>();
        services.AddSingleton<IVirtualTunerDriver>(provider => provider.GetRequiredService<WebPlayerVirtualTunerDriver>());
        services.AddSingleton<IStreamingConfigurationNormalizer>(provider => provider.GetRequiredService<WebPlayerVirtualTunerDriver>());
    }

    /// <summary>Refreshes physical devices before applying guide sources that depend on their runtime capabilities.</summary>
    /// <param name="lineups">Physical lineup service.</param>
    /// <param name="guides">Guide-source orchestrator.</param>
    /// <param name="configuration">Streaming topology to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Physical lineup refresh results.</returns>
    internal static async Task<IReadOnlyList<PhysicalLineupRefreshResult>> ApplyTopologyAsync(
        IPhysicalLineupService lineups,
        IGuideOrchestrator guides,
        StreamingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var results = await lineups.RefreshAsync(configuration, cancellationToken);
        await guides.ApplyAsync(configuration, cancellationToken);
        return results;
    }

    /// <summary>Applies valid topology or returns diagnostics so configuration remains accessible.</summary>
    internal static async Task<IReadOnlyList<string>> ApplyTopologyIfValidAsync(IPhysicalLineupService lineups, IGuideOrchestrator guides, StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = StreamingConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            return errors;
        }

        await ApplyTopologyAsync(lineups, guides, configuration, cancellationToken);
        return [];
    }

    static LineupStartupState? RequestFactoryReset(LineupStartupState startupState, AppDataStore appDataStore, string databasePath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        AnsiConsole.MarkupLine("[red]Factory reset required.[/]");
        AnsiConsole.WriteLine(startupState.Reason ?? "Persisted state is incompatible with this Lineup release.");
        AnsiConsole.MarkupLine($"[yellow]The TUI database will be permanently deleted:[/] {Markup.Escape(fullDatabasePath)}");
        AnsiConsole.MarkupLine($"[yellow]The TUI settings will be replaced with defaults:[/] {Markup.Escape(appDataStore.SettingsPath)}");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Factory reset the Lineup TUI configuration and database?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Factory reset canceled. No files were changed.[/]");
            return null;
        }

        var confirmation = AnsiConsole.Ask<string>("Type [red]RESET[/] to confirm:");
        if (!string.Equals(confirmation, "RESET", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[grey]Factory reset canceled. No files were changed.[/]");
            return null;
        }

        try
        {
            var resetState = ResetPersistedState(appDataStore, fullDatabasePath);
            if (resetState.IsFactoryResetRequired)
            {
                AnsiConsole.MarkupLine($"[red]Factory reset failed:[/] {Markup.Escape(resetState.Reason ?? "Persisted state remains incompatible.")}");
                return null;
            }
            AnsiConsole.MarkupLine("[green]Factory reset completed. Starting Lineup TUI with clean configuration and database state.[/]");
            AnsiConsole.WriteLine();
            return resetState;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Factory reset failed:[/] {Markup.Escape(exception.Message)}");
            return null;
        }
    }

    /// <summary>Deletes TUI-owned persisted state and returns a fresh startup schema result.</summary>
    /// <param name="appDataStore">TUI application-data store.</param>
    /// <param name="databasePath">Resolved database path, including an optional external override.</param>
    /// <returns>The post-reset startup schema result.</returns>
    internal static LineupStartupState ResetPersistedState(AppDataStore appDataStore, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(appDataStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullDatabasePath = Path.GetFullPath(databasePath);
        appDataStore.DeleteContents();
        if (!string.Equals(fullDatabasePath, appDataStore.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            DatabaseFactoryReset.Reset(fullDatabasePath);
        }
        return StartupSchemaGate.Check(appDataStore.SettingsPath, appDataStore.SettingsBackupPath, fullDatabasePath);
    }

    static async Task DisplayStatsAsync(IGuideQueryService guides, StreamingConfiguration configuration)
    {
        var stats = await guides.GetStatisticsAsync(configuration);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Channels", stats.ChannelCount.ToString());
        table.AddRow("Programs", stats.ProgramCount.ToString());

        if (stats.TotalTimeSpan.HasValue)
        {
            table.AddRow("Time Span", FormatTimeSpan(stats.TotalTimeSpan.Value));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static async Task ViewStatsAsync(IGuideQueryService guides, StreamingConfiguration configuration)
    {
        var stats = await guides.GetStatisticsAsync(configuration);

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[blue]Cache Statistics[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Channels", stats.ChannelCount.ToString());
        table.AddRow("Programs", stats.ProgramCount.ToString());
        table.AddRow("Earliest Program", stats.EarliestProgramStartUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "N/A");
        table.AddRow("Latest Program", stats.LatestProgramEndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "N/A");
        table.AddRow("Total Time Span", stats.TotalTimeSpan.HasValue ? FormatTimeSpan(stats.TotalTimeSpan.Value) : "N/A");
        table.AddRow("Safe Fetch Start", stats.SafeCoverageEndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "N/A");

        if (stats.SafeCoverageEndUtc.HasValue && stats.LatestProgramEndUtc.HasValue && stats.SafeCoverageEndUtc < stats.LatestProgramEndUtc)
        {
            var gap = stats.LatestProgramEndUtc.Value - stats.SafeCoverageEndUtc.Value;
            table.AddRow("[yellow]Channel Gap[/]", $"[yellow]{FormatTimeSpan(gap)}[/]");
        }

        AnsiConsole.Write(table);
        WaitForKey();
    }

    static async Task RefreshChannelsAsync(IPhysicalLineupService lineups, IGuideOrchestrator guides, StreamingConfiguration configuration)
    {
        IReadOnlyList<PhysicalLineupRefreshResult>? results = null;
        await AnsiConsole.Status()
            .StartAsync("Refreshing physical HDHomeRun channels...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                results = await ApplyTopologyAsync(lineups, guides, configuration);
            });

        var channels = results!.Where(result => result.Error is null).Sum(result => result.ChannelCount);
        var failures = results!.Count(result => result.Error is not null);
        AnsiConsole.MarkupLine($"[green]Saved {channels} physical channels.[/] [yellow]{failures} device refresh failures.[/]");
        WaitForKey();
    }

    static async Task FetchDataAsync(IGuideOrchestrator orchestrator)
    {
        AnsiConsole.MarkupLine("[grey]Downloading the complete guide available from SiliconDust.[/]");
        AnsiConsole.WriteLine();

        var results = await orchestrator.RefreshAllAsync();
        foreach (var result in results)
        {
            var color = result.Succeeded ? "green" : "red";
            var message = result.Succeeded
                ? $"{result.ChannelCount} channels and {result.ProgramCount} programs imported"
                : result.Error ?? "Guide refresh failed";
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(result.SourceId.ToString())}: {Markup.Escape(message)}[/]");
        }
        WaitForKey();
    }

    static async Task GenerateGuideOutputsAsync(ITopologyGuideService guides)
    {
        IReadOnlyList<GuideOutputPublicationResult>? results = null;
        await AnsiConsole.Status()
            .StartAsync("Publishing enabled guide outputs...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                results = await guides.PublishAsync();
            });

        foreach (var result in results!)
        {
            var color = result.Succeeded ? "green" : "red";
            var message = result.Succeeded
                ? $"{result.ChannelCount} channels and {result.ProgramCount} programs published to {result.Path}"
                : result.Error ?? "Publication failed";
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(result.Name)}: {Markup.Escape(message)}[/]");
        }
        WaitForKey();
    }

    static async Task ExportXmltvAsync(StreamingConfiguration configuration, IStreamingAdapterRegistry adapters)
    {
        string? sourceFile;
        try
        {
            sourceFile = ResolveBuiltInXmltvOutputPath(configuration, adapters);
        }
        catch (InvalidOperationException exception)
        {
            AnsiConsole.MarkupLine($"[red]Unable to resolve the built-in XMLTV output:[/] {Markup.Escape(exception.Message)}");
            WaitForKey();
            return;
        }

        if (sourceFile is null)
        {
            AnsiConsole.MarkupLine("[red]The built-in XMLTV guide output is not enabled.[/]");
            WaitForKey();
            return;
        }

        if (!File.Exists(sourceFile))
        {
            AnsiConsole.MarkupLine("[red]No XMLTV file found. Please generate one first.[/]");
            WaitForKey();
            return;
        }

        var fileInfo = new FileInfo(sourceFile);
        AnsiConsole.MarkupLine($"[grey]Source file: {sourceFile} ({fileInfo.Length / 1024:N0} KB, modified {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm})[/]");

        var destination = AnsiConsole.Ask("Export to path:", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppConstants.DefaultXmltvFileName));

        try
        {
            var destinationDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            await Task.Run(() => File.Copy(sourceFile, destination, overwrite: true));

            AnsiConsole.MarkupLine($"[green]Exported to {destination} successfully![/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error exporting file: {ex.Message}[/]");
        }

        WaitForKey();
    }

    /// <summary>Resolves the enabled built-in XMLTV output through its plugin-owned destination contract.</summary>
    /// <param name="configuration">Current streaming configuration.</param>
    /// <param name="adapters">Registered streaming adapters.</param>
    /// <returns>The resolved XMLTV publication path, or <see langword="null"/> when the built-in output is disabled.</returns>
    internal static string? ResolveBuiltInXmltvOutputPath(StreamingConfiguration configuration, IStreamingAdapterRegistry adapters)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);
        var output = configuration.GuideOutputs.SingleOrDefault(candidate =>
            candidate.Enabled &&
            candidate.Id == XmltvGuideOutputDriver.BuiltInId);
        return output is null
            ? null
            : adapters.GetGuideOutput(output.Adapter.Kind).ResolveDestination(output.Adapter).Path;
    }

    static async Task ViewChannelsAsync(IGuideQueryService guides, StreamingConfiguration configuration)
    {
        var snapshot = await guides.BuildUnionSnapshotAsync(configuration);
        var channels = snapshot.Channels;

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[blue]Channels ({channels.Count})[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Number")
            .AddColumn("Name")
            .AddColumn("Affiliate");

        foreach (var channel in channels.OrderBy(c => c.Number))
        {
            table.AddRow(channel.Number, channel.Name, channel.Affiliate ?? "");
        }

        AnsiConsole.Write(table);
        WaitForKey();
    }

    static async Task ViewProgramsAsync(IGuideQueryService guides, StreamingConfiguration configuration)
    {
        var snapshot = await guides.BuildUnionSnapshotAsync(configuration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(6));
        var programs = snapshot.Channels.SelectMany(channel => channel.Programs.Select(program => new { Channel = channel.Number, Program = program })).ToArray();

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[blue]Programs - Next 6 Hours ({programs.Length})[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Channel")
            .AddColumn("Time")
            .AddColumn("Title")
            .AddColumn("Episode");

        foreach (var program in programs.Take(50))
        {
            table.AddRow(program.Channel, program.Program.StartUtc.LocalDateTime.ToString("HH:mm"), program.Program.Title, program.Program.EpisodeTitle ?? "");
        }

        AnsiConsole.Write(table);

        if (programs.Length > 50)
        {
            AnsiConsole.MarkupLine($"[grey]Showing 50 of {programs.Length} programs[/]");
        }

        WaitForKey();
    }

    static void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "Unknown";

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[blue]About Lineup[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        table.AddRow("Version", version);
        table.AddRow("GitHub", "[link]https://github.com/am385/Lineup[/]");

        AnsiConsole.Write(table);
        WaitForKey();
    }

    static string FormatTimeSpan(TimeSpan span)
    {
        return $"{(int)span.TotalDays} days {span.Hours} hours";
    }

    static void WaitForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
}
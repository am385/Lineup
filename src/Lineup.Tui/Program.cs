using System.Reflection;
using Lineup.Core;
using Lineup.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spectre.Console;

namespace Lineup.Tui;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var deviceAddress = Environment.GetEnvironmentVariable(AppConstants.DeviceAddressEnvVar) ?? AppConstants.DefaultDeviceAddress;
        var databasePath = Environment.GetEnvironmentVariable(AppConstants.DatabasePathEnvVar);

        var builder = Host.CreateApplicationBuilder(args);
        
        // Configure Serilog from appsettings.json
        builder.Services.AddSerilog(config => config
            .ReadFrom.Configuration(builder.Configuration));
        
        builder.Services.AddEpgCore(deviceAddress, databasePath);

        using var host = builder.Build();

        var orchestrator = host.Services.GetRequiredService<EpgOrchestrator>();
        var repository = host.Services.GetRequiredService<IEpgRepository>();

        await repository.EnsureDatabaseCreatedAsync();


        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Lineup").Color(Color.Blue));

            await DisplayStatsAsync(repository);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold blue]What would you like to do?[/]")
                    .PageSize(10)
                    .AddChoices([
                        "View Cache Statistics",
                        "Fetch EPG Data",
                        "Generate XMLTV",
                        "Export XMLTV",
                        "View Channels",
                        "View Programs",
                        "About",
                        "Exit"
                    ]));

            switch (choice)
            {
                case "View Cache Statistics":
                    await ViewStatsAsync(repository);
                    break;
                case "Fetch EPG Data":
                    await FetchDataAsync(orchestrator);
                    break;
                case "Generate XMLTV":
                    await GenerateXmltvAsync(orchestrator);
                    break;
                case "Export XMLTV":
                    await ExportXmltvAsync();
                    break;
                case "View Channels":
                    await ViewChannelsAsync(repository);
                    break;
                case "View Programs":
                    await ViewProgramsAsync(repository);
                    break;
                case "About":
                    ShowAbout();
                    break;
                case "Exit":
                    return 0;
            }
        }
    }

    static async Task DisplayStatsAsync(IEpgRepository repository)
    {
        var stats = await repository.GetCacheStatisticsAsync();

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

    static async Task ViewStatsAsync(IEpgRepository repository)
    {
        var stats = await repository.GetCacheStatisticsAsync();
        var safeFetchStart = await repository.GetSafeFetchStartTimeAsync();

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[blue]Cache Statistics[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Channels", stats.ChannelCount.ToString());
        table.AddRow("Programs", stats.ProgramCount.ToString());
        table.AddRow("Earliest Program", stats.EarliestProgramStart?.ToString("yyyy-MM-dd HH:mm") ?? "N/A");
        table.AddRow("Latest Program", stats.LatestProgramEnd?.ToString("yyyy-MM-dd HH:mm") ?? "N/A");
        table.AddRow("Total Time Span", stats.TotalTimeSpan.HasValue ? FormatTimeSpan(stats.TotalTimeSpan.Value) : "N/A");
        table.AddRow("Safe Fetch Start", safeFetchStart?.ToString("yyyy-MM-dd HH:mm") ?? "N/A");

        if (safeFetchStart.HasValue && stats.LatestProgramEnd.HasValue && safeFetchStart < stats.LatestProgramEnd)
        {
            var gap = stats.LatestProgramEnd.Value - safeFetchStart.Value;
            table.AddRow("[yellow]Channel Gap[/]", $"[yellow]{FormatTimeSpan(gap)}[/]");
        }

        AnsiConsole.Write(table);
        WaitForKey();
    }

    static async Task FetchDataAsync(EpgOrchestrator orchestrator)
    {
        var force = AnsiConsole.Confirm("Force fetch (ignore existing data)?", false);
        var targetDays = AnsiConsole.Ask("Target days of data to fetch:", 3);

        AnsiConsole.WriteLine();
        
        var progressTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Status")
            .AddColumn("Fetch #")
            .AddColumn("Programs")
            .AddColumn("Channels")
            .AddColumn("Progress")
            .AddColumn("Message");

        await AnsiConsole.Live(progressTable)
            .StartAsync(async ctx =>
            {
                var progress = new Progress<FetchProgressInfo>(info =>
                {
                    progressTable.Rows.Clear();
                    
                    var statusColor = info.Status switch
                    {
                        FetchStatus.Initializing => "grey",
                        FetchStatus.Fetching => "blue",
                        FetchStatus.Storing => "yellow",
                        FetchStatus.Completed => "green",
                        FetchStatus.Failed => "red",
                        _ => "white"
                    };

                    var progressBar = new string('?', info.PercentComplete / 5) + 
                                     new string('?', 20 - info.PercentComplete / 5);

                    progressTable.AddRow(
                        $"[{statusColor}]{info.Status}[/]",
                        info.FetchCount.ToString(),
                        info.TotalProgramsFetched.ToString("N0"),
                        info.TotalChannelsFetched.ToString(),
                        $"[blue]{progressBar}[/] {info.PercentComplete}%",
                        info.Message.Length > 50 ? info.Message[..47] + "..." : info.Message
                    );

                    ctx.Refresh();
                });

                await orchestrator.FetchAndStoreEpgAsync(targetDays, force, progress);
            });

        AnsiConsole.MarkupLine("[green]EPG data fetched successfully![/]");
        WaitForKey();
    }

    static async Task GenerateXmltvAsync(EpgOrchestrator orchestrator)
    {
        var filename = AnsiConsole.Ask("Output filename:", AppConstants.DefaultXmltvFileName);
        var days = AnsiConsole.Ask("Number of days to include:", 7);

        await AnsiConsole.Status()
            .StartAsync($"Generating {filename}...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                await orchestrator.GenerateEpgFromCacheAsync(days, filename);
            });

        AnsiConsole.MarkupLine($"[green]Generated {filename} successfully![/]");
        WaitForKey();
    }

    static async Task ExportXmltvAsync()
    {
        var defaultSourceFile = Path.Combine(AppConstants.DefaultXmltvFilePath, AppConstants.DefaultXmltvFileName);
        
        if (!File.Exists(defaultSourceFile))
        {
            AnsiConsole.MarkupLine("[red]No XMLTV file found. Please generate one first.[/]");
            WaitForKey();
            return;
        }

        var fileInfo = new FileInfo(defaultSourceFile);
        AnsiConsole.MarkupLine($"[grey]Source file: {defaultSourceFile} ({fileInfo.Length / 1024:N0} KB, modified {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm})[/]");

        var destination = AnsiConsole.Ask("Export to path:", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppConstants.DefaultXmltvFileName));

        try
        {
            var destinationDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            await Task.Run(() => File.Copy(defaultSourceFile, destination, overwrite: true));
            
            AnsiConsole.MarkupLine($"[green]Exported to {destination} successfully![/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error exporting file: {ex.Message}[/]");
        }

        WaitForKey();
    }

    static async Task ViewChannelsAsync(IEpgRepository repository)
    {
        var channels = await repository.GetChannelsAsync();

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[blue]Channels ({channels.Count})[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Number")
            .AddColumn("Name")
            .AddColumn("Affiliate");

        foreach (var channel in channels.OrderBy(c => c.GuideNumber))
        {
            table.AddRow(
                channel.GuideNumber ?? "",
                channel.GuideName ?? "",
                channel.Affiliate ?? "");
        }

        AnsiConsole.Write(table);
        WaitForKey();
    }

    static async Task ViewProgramsAsync(IEpgRepository repository)
    {
        var programs = await repository.GetProgramsAsync(DateTime.UtcNow, DateTime.UtcNow.AddHours(6));

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[blue]Programs - Next 6 Hours ({programs.Count})[/]"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Channel")
            .AddColumn("Time")
            .AddColumn("Title")
            .AddColumn("Episode");

        foreach (var program in programs.Take(50))
        {
            var startTime = DateTimeOffset.FromUnixTimeSeconds(program.StartTime).LocalDateTime;
            table.AddRow(
                program.GuideNumber ?? "",
                startTime.ToString("HH:mm"),
                program.Title ?? "",
                program.EpisodeTitle ?? "");
        }

        AnsiConsole.Write(table);

        if (programs.Count > 50)
        {
            AnsiConsole.MarkupLine($"[grey]Showing 50 of {programs.Count} programs[/]");
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
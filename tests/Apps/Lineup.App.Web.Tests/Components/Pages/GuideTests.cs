using Bunit;
using Lineup.App.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Globalization;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>Verifies guide page presentation behavior.</summary>
public sealed class GuideTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    /// <summary>Restores the test's original formatting culture.</summary>
    public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;

    /// <summary>Verifies excluded sources remain visible beside healthy rows and when no channel data is available.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialGuide_ShowsSourceWarningEvenWhenEmpty(bool empty)
    {
        // Arrange
        var reader = Substitute.For<ILineupGuideReader>();
        var sourceId = GuideSourceId.New();
        reader.GetChannelDirectoryAsync(Arg.Any<CancellationToken>())
            .Returns(empty ? [] : [new GuideChannelSegment { GuideNumber = "2", GuideName = "Healthy channel" }]);
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetSourceDiagnostics().Returns(
        [
            new GuideSourceDiagnostic
            {
                SourceId = sourceId,
                SourceName = "Missing source",
                Code = GuideSourceDiagnosticCode.AdapterUnavailable,
                Message = "Install the source plugin."
            }
        ]);
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var warning = component.Find("[data-guide-source-warnings]");
            Assert.Contains("Partial guide results", warning.TextContent, StringComparison.Ordinal);
            Assert.Contains("Missing source: Install the source plugin.", warning.TextContent, StringComparison.Ordinal);
            Assert.Equal(sourceId.ToString(), component.Find("[data-guide-source-id]").GetAttribute("data-guide-source-id"));
            Assert.Equal(empty ? 0 : 1, component.FindAll(".channel-number").Count);
        }, timeout: LineupWebBunitContext.WaitTimeout);
        await reader.DidNotReceive().GetStatisticsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies channel rows are ordered by numeric channel components instead of alphabetically.</summary>
    [Fact]
    public async Task ChannelRows_MixedChannelNumbers_OrdersByNumericValue()
    {
        // Arrange
        var reader = Substitute.For<ILineupGuideReader>();
        reader.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new GuideChannelSegment { GuideNumber = "100.1", GuideName = "Hundred" },
            new GuideChannelSegment { GuideNumber = "10.1", GuideName = "Ten" },
            new GuideChannelSegment { GuideNumber = "7.2", GuideName = "Seven Two" },
            new GuideChannelSegment { GuideNumber = "2.2", GuideName = "Two Two" },
            new GuideChannelSegment { GuideNumber = "7.10", GuideName = "Seven Ten" },
            new GuideChannelSegment { GuideNumber = "2", GuideName = "Two" }
        ]);
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
            Assert.Equal(
                ["2", "2.2", "7.10", "7.2", "10.1", "100.1"],
                component.FindAll(".channel-number").Select(element => element.TextContent.Trim())), timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies navigation cannot publish older programs or clear a newer request's loading indicator.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Navigation_OverlappingLoads_OnlyNewestRequestCommits(bool olderCompletesFirst, bool staleFailure)
    {
        // Arrange
        var reader = CreateReader();
        var older = new TaskCompletionSource<IReadOnlyList<GuideCacheProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<IReadOnlyList<GuideCacheProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<(DateTimeOffset Start, CancellationToken Token)>();
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            requests.Add((call.ArgAt<DateTimeOffset?>(0)!.Value, call.Arg<CancellationToken>()));
            return requests.Count switch
            {
                1 => Task.FromResult<IReadOnlyList<GuideCacheProgram>>([]),
                2 => older.Task,
                _ => newer.Task
            };
        });
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        var component = context.Render<Guide>();
        component.WaitForAssertion(() => Assert.Single(requests), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        var olderNavigation = component.FindAll("button.btn-outline-primary")[1].ClickAsync(new());
        component.WaitForAssertion(() => Assert.Equal(2, requests.Count), timeout: LineupWebBunitContext.WaitTimeout);
        var newerNavigation = component.FindAll("button.btn-outline-primary")[2].ClickAsync(new());
        component.WaitForAssertion(() => Assert.Equal(3, requests.Count), timeout: LineupWebBunitContext.WaitTimeout);
        if (olderCompletesFirst)
        {
            older.SetResult([CreateProgram("Older", requests[1].Start)]);
            await olderNavigation;
            component.WaitForAssertion(() => Assert.Single(component.FindAll(".spinner-border")), timeout: LineupWebBunitContext.WaitTimeout);
        }
        newer.SetResult([CreateProgram("Newest", requests[2].Start)]);
        await newerNavigation;
        if (!olderCompletesFirst)
        {
            if (staleFailure)
            {
                older.SetException(new InvalidOperationException("Obsolete query failed."));
            }
            else
            {
                older.SetResult([CreateProgram("Older", requests[1].Start)]);
            }
            await olderNavigation;
        }

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal("Newest", component.Find(".program-title").TextContent.Trim());
            Assert.Empty(component.FindAll(".spinner-border"));
            Assert.Empty(component.FindAll("[role='alert']"));
            Assert.Contains("Wed", component.Find("button.active").TextContent, StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
        Assert.True(requests[1].Token.IsCancellationRequested);
    }

    /// <summary>Verifies DST transitions retain real half-hour slots and elapsed program widths and durations.</summary>
    [Theory]
    [InlineData(3, 8, 5, 1080, 8, 236)]
    [InlineData(11, 1, 4, 1080, 8, 236)]
    [InlineData(3, 8, 5, 600, 4, 116)]
    public async Task Timeline_DaylightSavingTransition_UsesElapsedTime(int month, int day, int utcHour, int width, int slots, int programWidth)
    {
        // Arrange
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var midnight = new DateTimeOffset(2026, month, day, utcHour, 0, 0, TimeSpan.Zero);
        var reader = CreateReader();
        DateTimeOffset? queryStart = null;
        DateTimeOffset? queryEnd = null;
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            queryStart = call.ArgAt<DateTimeOffset?>(0);
            queryEnd = call.ArgAt<DateTimeOffset?>(1);
            return new[] { CreateProgram("Transition", midnight.AddMinutes(90)) };
        });
        await using var context = CreateContext(reader, zone, midnight, width);

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(midnight, queryStart);
            Assert.Equal(midnight.AddMinutes(slots * 30), queryEnd);
            var labels = component.FindAll(".guide-time-slot").Select(slot => slot.TextContent.Trim()).ToArray();
            Assert.Equal(slots, labels.Length);
            if (month == 3)
            {
                Assert.DoesNotContain(labels, label => label.StartsWith("2:", StringComparison.Ordinal));
                if (slots == 8)
                {
                    Assert.Contains("3:00 AM", labels);
                }
            }
            else
            {
                Assert.Contains("1:00 AM (UTC-04:00)", labels);
                Assert.Contains("1:00 AM (UTC-05:00)", labels);
            }
            var program = component.Find(".guide-program");
            Assert.Contains($"left: 360px; width: {programWidth}px;", program.GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("(60 min)", program.GetAttribute("title"), StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
        await component.Find(".guide-program").ClickAsync(new());
        if (month == 11)
        {
            var details = component.Find(".modal-body").TextContent;
            Assert.Contains("1:30 AM (UTC-04:00)", details, StringComparison.Ordinal);
            Assert.Contains("1:30 AM (UTC-05:00)", details, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies narrow current-time windows preserve the actual occurrence and local half-hour alignment.</summary>
    [Theory]
    [InlineData("America/New_York", 3, 8, 7, 15, 7, 0, "3:00 AM")]
    [InlineData("America/New_York", 11, 1, 6, 45, 6, 30, "1:30 AM (UTC-05:00)")]
    [InlineData("Asia/Kathmandu", 10, 5, 8, 0, 7, 45, "1:30 PM")]
    public async Task Timeline_NarrowCurrentWindow_KeepsNowVisible(string zoneId, int month, int day, int nowHour, int nowMinute, int startHour, int startMinute, string firstLabel)
    {
        // Arrange
        var reader = CreateReader();
        var now = new DateTimeOffset(2026, month, day, nowHour, nowMinute, 0, TimeSpan.Zero);
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            start = call.ArgAt<DateTimeOffset?>(0);
            end = call.ArgAt<DateTimeOffset?>(1);
            return Array.Empty<GuideCacheProgram>();
        });
        await using var context = CreateContext(reader, TimeZoneInfo.FindSystemTimeZoneById(zoneId), now, 360);

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(new DateTimeOffset(2026, month, day, startHour, startMinute, 0, TimeSpan.Zero), start);
            Assert.True(start <= now && now < end);
            Assert.Equal(TimeSpan.FromHours(1), end - start);
            Assert.Equal(firstLabel, component.FindAll(".guide-time-slot")[0].TextContent.Trim());
            Assert.True(component.FindAll("button.btn-outline-primary")[7].HasAttribute("disabled"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies resizing and directory reads use the same latest-request guard as navigation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resize_PendingNavigation_DiscardsObsoleteDirectoryAndPrograms(bool blockDirectory)
    {
        // Arrange
        var reader = CreateReader();
        var directory = new TaskCompletionSource<IReadOnlyList<GuideChannelSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var programs = new TaskCompletionSource<IReadOnlyList<GuideCacheProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directoryCalls = 0;
        var programCalls = 0;
        reader.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            ++directoryCalls == 2 && blockDirectory
                ? directory.Task
                : Task.FromResult<IReadOnlyList<GuideChannelSegment>>([new GuideChannelSegment { GuideNumber = "2", GuideName = "Current" }]));
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            programCalls++;
            var start = call.ArgAt<DateTimeOffset?>(0)!.Value;
            var end = call.ArgAt<DateTimeOffset?>(1)!.Value;
            return (end - start).TotalHours == 2.5
                ? Task.FromResult<IReadOnlyList<GuideCacheProgram>>([CreateProgram("Resized", start)])
                : programCalls == 1 ? Task.FromResult<IReadOnlyList<GuideCacheProgram>>([]) : programs.Task;
        });
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        var component = context.Render<Guide>();
        component.WaitForAssertion(() => Assert.Equal(1, programCalls), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        var navigation = component.FindAll("button.btn-outline-primary")[1].ClickAsync(new());
        component.WaitForAssertion(() => Assert.Equal(2, directoryCalls), timeout: LineupWebBunitContext.WaitTimeout);
        await component.InvokeAsync(() => component.Instance.OnContainerResize(720));
        directory.SetResult([new GuideChannelSegment { GuideNumber = "99", GuideName = "Obsolete" }]);
        programs.SetResult([]);
        await navigation;

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(5, component.FindAll(".guide-time-slot").Count);
            Assert.Equal("Resized", component.Find(".program-title").TextContent.Trim());
            Assert.Equal("2", component.Find(".channel-number").TextContent.Trim());
            Assert.Empty(component.FindAll(".spinner-border"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
        Assert.Equal(blockDirectory ? 2 : 3, programCalls);
    }

    /// <summary>Verifies closing the page cancels pending storage work and rejects late completions.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_PendingRead_CancelsWithoutPublishing(bool blockDirectory)
    {
        // Arrange
        var reader = CreateReader();
        var directory = new TaskCompletionSource<IReadOnlyList<GuideChannelSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var programs = new TaskCompletionSource<IReadOnlyList<GuideCacheProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directoryCalls = 0;
        var programCalls = 0;
        CancellationToken pendingToken = default;
        reader.GetChannelDirectoryAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (++directoryCalls == 2)
            {
                pendingToken = call.Arg<CancellationToken>();
                if (blockDirectory)
                {
                    return directory.Task;
                }
            }
            return Task.FromResult<IReadOnlyList<GuideChannelSegment>>([new GuideChannelSegment { GuideNumber = "2", GuideName = "Current" }]);
        });
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++programCalls == 1 ? Task.FromResult<IReadOnlyList<GuideCacheProgram>>([]) : programs.Task);
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        var component = context.Render<Guide>();
        component.WaitForAssertion(() => Assert.Equal(1, programCalls), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        var navigation = component.FindAll("button.btn-outline-primary")[1].ClickAsync(new());
        component.WaitForAssertion(() => Assert.Equal(2, directoryCalls), timeout: LineupWebBunitContext.WaitTimeout);
        await component.InvokeAsync(async () => await component.Instance.DisposeAsync());
        directory.SetResult([]);
        programs.SetResult([]);
        await navigation;

        // Assert
        Assert.True(pendingToken.IsCancellationRequested);
        Assert.Equal(blockDirectory ? 1 : 2, programCalls);
        reader.Received(1).GetSourceDiagnostics();
    }

    /// <summary>Verifies short and long local days and odd slot counts remain bounded by the correct UTC midnights.</summary>
    [Theory]
    [InlineData(3, 8, 5, 23, 720)]
    [InlineData(11, 1, 4, 25, 1080)]
    public async Task Navigation_DaylightSavingDay_RespectsLocalDateBounds(int month, int day, int utcHour, int dayHours, int width)
    {
        // Arrange
        var reader = CreateReader();
        var midnight = new DateTimeOffset(2026, month, day, utcHour, 0, 0, TimeSpan.Zero);
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            start = call.ArgAt<DateTimeOffset?>(0);
            end = call.ArgAt<DateTimeOffset?>(1);
            return Array.Empty<GuideCacheProgram>();
        });
        await using var context = CreateContext(reader, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), midnight, width);
        var component = context.Render<Guide>();
        component.WaitForAssertion(() => Assert.Equal(midnight, start), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        for (var step = 0; step < 12 && !component.FindAll("button.btn-outline-secondary")[1].HasAttribute("disabled"); step++)
        {
            await component.FindAll("button.btn-outline-secondary")[1].ClickAsync(new());
        }

        // Assert
        Assert.True(component.FindAll("button.btn-outline-secondary")[1].HasAttribute("disabled"));
        Assert.Equal(midnight.AddHours(dayHours), end);
        Assert.Equal(width == 720 ? TimeSpan.FromHours(2.5) : TimeSpan.FromHours(4), end - start);
        for (var step = 0; step < 12 && !component.FindAll("button.btn-outline-secondary")[0].HasAttribute("disabled"); step++)
        {
            await component.FindAll("button.btn-outline-secondary")[0].ClickAsync(new());
        }
        Assert.True(component.FindAll("button.btn-outline-secondary")[0].HasAttribute("disabled"));
        Assert.Equal(midnight, start);
    }

    /// <summary>Verifies the first viewport measurement cannot reset a date already selected by the user.</summary>
    [Fact]
    public async Task Initialization_LateViewportMeasurement_PreservesNewerNavigation()
    {
        // Arrange
        var reader = CreateReader();
        var starts = new List<DateTimeOffset?>();
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            starts.Add(call.ArgAt<DateTimeOffset?>(0));
            return Array.Empty<GuideCacheProgram>();
        });
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), null);
        var viewport = context.JSInterop.Setup<int>("getGuideViewportWidth");
        var component = context.Render<Guide>();

        // Act
        await component.FindAll("button.btn-outline-primary")[1].ClickAsync(new());
        viewport.SetResult(1080);

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(2, starts.Count);
            Assert.All(starts, start => Assert.Equal(new DateTimeOffset(2026, 10, 6, 6, 0, 0, TimeSpan.Zero), start));
            Assert.Contains("Tomorrow", component.Find("button.active").TextContent, StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies a failed current read is explicit and can be retried without displaying old-window data.</summary>
    [Fact]
    public async Task Navigation_CurrentReadFails_ShowsErrorAndAllowsRetry()
    {
        // Arrange
        var reader = CreateReader();
        var calls = 0;
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (++calls == 2)
            {
                throw new InvalidDataException("Storage read failed.");
            }
            return new[] { CreateProgram("Current", call.ArgAt<DateTimeOffset?>(0)!.Value) };
        });
        await using var context = CreateContext(reader, TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero));
        var component = context.Render<Guide>();
        component.WaitForAssertion(() => Assert.Single(component.FindAll(".guide-program")), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.FindAll("button.btn-outline-primary")[1].ClickAsync(new());

        // Assert
        Assert.Single(component.FindAll("[role='alert']"));
        Assert.Empty(component.FindAll(".guide-program"));
        Assert.DoesNotContain("Storage read failed", component.Markup, StringComparison.Ordinal);
        await component.Find("[role='alert'] button").ClickAsync(new());
        Assert.Empty(component.FindAll("[role='alert']"));
        Assert.Equal("Current", component.Find(".program-title").TextContent.Trim());
    }

    private static ILineupGuideReader CreateReader()
    {
        var reader = Substitute.For<ILineupGuideReader>();
        reader.GetChannelDirectoryAsync(Arg.Any<CancellationToken>())
            .Returns([new GuideChannelSegment { GuideNumber = "2", GuideName = "Channel" }]);
        reader.GetProgramsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetSourceDiagnostics().Returns([]);
        return reader;
    }

    private static BunitContext CreateContext(ILineupGuideReader reader, TimeZoneInfo zone, DateTimeOffset now, int? width = 1080)
    {
        var timeZone = Substitute.For<ITimeZoneService>();
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        timeZone.TimeZone.Returns(zone);
        timeZone.Today.Returns(local.Date);
        timeZone.Now.Returns(local);
        timeZone.ConvertToUtc(Arg.Any<DateTime>()).Returns(call =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(call.Arg<DateTime>(), DateTimeKind.Unspecified), zone));
        timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(call =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(call.Arg<DateTime>(), DateTimeKind.Utc), zone));
        var context = new BunitContext();
        context.Services.AddSingleton(reader);
        context.Services.AddSingleton(timeZone);
        context.Services.AddSingleton<TimeProvider>(new GuideClock(now));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        if (width.HasValue)
        {
            context.JSInterop.Setup<int>("getGuideViewportWidth").SetResult(width.Value);
        }
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        return context;
    }

    private static GuideCacheProgram CreateProgram(string title, DateTimeOffset start) => new()
    {
        GuideNumber = "2",
        Title = title,
        StartTime = start.ToUnixTimeSeconds(),
        EndTime = start.AddHours(1).ToUnixTimeSeconds()
    };

    private sealed class GuideClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }
}

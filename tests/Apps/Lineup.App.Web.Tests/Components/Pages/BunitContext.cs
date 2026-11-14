using Lineup.Plugin.GuideOutputs.Json.Plugin;
using Lineup.App.Web.Services;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;

namespace Lineup.App.Web.Tests;

/// <summary>
/// Provides page component tests with services normally supplied by the application host.
/// </summary>
internal sealed class LineupWebBunitContext : Bunit.BunitContext
{
    /// <summary>Gets the explicit watchdog budget for asynchronous component observations.</summary>
    public static TimeSpan WaitTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new page component test context.
    /// </summary>
    public LineupWebBunitContext()
    {
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(new LineupStartupState(LineupStartupStatus.Ready));
        Services.AddSingleton<IStatusNotificationService, StatusNotificationService>();
        Services.AddSingleton<IPhysicalTunerDriver, Lineup.App.Web.Tests.TestPhysicalTunerDriver>();
        Services.AddSingleton<IGuideSourceDriver, Lineup.App.Web.Tests.TestGuideSourceDriver>();
        Services.AddSingleton<IVirtualTunerDriver, HdHomeRunVirtualTunerDriver>();
        Services.AddSingleton<IVirtualTunerDriver, WebPlayerVirtualTunerDriver>();
        Services.AddJsonGuideOutput();
        Services.AddSingleton<IGuideOutputDriver, XmltvGuideOutputDriver>();
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            await DisposeComponentsAsync();
            if (Renderer.UnhandledException.IsCompletedSuccessfully)
            {
                ExceptionDispatchInfo.Capture(await Renderer.UnhandledException).Throw();
            }
        }
        finally
        {
            await base.DisposeAsyncCore();
        }
    }
}

using Lineup.Plugin.GuideOutputs.Json.Plugin;
using Lineup.App.Web.Services;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Lineup.App.Web.Tests;

/// <summary>
/// Provides page component tests with services normally supplied by the application host.
/// </summary>
internal sealed class LineupWebBunitContext : Bunit.BunitContext
{
    /// <summary>
    /// Initializes a new page component test context.
    /// </summary>
    public LineupWebBunitContext()
    {
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        Services.AddSingleton(new LineupStartupState(LineupStartupStatus.Ready));
        Services.AddSingleton<IStatusNotificationService, StatusNotificationService>();
        Services.AddSingleton<IPhysicalTunerDriver, Lineup.App.Web.Tests.TestPhysicalTunerDriver>();
        Services.AddSingleton<IGuideSourceDriver, Lineup.App.Web.Tests.TestGuideSourceDriver>();
        Services.AddSingleton<IVirtualTunerDriver, HdHomeRunVirtualTunerDriver>();
        Services.AddSingleton<IVirtualTunerDriver, WebPlayerVirtualTunerDriver>();
        Services.AddJsonGuideOutput();
        Services.AddSingleton<IGuideOutputDriver, XmltvGuideOutputDriver>();
    }

}

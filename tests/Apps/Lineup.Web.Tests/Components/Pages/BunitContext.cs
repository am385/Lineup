using Lineup.GuideOutputs.Json.Plugin;
using Lineup.Web.Services;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Provides page component tests with services normally supplied by the application host.
/// </summary>
internal sealed class BunitContext : Bunit.BunitContext
{
    /// <summary>
    /// Initializes a new page component test context.
    /// </summary>
    public BunitContext()
    {
        Services.AddSingleton(new LineupStartupState(LineupStartupStatus.Ready));
        Services.AddSingleton<IStatusNotificationService, StatusNotificationService>();
        Services.AddSingleton<IPhysicalTunerDriver, Lineup.Web.Tests.TestPhysicalTunerDriver>();
        Services.AddSingleton<IGuideSourceDriver, Lineup.Web.Tests.TestGuideSourceDriver>();
        Services.AddSingleton<IVirtualTunerDriver, HdHomeRunVirtualTunerDriver>();
        Services.AddSingleton<IVirtualTunerDriver, WebPlayerVirtualTunerDriver>();
        Services.AddJsonGuideOutput();
        Services.AddSingleton<IGuideOutputDriver, XmltvGuideOutputDriver>();
    }

}

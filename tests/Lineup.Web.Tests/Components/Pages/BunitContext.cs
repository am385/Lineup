using Lineup.Web.Services;
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
        Services.AddSingleton<IStatusNotificationService, StatusNotificationService>();
    }
}

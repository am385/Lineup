using Microsoft.AspNetCore.Components;
using SettingsPage = Lineup.Web.Components.Pages.Settings;

namespace Lineup.Web.Components.Settings;

/// <summary>
/// Edits physical tuner devices and global source-stage settings.
/// </summary>
public partial class PhysicalDevicesSettingsTab
{
    /// <summary>Gets or sets the owning settings editor.</summary>
    [Parameter, EditorRequired]
    public SettingsPage Host { get; set; } = default!;

    /// <summary>Gets or sets the tab visibility class.</summary>
    [Parameter]
    public string CssClass { get; set; } = string.Empty;
}

using Microsoft.AspNetCore.Components;
using SettingsPage = Lineup.Web.Components.Pages.Settings;

namespace Lineup.Web.Components.Settings;

/// <summary>
/// Edits guide sources and global guide publication settings.
/// </summary>
public partial class GuideSourcesSettingsTab
{
    /// <summary>Gets or sets the owning settings editor.</summary>
    [Parameter, EditorRequired]
    public SettingsPage Host { get; set; } = default!;

    /// <summary>Gets or sets the tab visibility class.</summary>
    [Parameter]
    public string CssClass { get; set; } = string.Empty;
}

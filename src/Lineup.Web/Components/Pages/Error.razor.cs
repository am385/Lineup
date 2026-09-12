using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Represents error.
/// </summary>
public partial class Error
{
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    private string? RequestId { get; set; }
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>
    /// Performs the on initialized operation.
    /// </summary>
    protected override void OnInitialized() =>
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
}

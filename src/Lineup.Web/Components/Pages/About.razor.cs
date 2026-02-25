using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

public partial class About
{
    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private string _version = "Unknown";
    private string _xmltvUrl = "";
    private bool _copied;

    protected override void OnInitialized()
    {
        _version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "Unknown";

        _xmltvUrl = Navigation.BaseUri.TrimEnd('/') + "/api/xmltv";
    }

    private async Task CopyXmltvUrl()
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", _xmltvUrl);
        _copied = true;
        StateHasChanged();

        await Task.Delay(2000);
        _copied = false;
        StateHasChanged();
    }
}

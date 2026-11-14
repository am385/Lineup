using Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime;

namespace Lineup.App.Web.Services;

/// <summary>Adapts the Web host's discovery preference to the shared provider discovery runtime.</summary>
public sealed class WebHdHomeRunDiscoveryPolicy : IHdHomeRunDiscoveryPolicy
{
    private readonly IWebSettingsService _settings;

    /// <summary>Initializes the Web discovery policy adapter.</summary>
    public WebHdHomeRunDiscoveryPolicy(IWebSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Gets the enabled.</summary>
    public bool Enabled => _settings.Settings.EnableNetworkDiscovery;

    /// <summary>Provides the changed.</summary>
    public event Action? Changed
    {
        add => _settings.OnSettingsChanged += value;
        remove => _settings.OnSettingsChanged -= value;
    }
}

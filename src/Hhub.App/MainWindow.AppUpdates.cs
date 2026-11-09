using System.Diagnostics;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    // A future release-source adapter supplies real offers. No source is configured yet.
    private sealed record AppUpdateOffer(Version Version, string Channel, Uri DownloadPage);
    private AppUpdateOffer? _appUpdateOffer;
    private readonly Button _appUpdateIndicator = new()
    {
        Content = "NEW", FontSize = 10, MinWidth = 0, MinHeight = 0, Height = 22,
        Padding = new(6, 0, 6, 0), Margin = new(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
    };

    private void SetAppUpdateOffer(AppUpdateOffer? offer)
    {
        _appUpdateOffer = offer is not null && offer.Version > PluginCompatibility.HostVersion
            && offer.Channel == _preferences.UpdateChannel && offer.DownloadPage.Scheme == "https"
            && offer.DownloadPage.UserInfo.Length == 0 ? offer : null;
        RefreshAppUpdateIndicator();
    }

    private void RefreshAppUpdateIndicator()
    {
        _appUpdateIndicator.Visibility = !_preferences.SidebarCollapsed && _appUpdateOffer is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_appUpdateIndicator, _appUpdateOffer is { } offer ? "更新到 " + offer.Version : null);
    }

    private void RenderAppUpdateSettings(Panel panel)
    {
        panel.Children.Add(Text("软件更新", 24, true));
        if (_appUpdateOffer is not { } offer)
        {
            panel.Children.Add(Text("当前版本 " + PluginCompatibility.HostVersion));
            panel.Children.Add(SettingsMutedText("尚未配置更新源"));
            return;
        }
        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(Text($"{PluginCompatibility.HostVersion} → {offer.Version}", 20, true));
        details.Children.Add(SettingsMutedText("从当前版本更新到 " + offer.Version));
        var update = Button("更新到 " + offer.Version, () => Process.Start(new ProcessStartInfo(offer.DownloadPage.AbsoluteUri)
        { UseShellExecute = true }), true);
        update.HorizontalAlignment = HorizontalAlignment.Left;
        details.Children.Add(update);
        panel.Children.Add(Card(details, padding: new(16)));
    }
}

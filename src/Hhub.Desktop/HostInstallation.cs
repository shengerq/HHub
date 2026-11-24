using Hhub.Desktop.Pages;

namespace Hhub.Desktop;

internal static class HostInstallation
{
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "hhub-installed.marker"));
    public static string TrayWindowClass => IsInstalled ? "Hhub.Rebuild.Tray.Installed" : "Hhub.Rebuild.Tray";

    // Publisher keys used by previously distributed signed plugins and their standalone packages.
    public static PluginLab LoadPlugins() => new(Path.Combine(PluginPreferences.Root, "installed-plugins"), [],
        "97B2D696AAD22003AA5B4E788DC172A5D84675B6B53B65D423E7FC5C0522F207",
        ["C4A3F00FF1A966CFD80D9AF535757E1980A22AF338632CCB804FD84F4DA4E307",
            "C66C8C4C25BC2040053AD251A6B8250C702346AAF2ACD754EC2FE1ACEB16766D"]);
}

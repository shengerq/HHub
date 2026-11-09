using Microsoft.Win32;

namespace Hhub.Core;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "Hhub")
{
    public static string Command(string executable) => "\"" + Path.GetFullPath(executable) + "\" --startup";
    public bool IsEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue(valueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase);
    }
    public void SetEnabled(string executable, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled) key.SetValue(valueName, Command(executable), RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

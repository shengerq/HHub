using Microsoft.Win32;

namespace Hhub.Core;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string keyPath, string valueName, string argument)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupRegistration(string keyPath = RunKey, string valueName = "Hhub") : this(keyPath, valueName, "--startup") { }

    public static string Command(string executable) => Command(executable, "--startup");

    private static string Command(string executable, string argument) =>
        "\"" + Path.GetFullPath(executable) + "\" " + argument;
    public bool IsEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue(valueName) as string, Command(executable, argument), StringComparison.OrdinalIgnoreCase);
    }
    public void SetEnabled(string executable, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled) key.SetValue(valueName, Command(executable, argument), RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

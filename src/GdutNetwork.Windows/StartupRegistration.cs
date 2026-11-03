using Microsoft.Win32;

namespace GdutNetwork.Windows;

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "GDUTAutoNetwork";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(Entry) is string value && value == Command();
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled) key.SetValue(Entry, Command(), RegistryValueKind.String);
        else key.DeleteValue(Entry, throwOnMissingValue: false);
    }
    private static string Command() => $"\"{Path.Combine(AppContext.BaseDirectory, "GdutNetwork.exe")}\" --background";
}

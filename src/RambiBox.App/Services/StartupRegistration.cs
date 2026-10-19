using Microsoft.Win32;
using RambiBox.Core;

namespace RambiBox.App.Services;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RambiBox.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RambiBox";

    public static string Command => $"\"{Environment.ProcessPath}\" --tray";

    /// <summary>Creates, updates or removes the Run entry so it matches <paramref name="enabled"/>.</summary>
    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command);
                    Log.Info($"Start with Windows enabled: {Command}");
                }
            }
            else if (current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Start with Windows disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update the Windows startup entry: {ex.Message}");
        }
    }
}

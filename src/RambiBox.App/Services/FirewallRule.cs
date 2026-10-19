using System.ComponentModel;
using System.Diagnostics;
using RambiBox.Core;
using RambiBox.Core.Calibration;

namespace RambiBox.App.Services;

/// <summary>
/// Optional inbound Windows Firewall rule for the phone calibration web server (TCP 47800–47820, private networks,
/// this exe only). Without it Windows shows a one-time "allow access" prompt when the calibration starts.
/// </summary>
public static class FirewallRule
{
    public const string RuleName = "RambiBox phone calibration";

    /// <summary>True when the rule exists (non-elevated query, hidden).</summary>
    public static Task<bool> ExistsAsync() => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Firewall rule check failed: {ex.Message}");
            return false;
        }
    });

    /// <summary>
    /// Adds (replaces) the rule in an elevated, hidden cmd. Returns true on success, false on failure,
    /// null when the user cancelled the UAC prompt.
    /// </summary>
    public static Task<bool?> AddAsync() => Task.Run<bool?>(() =>
    {
        string program = Environment.ProcessPath ?? "";
        string ports = $"{PhoneCalibrationService.FirstPort}-{PhoneCalibrationService.LastPort}";
        string cmd =
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
            $"localport={ports} profile=private program=\"{program}\"";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(30_000)) return false;
            Log.Info($"Firewall rule '{RuleName}' added (exit code {p.ExitCode})");
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Firewall rule: the user cancelled the administrator prompt");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Adding the firewall rule failed: {ex.Message}");
            return false;
        }
    });
}

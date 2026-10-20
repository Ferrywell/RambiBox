using System.Diagnostics;

namespace RambiBox.Core.Updates;

/// <summary>File operations the installer needs (faked in tests).</summary>
public interface IUpdateFileSystem
{
    bool Exists(string path);
    void Move(string from, string to);
    void Copy(string from, string to);
    void Delete(string path);
}

/// <summary>A started process (faked in tests).</summary>
public interface IUpdateProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

public interface IUpdateProcessLauncher
{
    /// <summary>Starts <paramref name="exePath"/>; throws when it can't.</summary>
    IUpdateProcess Start(string exePath, IReadOnlyList<string> args);
}

public enum HandshakeOutcome { Ok, ProcessExited, TimedOut }

/// <summary>"The new version started fine": created before the new process starts, signalled by it.</summary>
public interface IUpdateHandshake : IDisposable
{
    HandshakeOutcome Wait(IUpdateProcess process, TimeSpan timeout);
}

public interface IUpdateHandshakeFactory
{
    IUpdateHandshake Create(string token);
}

/// <param name="CurrentExe">The running exe (%LocalAppData%\Programs\RambiBox\RambiBox.exe).</param>
/// <param name="NewExe">The downloaded, verified exe.</param>
/// <param name="StartInTray">The window isn't open: the new version starts in the tray too.</param>
/// <param name="Token">Names the handshake event (a GUID).</param>
public sealed record InstallRequest(string CurrentExe, string NewExe, SemVersion OldVersion, SemVersion NewVersion, bool StartInTray, string Token);

public enum InstallOutcome
{
    /// <summary>The new version runs and reported a healthy start: this process should exit now.</summary>
    Installed,
    /// <summary>Nothing was changed (e.g. the exe couldn't be renamed).</summary>
    NotStarted,
    /// <summary>The swap was undone; the old exe is back in place.</summary>
    RolledBack,
    /// <summary>The swap could not be undone completely (see <see cref="InstallResult.Detail"/> and the log).</summary>
    RollbackFailed,
}

/// <param name="RelaunchPath">After a failure: the exe to restart (the old version).</param>
public sealed record InstallResult(InstallOutcome Outcome, string Detail, string RelaunchPath)
{
    public bool Installed => Outcome == InstallOutcome.Installed;
}

/// <summary>
/// Swaps the running exe for a verified new one and makes sure the new one works, otherwise puts the old one back:
/// <list type="number">
/// <item>rename the running RambiBox.exe → RambiBox.exe.old (allowed for a running exe on NTFS);</item>
/// <item>copy the new exe → RambiBox.exe;</item>
/// <item>start it with <c>--updated-from &lt;old&gt; --update-token &lt;guid&gt;</c> (+ <c>--tray</c>);</item>
/// <item>wait (≤ 45 s) until it signals a healthy start; if it exits or never signals: kill it, delete the new exe,
/// rename .old back, and report the failure (the caller restarts the old version).</item>
/// </list>
/// The caller must have stopped the light engine (COM port free) and released the single-instance mutex first.
/// </summary>
public sealed class UpdateInstaller
{
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(45);

    private readonly IUpdateFileSystem _fs;
    private readonly IUpdateProcessLauncher _launcher;
    private readonly IUpdateHandshakeFactory _handshakes;
    private readonly Action<TimeSpan> _sleep;

    public TimeSpan HandshakeTimeout { get; init; } = DefaultHandshakeTimeout;

    public UpdateInstaller(IUpdateFileSystem fs, IUpdateProcessLauncher launcher, IUpdateHandshakeFactory handshakes, Action<TimeSpan>? sleep = null)
    {
        _fs = fs;
        _launcher = launcher;
        _handshakes = handshakes;
        _sleep = sleep ?? Thread.Sleep;
    }

    public static string OldPath(string currentExe) => currentExe + ".old";

    /// <summary>Command line of the new version.</summary>
    public static IReadOnlyList<string> NewVersionArgs(InstallRequest r)
    {
        var args = new List<string> { "--updated-from", r.OldVersion.ToString(), "--update-token", r.Token };
        if (r.StartInTray) args.Add("--tray");
        return args;
    }

    /// <summary>Command line of the old version restarted after a failed update.</summary>
    public static IReadOnlyList<string> FailedArgs(InstallRequest r)
    {
        var args = new List<string> { "--update-failed", r.NewVersion.ToString() };
        if (r.StartInTray) args.Add("--tray");
        return args;
    }

    public InstallResult Install(InstallRequest r)
    {
        var old = OldPath(r.CurrentExe);
        Log.Info($"Installing update {r.OldVersion} → {r.NewVersion}");

        if (!_fs.Exists(r.NewExe)) return Fail(InstallOutcome.NotStarted, "The downloaded update is missing", r.CurrentExe);
        if (!_fs.Exists(r.CurrentExe)) return Fail(InstallOutcome.NotStarted, $"{r.CurrentExe} was not found", r.CurrentExe);

        if (_fs.Exists(old))
        {
            try { _fs.Delete(old); }
            catch (Exception ex) { return Fail(InstallOutcome.NotStarted, $"The leftover {Path.GetFileName(old)} is still in use ({ex.Message})", r.CurrentExe); }
        }

        try { _fs.Move(r.CurrentExe, old); }
        catch (Exception ex) { return Fail(InstallOutcome.NotStarted, $"Could not rename the running RambiBox.exe ({ex.Message})", r.CurrentExe); }

        try { _fs.Copy(r.NewExe, r.CurrentExe); }
        catch (Exception ex) { return Rollback(r, $"Could not copy the new version into place ({ex.Message})"); }

        using var handshake = _handshakes.Create(r.Token);
        IUpdateProcess process;
        try { process = _launcher.Start(r.CurrentExe, NewVersionArgs(r)); }
        catch (Exception ex) { return Rollback(r, $"Could not start the new version ({ex.Message})"); }

        using (process)
        {
            var outcome = handshake.Wait(process, HandshakeTimeout);
            if (outcome == HandshakeOutcome.Ok)
            {
                Log.Info($"RambiBox {r.NewVersion} started (process {process.Id}); handing over");
                return new InstallResult(InstallOutcome.Installed, $"Updated to {r.NewVersion}", r.CurrentExe);
            }
            var why = outcome == HandshakeOutcome.ProcessExited
                ? $"The new version exited during startup"
                : $"The new version didn't report a healthy start within {HandshakeTimeout.TotalSeconds:0} s";
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) { Log.Warn($"Could not stop the new version: {ex.Message}"); }
            }
            return Rollback(r, why);
        }
    }

    /// <summary>Restarts the old version after a failed install (with <c>--update-failed</c>). Returns false when that fails too.</summary>
    public bool RelaunchAfterFailure(InstallRequest r, InstallResult result)
    {
        try
        {
            using var p = _launcher.Start(result.RelaunchPath, FailedArgs(r));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restart {result.RelaunchPath} after the failed update", ex);
            return false;
        }
    }

    private InstallResult Rollback(InstallRequest r, string why)
    {
        Log.Warn($"Update to {r.NewVersion} failed: {why}. Restoring {r.OldVersion}");
        var old = OldPath(r.CurrentExe);
        // the killed process may hold its exe for a moment
        Exception? last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (_fs.Exists(r.CurrentExe)) _fs.Delete(r.CurrentExe);
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                _sleep(TimeSpan.FromMilliseconds(500));
            }
        }
        if (last is not null)
            return Fail(InstallOutcome.RollbackFailed, $"{why}; the new exe could not be removed ({last.Message})", old);
        try
        {
            _fs.Move(old, r.CurrentExe);
        }
        catch (Exception ex)
        {
            return Fail(InstallOutcome.RollbackFailed, $"{why}; {Path.GetFileName(old)} could not be renamed back ({ex.Message})", old);
        }
        Log.Info($"Restored RambiBox {r.OldVersion}");
        return new InstallResult(InstallOutcome.RolledBack, why, r.CurrentExe);
    }

    private static InstallResult Fail(InstallOutcome outcome, string detail, string relaunch)
    {
        if (outcome == InstallOutcome.RollbackFailed) Log.Error($"Update rollback incomplete: {detail}");
        else Log.Warn($"Update not installed: {detail}");
        return new InstallResult(outcome, detail, relaunch);
    }
}

/// <summary>The handshake between the old and the new process: a named event per update.</summary>
public static class UpdateHandshake
{
    public static string EventName(string token) => @"Local\RambiBox.UpdateOk." + token;

    public static bool IsValidToken(string? token) => Guid.TryParseExact(token, "N", out _) || Guid.TryParseExact(token, "D", out _);

    /// <summary>Called by the new version once it runs fine. Returns false when nobody is waiting (any more).</summary>
    public static bool Signal(string token)
    {
        if (!IsValidToken(token)) return false;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName(token));
            return ev.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

// ------------------------------------------------------------------ the real platform

public sealed class RealUpdateFileSystem : IUpdateFileSystem
{
    public bool Exists(string path) => File.Exists(path);
    public void Move(string from, string to) => File.Move(from, to);
    public void Copy(string from, string to) => File.Copy(from, to, overwrite: false);
    public void Delete(string path) => File.Delete(path);
}

public sealed class RealUpdateProcessLauncher : IUpdateProcessLauncher
{
    public IUpdateProcess Start(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // a virus scanner may hold a freshly copied exe for a moment
            try { return new RealProcess(Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null")); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                last = ex;
                Thread.Sleep(1000);
            }
        }
        throw last!;
    }

    private sealed class RealProcess(Process p) : IUpdateProcess
    {
        public int Id => p.Id;
        public bool HasExited
        {
            get
            {
                try { return p.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }
        public void Kill() => p.Kill(entireProcessTree: true);
        public bool WaitForExit(TimeSpan timeout) => p.WaitForExit(timeout);
        public void Dispose() => p.Dispose();
    }
}

public sealed class RealUpdateHandshakeFactory : IUpdateHandshakeFactory
{
    public IUpdateHandshake Create(string token)
    {
        if (!UpdateHandshake.IsValidToken(token)) throw new ArgumentException("Invalid update token", nameof(token));
        return new Handshake(new EventWaitHandle(false, EventResetMode.ManualReset, UpdateHandshake.EventName(token)));
    }

    private sealed class Handshake(EventWaitHandle ev) : IUpdateHandshake
    {
        public HandshakeOutcome Wait(IUpdateProcess process, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ev.WaitOne(250)) return HandshakeOutcome.Ok;
                if (process.HasExited) return ev.WaitOne(0) ? HandshakeOutcome.Ok : HandshakeOutcome.ProcessExited;
            }
            return HandshakeOutcome.TimedOut;
        }

        public void Dispose() => ev.Dispose();
    }
}

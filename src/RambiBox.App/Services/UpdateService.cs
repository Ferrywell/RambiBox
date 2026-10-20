using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using RambiBox.Core;
using RambiBox.Core.Config;
using RambiBox.Core.Updates;

namespace RambiBox.App.Services;

/// <summary>What the app shell does for the updater (implemented by <see cref="App"/>).</summary>
public interface IUpdateShell
{
    /// <summary>The main window is open and visible.</summary>
    bool WindowVisible { get; }
    /// <summary>Tray notification; clicking it opens the window on <paramref name="page"/>.</summary>
    void Notify(string title, string message, string page);
    /// <summary>Stops the light engine (LEDs off, COM port free) and releases the single-instance mutex. UI thread.</summary>
    void PrepareForInstall();
    /// <summary>Exits this process (the new version — or the restarted old one — takes over).</summary>
    void ExitForUpdate();
}

public enum UpdateState { Idle, Checking, UpToDate, Available, CheckFailed, Downloading, Verifying, ReadyToInstall, Installing, Failed }

/// <summary>
/// The in-app updater: automatic daily checks (only after the user said yes), "Check now", download + signature
/// verification, and installing — by the user's click, or, only when turned on, automatically while the PC is idle.
/// Lives on the UI thread; all network and file work runs in the background.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly AppHost _host;
    private readonly IUpdateShell _shell;
    private readonly HttpClient _http;
    private readonly GitHubReleaseClient _client;
    private readonly UpdateDownloader _downloader;
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DateTimeOffset _autoStart = DateTimeOffset.Now;
    private DateTimeOffset? _lastAutoCheck;
    private UpdateChannel _lastChannel;
    private bool _lastAutoCheckEnabled, _lastAutoInstall;
    private Task? _checkTask, _downloadTask;
    private CancellationTokenSource? _downloadCts;
    private UpdateCheckResult? _lastFailure;
    private (ReleaseInfo Release, string Exe)? _ready;
    private bool _readyForAutoInstall, _disposed;
    private int _demoClicks;

    public UpdateService(AppHost host, IUpdateShell shell)
    {
        _host = host;
        _shell = shell;
        Current = AppInfo.SemVersion;
        _http = GitHubReleaseClient.CreateHttpClient(Current.ToString());
        _client = new GitHubReleaseClient(host.UpdatesDirectory, Current.ToString(), _http);
        _downloader = new UpdateDownloader(_http, host.UpdatesDirectory, new UpdateVerifier());
        InstallUnsupportedReason = DetectInstallSupport(host.Options);
        UpdatedFrom = host.Options.UpdatedFrom;
        FailedVersion = host.Options.UpdateFailed;
        ShowUpdatedBar = UpdatedFrom is not null;
        ShowFailedBar = FailedVersion is not null;

        _checkTimer = new DispatcherTimer(DispatcherPriority.Background);
        _checkTimer.Tick += OnCheckTimer;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += OnIdleTimer;

        _lastChannel = S.Channel;
        _lastAutoCheckEnabled = S.AutoCheckEnabled;
        _lastAutoInstall = S.InstallAutomatically;
        _host.SettingsChanged += OnSettingsChanged;

        if (FailedVersion is not null && !host.Options.Demo)
        {
            // never retry that version automatically (the user can still install it by hand)
            S.FailedVersion = FailedVersion.ToString();
            _host.Commit(this, pushToEngine: false);
        }
    }

    private UpdateSettings S => _host.Settings.Updates;
    private bool Demo => _host.Options.DemoUpdate;

    public SemVersion Current { get; }
    public UpdateState State { get; private set; } = UpdateState.Idle;
    /// <summary>The newest release newer than this one (also when the user skipped it).</summary>
    public ReleaseInfo? Latest { get; private set; }
    /// <summary>The release to offer (newer and not skipped).</summary>
    public ReleaseInfo? Offered => UpdatePolicy.ShouldOffer(Latest, S) ? Latest : null;
    public double Progress { get; private set; }
    public string? ErrorText { get; private set; }

    /// <summary>Why this copy can't install updates itself (dev build, demo, read-only folder); null = it can.</summary>
    public string? InstallUnsupportedReason { get; }
    public bool InstallSupported => InstallUnsupportedReason is null;

    /// <summary>This start is the first one after an update from that version.</summary>
    public SemVersion? UpdatedFrom { get; }
    /// <summary>This start follows a failed update to that version (it was rolled back).</summary>
    public SemVersion? FailedVersion { get; }

    public bool ShowUpdatedBar { get; private set; }
    public bool ShowFailedBar { get; private set; }
    public bool ShowAvailableBar => Offered is not null && !_availableBarDismissed && State != UpdateState.Installing;
    private bool _availableBarDismissed;
    public bool ShowConsentBar => !S.ConsentAsked;
    public bool Busy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing;

    /// <summary>Raised on the UI thread whenever anything above changed.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ text

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.Downloading => $"Downloading RambiBox {Latest?.Version}… {Progress:P0}",
        UpdateState.Verifying => "Checking the digital signature…",
        UpdateState.ReadyToInstall when _readyForAutoInstall => $"RambiBox {_ready?.Release.Version} is ready – it installs when you're not using the PC",
        UpdateState.ReadyToInstall => $"RambiBox {_ready?.Release.Version} is downloaded and verified",
        UpdateState.Installing => "Installing… RambiBox restarts in a moment",
        UpdateState.CheckFailed when _lastFailure is not null => UpdateText.CheckFailed(_lastFailure),
        UpdateState.Failed => Offered is { } f ? $"Version {f.Version} is available" : "Update failed",
        _ when Offered is { } r => $"Version {r.Version} is available",
        _ when S.LastCheck is { } t => $"You're up to date · {UpdateText.Checked(t, DateTimeOffset.Now)}",
        _ => S.ConsentAsked && !S.CheckAutomatically ? "Not checked yet · automatic checks are off" : "Not checked yet",
    };

    /// <summary>Second line under the status (release name/date, skipped version, …).</summary>
    public string StatusDetail
    {
        get
        {
            if (State == UpdateState.Failed && ErrorText is not null) return ErrorText;
            if (Offered is { } r)
                return r.PublishedAt is { } p ? $"{r.Title} · released {p.ToLocalTime():d MMM yyyy}" : r.Title;
            if (Latest is { } skipped && State is not UpdateState.Checking)
                return $"You skipped version {skipped.Version} (\"What's new\" still lets you install it).";
            if (State == UpdateState.CheckFailed && S.LastCheck is { } t) return $"Last successful check: {UpdateText.Checked(t, DateTimeOffset.Now)[8..]}";
            return "";
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts the automatic schedule (if the user agreed) and the demo, if any.</summary>
    public void Start()
    {
        if (Demo)
        {
            // --demo-update: pretend the question was answered and a newer version was found right away
            S.ConsentAsked = true;
            _host.Commit(this, pushToEngine: false);
            _ = CheckNowAsync(manual: true);
        }
        Reschedule();
    }

    private void Reschedule()
    {
        _checkTimer.Stop();
        if (_disposed || Demo) return;
        var next = UpdatePolicy.NextAutomaticCheck(S, _autoStart, _lastAutoCheck);
        if (next is not { } due) return;
        var wait = due - DateTimeOffset.Now;
        _checkTimer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        _checkTimer.Start();
    }

    private async void OnCheckTimer(object? sender, EventArgs e)
    {
        _checkTimer.Stop();
        try
        {
            if (!UpdatePolicy.MayCheckAutomatically(S, IsMetered()))
            {
                Log.Info("Automatic update check skipped (metered connection or turned off); trying again in an hour");
                _lastAutoCheck = DateTimeOffset.Now - UpdatePolicy.CheckInterval + TimeSpan.FromHours(1);
            }
            else
            {
                _lastAutoCheck = DateTimeOffset.Now;
                await CheckNowAsync(manual: false);
            }
        }
        catch (Exception ex) { Log.Warn($"Automatic update check failed: {ex.Message}"); }
        Reschedule();
    }

    // ------------------------------------------------------------------ consent

    /// <summary>The one-time question on Home: yes = daily checks, no = never automatically.</summary>
    public void AnswerConsent(bool checkDaily)
    {
        S.ConsentAsked = true;
        S.CheckAutomatically = checkDaily;
        if (!checkDaily) S.InstallAutomatically = false;
        Log.Info(checkDaily ? "Updates: the user allowed daily checks" : "Updates: the user turned automatic checks off");
        _host.Commit(this, pushToEngine: false);
        if (checkDaily)
        {
            _lastAutoCheck = DateTimeOffset.Now;
            _ = CheckNowAsync(manual: false);
        }
        Reschedule();
        RaiseChanged();
    }

    // ------------------------------------------------------------------ check

    /// <summary>Checks GitHub now. <paramref name="manual"/>: the user asked (always allowed).</summary>
    public Task CheckNowAsync(bool manual)
    {
        if (_checkTask is { IsCompleted: false }) return _checkTask;
        _checkTask = CheckCoreAsync(manual);
        return _checkTask;
    }

    private async Task CheckCoreAsync(bool manual)
    {
        if (Busy || State == UpdateState.ReadyToInstall && !manual) return;
        var before = State;
        if (State != UpdateState.ReadyToInstall) SetState(UpdateState.Checking);
        if (manual) ErrorText = null;
        RaiseChanged();

        var result = Demo ? await DemoCheckAsync() : await _client.CheckAsync(Current, S.Channel);
        if (_disposed) return;

        if (!result.Succeeded)
        {
            _lastFailure = result;
            Log.Info($"Update check failed: {result.Status} {result.Error}");
            SetState(before == UpdateState.ReadyToInstall ? before : UpdateState.CheckFailed);
            RaiseChanged();
            return;
        }

        _lastFailure = null;
        S.LastCheck = DateTimeOffset.Now;
        _host.Commit(this, pushToEngine: false);
        var previous = Latest?.Version;
        Latest = result.Update;
        if (Latest is not null && Latest.Version != previous) _availableBarDismissed = false;
        Log.Info(Latest is null ? $"Update check: {Current} is up to date ({S.Channel})"
                                : $"Update check: {Latest.Version} is available ({S.Channel}){(Offered is null ? " – skipped by the user" : "")}");
        if (before == UpdateState.ReadyToInstall && _ready is { } r && r.Release.Version == Latest?.Version) SetState(UpdateState.ReadyToInstall);
        else SetState(Latest is null ? UpdateState.UpToDate : UpdateState.Available);
        RaiseChanged();
        OnFound(manual);
    }

    private void OnFound(bool manual)
    {
        var r = Offered;
        if (r is null) return;
        if (!manual && UpdatePolicy.ShouldNotify(r, S))
        {
            if (!_shell.WindowVisible)
                _shell.Notify($"RambiBox {r.Version} is available", "Click to see what's new. Updating takes one click.", "home");
            S.NotifiedVersion = r.Version.ToString();
            _host.Commit(this, pushToEngine: false);
        }
        MaybeStartAutoDownload();
    }

    private void MaybeStartAutoDownload()
    {
        var r = Offered;
        if (r is null || _downloadTask is { IsCompleted: false } || State == UpdateState.ReadyToInstall) return;
        if (!UpdatePolicy.ShouldDownloadAutomatically(r, S, InstallSupported, IsMetered())) return;
        Log.Info($"Downloading {r.Version} in the background for the automatic install");
        _downloadTask = AutoDownloadAsync(r);
    }

    private async Task AutoDownloadAsync(ReleaseInfo r)
    {
        var exe = await DownloadAsync(r);
        if (exe is null || _disposed) return;
        _readyForAutoInstall = S.InstallAutomatically;
        if (_readyForAutoInstall) _idleTimer.Start();
        RaiseChanged();
    }

    // ------------------------------------------------------------------ user actions

    /// <summary>"Skip this version": the bar goes away and no notification or automatic install for it.</summary>
    public void SkipOffered()
    {
        if (Offered is not { } r) return;
        S.SkippedVersion = r.Version.ToString();
        Log.Info($"Updates: the user skipped {r.Version}");
        _host.Commit(this, pushToEngine: false);
        if (_ready?.Release.Version == r.Version)
        {
            _ready = null;
            _idleTimer.Stop();
            UpdateDownloader.Cleanup(_downloader.UpdatesDirectory, keep: null);
        }
        SetState(UpdateState.UpToDate);
        RaiseChanged();
    }

    public void DismissAvailableBar()
    {
        _availableBarDismissed = true;
        RaiseChanged();
    }

    public void DismissUpdatedBar()
    {
        ShowUpdatedBar = false;
        RaiseChanged();
    }

    public void DismissFailedBar()
    {
        ShowFailedBar = false;
        RaiseChanged();
    }

    /// <summary>"Update now": download (unless done), verify, install and restart.</summary>
    public async Task UpdateNowAsync()
    {
        var r = Offered ?? Latest;
        if (r is null || Busy) return;
        if (!InstallSupported && !Demo)
        {
            AppInfo.OpenUrl(r.HtmlUrl); // e.g. a copy in a read-only folder: download by hand
            return;
        }
        ErrorText = null;
        string? exe = _ready is { } ready && ready.Release.Version == r.Version ? ready.Exe : await DownloadAsync(r);
        if (exe is null || _disposed) return;
        if (Demo)
        {
            // the demo stops at "downloaded and verified"; a second click explains why nothing happens
            ErrorText = _ready is not null && State == UpdateState.ReadyToInstall && exe == _ready.Value.Exe && _demoClicks++ > 0
                ? "Demo mode: nothing is installed." : null;
            RaiseChanged();
            return;
        }
        await InstallAsync(r, exe, automatic: false);
    }

    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<string?> DownloadAsync(ReleaseInfo r)
    {
        _downloadCts?.Dispose();
        var cts = _downloadCts = new CancellationTokenSource();
        Progress = 0;
        SetState(UpdateState.Downloading);
        RaiseChanged();
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (_disposed || State is not (UpdateState.Downloading or UpdateState.Verifying)) return;
            Progress = p.Fraction;
            SetState(p.Verifying ? UpdateState.Verifying : UpdateState.Downloading);
            RaiseChanged();
        });
        var result = Demo ? await DemoDownloadAsync(progress, cts.Token) : await _downloader.DownloadAsync(r, progress, cts.Token);
        if (_disposed) return null;
        if (result.Ok)
        {
            _ready = (r, result.ExePath!);
            SetState(UpdateState.ReadyToInstall);
            RaiseChanged();
            return result.ExePath;
        }
        ErrorText = result.Status switch
        {
            DownloadStatus.VerificationFailed => "The download could not be verified, so it was not installed. Please try again later.",
            DownloadStatus.Offline => "The download failed. Check your internet connection and try again.",
            DownloadStatus.Cancelled => null,
            _ => $"The download failed ({result.Error}).",
        };
        if (result.Status != DownloadStatus.Cancelled) Log.Warn($"Update download failed: {result.Status} {result.Error}");
        SetState(result.Status == DownloadStatus.Cancelled ? UpdateState.Available : UpdateState.Failed);
        RaiseChanged();
        return null;
    }

    private async void OnIdleTimer(object? sender, EventArgs e)
    {
        try
        {
            if (_ready is not { } ready || State != UpdateState.ReadyToInstall || !S.InstallAutomatically || !S.AutoCheckEnabled)
            {
                _idleTimer.Stop();
                return;
            }
            var activity = new ActivityState(NativeMethods.IdleTime(), _host.DisplayOff, !_host.Settings.Enabled, _shell.WindowVisible,
                _host.CalibrationActive, _host.ApiControl.Active);
            if (!UpdatePolicy.MayInstallNow(activity)) return;
            _idleTimer.Stop();
            Log.Info($"Installing {ready.Release.Version} automatically (idle {activity.IdleFor.TotalMinutes:0} min, display off: {activity.DisplayOff}, lights off: {activity.LightsOff})");
            await InstallAsync(ready.Release, ready.Exe, automatic: true);
        }
        catch (Exception ex) { Log.Error("Automatic update install failed", ex); }
    }

    private async Task InstallAsync(ReleaseInfo r, string exe, bool automatic)
    {
        if (_host.CalibrationActive)
        {
            ErrorText = "Finish the color calibration first, then update.";
            RaiseChanged();
            return;
        }
        var processPath = Environment.ProcessPath;
        if (processPath is null || !InstallSupported) return;

        // last check right before the swap: the file must still be exactly what was signed
        SetState(UpdateState.Verifying);
        RaiseChanged();
        var verify = await Task.Run(() => _downloader.VerifyDownloaded(r, out _));
        if (!verify.Ok)
        {
            Log.Warn($"Update {r.Version} failed verification before installing: {verify.Detail}");
            _ready = null;
            UpdateDownloader.Cleanup(_downloader.UpdatesDirectory, keep: null);
            ErrorText = "The download could not be verified, so it was not installed. Please try again later.";
            SetState(UpdateState.Failed);
            RaiseChanged();
            return;
        }

        SetState(UpdateState.Installing);
        RaiseChanged();
        await Task.Delay(300); // let the status repaint before the LEDs go dark
        _checkTimer.Stop();
        _idleTimer.Stop();

        var request = new InstallRequest(processPath, exe, Current, r.Version, StartInTray: !_shell.WindowVisible,
            Guid.NewGuid().ToString("N"));
        var installer = new UpdateInstaller(new RealUpdateFileSystem(), new RealUpdateProcessLauncher(), new RealUpdateHandshakeFactory());
        _shell.PrepareForInstall();
        var result = await Task.Run(() => installer.Install(request));
        if (result.Installed)
        {
            _shell.ExitForUpdate();
            return;
        }

        // rolled back (or not even started): restart the old version, which shows "Update failed" and won't retry it automatically
        Log.Warn($"Update {(automatic ? "(automatic) " : "")}to {r.Version} failed: {result.Outcome} – {result.Detail}");
        S.FailedVersion = r.Version.ToString();
        _host.SaveSettingsNow();
        bool restarted = await Task.Run(() => installer.RelaunchAfterFailure(request, result));
        if (!restarted)
        {
            MessageBox.Show($"The update to RambiBox {r.Version} failed and RambiBox could not restart itself.\n\n" +
                            $"Please start RambiBox again. Details are in {Log.FilePath}", "RambiBox", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        _shell.ExitForUpdate();
    }

    // ------------------------------------------------------------------ settings

    private void OnSettingsChanged(object? sender)
    {
        bool channelChanged = S.Channel != _lastChannel;
        bool autoChanged = S.AutoCheckEnabled != _lastAutoCheckEnabled;
        bool installChanged = S.InstallAutomatically != _lastAutoInstall;
        _lastChannel = S.Channel;
        _lastAutoCheckEnabled = S.AutoCheckEnabled;
        _lastAutoInstall = S.InstallAutomatically;

        if (channelChanged && !Busy)
        {
            // re-evaluate the last answer for the other channel without contacting GitHub
            var cached = Demo ? null : _client.EvaluateCached(Current, S.Channel);
            if (cached is not null)
            {
                Latest = cached.Update;
                if (State != UpdateState.ReadyToInstall || _ready?.Release.Version != Latest?.Version)
                {
                    if (_ready is not null && _ready.Value.Release.Version != Latest?.Version) { _ready = null; _idleTimer.Stop(); }
                    SetState(Latest is null ? UpdateState.UpToDate : UpdateState.Available);
                }
            }
        }
        if (autoChanged) Reschedule();
        if (installChanged)
        {
            _readyForAutoInstall = S.InstallAutomatically && _ready is not null;
            if (_readyForAutoInstall) _idleTimer.Start();
            else _idleTimer.Stop();
            if (S.InstallAutomatically) MaybeStartAutoDownload();
        }
        if (channelChanged || autoChanged || installChanged || sender is not UpdateService) RaiseChanged();
    }

    // ------------------------------------------------------------------ helpers

    private void SetState(UpdateState s) => State = s;

    private void RaiseChanged()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Update UI refresh failed: {ex.Message}"); }
    }

    /// <summary>Metered connection (mobile hotspot, data limit): no automatic checks or downloads.</summary>
    private static bool IsMetered()
    {
        try
        {
            var cost = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost is not null && (cost.NetworkCostType is Windows.Networking.Connectivity.NetworkCostType.Fixed
                or Windows.Networking.Connectivity.NetworkCostType.Variable || cost.OverDataLimit || cost.Roaming);
        }
        catch { return false; }
    }

    /// <summary>
    /// Only the release build (one self-contained RambiBox.exe in a folder the user can write to) replaces itself;
    /// a development build (dotnet run), a simulated/demo run or a read-only location only checks.
    /// </summary>
    private static string? DetectInstallSupport(AppOptions options)
    {
        const string releaseOnly = "Updates are installed by the release build only.";
        if (options.Simulate || options.Demo) return releaseOnly;
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return releaseOnly;
#pragma warning disable IL3000 // Assembly.Location is empty in a single-file app: exactly what is checked here
        if (!string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location)) return releaseOnly;
#pragma warning restore IL3000
        var dir = Path.GetDirectoryName(path)!;
        try
        {
            var probe = Path.Combine(dir, $".rambibox-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Log.Info($"Updates can't be installed in place: {dir} is not writable ({ex.Message})");
            return $"RambiBox can't write to its folder ({ViewModels.MainViewModel.DisplayPath(dir)}), so new versions have to be downloaded from GitHub.";
        }
        return null;
    }

    // ------------------------------------------------------------------ --demo-update

    /// <summary>A pretend release one minor version up, with typical release notes.</summary>
    internal static ReleaseInfo DemoRelease(SemVersion current)
    {
        var v = new SemVersion(current.Major, current.Minor + 1, 0);
        const string url = "https://github.com/Ferrywell/RambiBox/releases/download/";
        return new ReleaseInfo(v, $"v{v}", $"RambiBox {v}",
            $"""
            ## What's new in {v}
            - **Smoother fades** between scenes, especially in dark movies
            - The LED layout editor remembers the last corner you edited
            - New: *Breathe* mood effect follows the static color
            ### Fixes
            - WLED devices reconnect faster after Wi-Fi drops
            - The tray icon shows the right state after waking from sleep

            **Full Changelog**: https://github.com/Ferrywell/RambiBox/compare/v{current}...v{v}
            """,
            $"https://github.com/Ferrywell/RambiBox/releases/tag/v{v}", DateTimeOffset.Now.AddDays(-1), false,
            new ReleaseAsset(ReleaseParser.ExeName, url + $"v{v}/RambiBox.exe", 71_234_567),
            new ReleaseAsset(ReleaseParser.SignatureName, url + $"v{v}/RambiBox.exe.sig", 71),
            new ReleaseAsset(ReleaseParser.ChecksumName, url + $"v{v}/RambiBox.exe.sha256", 78));
    }

    private async Task<UpdateCheckResult> DemoCheckAsync()
    {
        await Task.Delay(700);
        var r = DemoRelease(Current);
        return S.Channel == UpdateChannel.Beta || !r.IsPrerelease
            ? new UpdateCheckResult(CheckStatus.UpdateAvailable, r, CheckedAt: DateTimeOffset.Now)
            : new UpdateCheckResult(CheckStatus.UpToDate, CheckedAt: DateTimeOffset.Now);
    }

    /// <summary>A slow pretend download (so the progress bar can be seen), then "verified". Nothing is written.</summary>
    private static async Task<DownloadResult> DemoDownloadAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        const long total = 71_234_567;
        try
        {
            for (int i = 0; i <= 100; i++)
            {
                progress.Report(new DownloadProgress(total * i / 100, total, false));
                await Task.Delay(i < 60 ? 150 : 60, ct);
            }
            progress.Report(new DownloadProgress(total, total, true));
            await Task.Delay(1500, ct);
            return new DownloadResult(DownloadStatus.Ok, Path.Combine(Path.GetTempPath(), "RambiBox.Demo", "updates", "RambiBox.exe"));
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(DownloadStatus.Cancelled);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.SettingsChanged -= OnSettingsChanged;
        _checkTimer.Stop();
        _idleTimer.Stop();
        CancelDownload();
        _downloadCts?.Dispose();
        _client.Dispose();
        _http.Dispose();
    }
}

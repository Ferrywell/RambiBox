using System.Globalization;
using System.Windows;
using System.Windows.Input;
using RambiBox.App.Services;
using RambiBox.App.Views;
using RambiBox.Core.Config;
using RambiBox.Core.Updates;

namespace RambiBox.App.ViewModels;

/// <summary>
/// "About &amp; updates" on the Settings page and the update InfoBars on Home. A thin view of <see cref="UpdateService"/>
/// plus the update settings (committed like every other setting).
/// </summary>
public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly UpdateService? _svc;
    private bool _onHome = true;

    public UpdatesViewModel(AppHost host)
    {
        _host = host;
        _svc = host.Updates;
        if (_svc is not null) _svc.Changed += RefreshAll;
        _host.SettingsChanged += OnSettingsChanged;

        CheckNowCommand = new RelayCommand(async () => await (_svc?.CheckNowAsync(manual: true) ?? Task.CompletedTask), () => _svc is { Busy: false });
        UpdateNowCommand = new RelayCommand(async () => await (_svc?.UpdateNowAsync() ?? Task.CompletedTask), () => _svc is { Busy: false, Latest: not null });
        WhatsNewCommand = new RelayCommand(ShowWhatsNew);
        SkipCommand = new RelayCommand(() => _svc?.SkipOffered());
        CancelDownloadCommand = new RelayCommand(() => _svc?.CancelDownload());
        DismissAvailableCommand = new RelayCommand(() => _svc?.DismissAvailableBar());
        DismissUpdatedCommand = new RelayCommand(() => _svc?.DismissUpdatedBar());
        DismissFailedCommand = new RelayCommand(() => _svc?.DismissFailedBar());
        ConsentYesCommand = new RelayCommand(() => _svc?.AnswerConsent(true));
        ConsentNoCommand = new RelayCommand(() => _svc?.AnswerConsent(false));
        CurrentReleaseNotesCommand = new RelayCommand(() => AppInfo.OpenUrl(UpdateText.ReleasePage(AppInfo.SemVersion)));
    }

    private UpdateSettings U => _host.Settings.Updates;

    private void OnSettingsChanged(object? sender)
    {
        if (!ReferenceEquals(sender, this)) RefreshAll();
    }

    /// <summary>The InfoBars belong on Home only (set by <see cref="MainViewModel"/> on navigation).</summary>
    public bool OnHome
    {
        get => _onHome;
        set
        {
            if (SetField(ref _onHome, value)) RefreshAll();
        }
    }

    // ------------------------------------------------------------------ about

    public string VersionTitle => $"RambiBox {AppInfo.Version}";

    public string BuildText
    {
        get
        {
            var parts = new List<string>();
            if (AppInfo.BuildDate is { } d) parts.Add("Built " + d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            parts.Add(AppInfo.SemVersion.IsPrerelease ? "pre-release" : "release");
            if (_host.Options.Simulate && !_host.Options.Demo) parts.Add("simulated LED device");
            return string.Join(" · ", parts);
        }
    }

    public string ChannelBadge => U.Channel == UpdateChannel.Beta ? "Beta channel" : "Stable channel";

    // ------------------------------------------------------------------ status card

    public string StatusText => _svc?.StatusText ?? "Updates are not available";
    public string StatusDetail => _svc?.StatusDetail ?? "";
    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);

    public StatusLevel StatusLevel => _svc?.State switch
    {
        UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing => StatusLevel.Busy,
        UpdateState.CheckFailed or UpdateState.Failed => StatusLevel.Problem,
        _ when _svc?.Offered is not null => StatusLevel.Busy,
        _ when U.LastCheck is not null => StatusLevel.Good,
        _ => StatusLevel.Off,
    };

    public string StatusGlyph => _svc?.State switch
    {
        UpdateState.Checking => "",                                  // Sync
        UpdateState.Downloading or UpdateState.Verifying => "",      // Download
        UpdateState.Installing => "",                                // UpdateRestore
        UpdateState.CheckFailed or UpdateState.Failed => "",         // Warning
        _ when _svc?.Offered is not null => "",
        _ when U.LastCheck is not null => "",                        // Completed
        _ => "",
    };

    public bool IsBusy => _svc?.Busy == true;
    public bool ShowProgress => _svc?.State is UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing;
    public bool ProgressIndeterminate => _svc?.State is UpdateState.Verifying or UpdateState.Installing;
    public double ProgressValue => (_svc?.Progress ?? 0) * 100;
    public bool ShowCancelDownload => _svc?.State == UpdateState.Downloading;

    public bool ShowUpdateNow => _svc?.Offered is not null && !ShowProgress;
    public string UpdateNowLabel => CanInstall
        ? _svc?.State == UpdateState.ReadyToInstall ? "Install now" : "Update now"
        : "Download from GitHub";

    /// <summary>This copy can replace itself (the release build), or the demo pretends it can.</summary>
    public bool CanInstall => _svc is not null && (_svc.InstallSupported || _host.Options.DemoUpdate);
    public string? InstallNote => _svc is { InstallSupported: false } s && !_host.Options.DemoUpdate ? s.InstallUnsupportedReason : null;
    public string? ErrorText => _svc?.State == UpdateState.Failed ? null : _svc?.ErrorText; // a Failed state shows it as the detail line

    // ------------------------------------------------------------------ settings

    /// <summary>On only once the user agreed (before that nothing is checked automatically, whatever the default says).</summary>
    public bool CheckAutomatically
    {
        get => U.AutoCheckEnabled;
        set
        {
            if (value == U.AutoCheckEnabled) return;
            if (_svc is not null) _svc.AnswerConsent(value); // changing it here answers the question too (and checks right away)
            else
            {
                U.ConsentAsked = true;
                U.CheckAutomatically = value;
                if (!value) U.InstallAutomatically = false;
                _host.Commit(this, pushToEngine: false);
            }
            RefreshAll();
        }
    }

    public bool InstallAutomatically
    {
        get => U.InstallAutomatically && U.AutoCheckEnabled;
        set
        {
            if (value == InstallAutomatically) return;
            if (value && !U.AutoCheckEnabled)
            {
                U.ConsentAsked = true;
                U.CheckAutomatically = true;
            }
            U.InstallAutomatically = value;
            _host.Commit(this, pushToEngine: false);
            RefreshAll();
        }
    }

    /// <summary>Automatic install needs the release build (and is meaningless without automatic checks).</summary>
    public bool InstallAutomaticallyEnabled => CanInstall;

    public UpdateChannel Channel
    {
        get => U.Channel;
        set
        {
            if (value == U.Channel) return;
            U.Channel = value;
            _host.Commit(this, pushToEngine: false);
            RefreshAll();
        }
    }

    public IReadOnlyList<Option<UpdateChannel>> Channels { get; } =
    [
        new(UpdateChannel.Stable, "Stable"),
        new(UpdateChannel.Beta, "Beta (pre-releases)"),
    ];

    // ------------------------------------------------------------------ Home InfoBars

    public bool ShowConsentBar => OnHome && _svc?.ShowConsentBar == true && _svc?.ShowFailedBar != true;
    public bool ShowAvailableBar => OnHome && _svc?.ShowAvailableBar == true;
    public bool ShowUpdatedBar => OnHome && _svc?.ShowUpdatedBar == true;
    public bool ShowFailedBar => OnHome && _svc?.ShowFailedBar == true;

    public string AvailableTitle => _svc?.Offered is { } r ? $"RambiBox {r.Version} is available." : "";
    public string AvailableMessage => _svc switch
    {
        { State: UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing } s => s.StatusText,
        { State: UpdateState.Failed, ErrorText: { } e } => e,
        { State: UpdateState.ReadyToInstall } s => s.StatusText + ".",
        { Offered: { } r } => $"You have {AppInfo.Version}. Updates are downloaded from GitHub and installed only when their signature checks out.",
        _ => "",
    };

    public string UpdatedTitle => $"Updated to {AppInfo.Version}.";
    public string UpdatedMessage => _svc?.UpdatedFrom is { } from ? $"RambiBox was updated from {from}." : "";
    public string FailedTitle => $"Update failed — you're still on {AppInfo.Version}.";
    public string FailedMessage => _svc?.FailedVersion is { } v
        ? $"RambiBox {v} couldn't be installed or didn't start correctly, so the previous version is still in place. Details are in the log."
        : "";

    // ------------------------------------------------------------------ commands

    public ICommand CheckNowCommand { get; }
    public ICommand UpdateNowCommand { get; }
    public ICommand WhatsNewCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand DismissAvailableCommand { get; }
    public ICommand DismissUpdatedCommand { get; }
    public ICommand DismissFailedCommand { get; }
    public ICommand ConsentYesCommand { get; }
    public ICommand ConsentNoCommand { get; }
    public ICommand CurrentReleaseNotesCommand { get; }

    /// <summary>Release notes of the offered version in a small window, or the notes of this version on GitHub.</summary>
    private void ShowWhatsNew()
    {
        var r = _svc?.Offered ?? _svc?.Latest;
        if (r is null)
        {
            AppInfo.OpenUrl(UpdateText.ReleasePage(AppInfo.SemVersion));
            return;
        }
        var w = new ReleaseNotesWindow(r, CanInstall && _svc is { Busy: false } ? UpdateNowCommand : null)
        {
            Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(),
        };
        w.ShowDialog();
    }

    private new void RefreshAll()
    {
        base.RefreshAll();
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        if (_svc is not null) _svc.Changed -= RefreshAll;
        _host.SettingsChanged -= OnSettingsChanged;
    }
}

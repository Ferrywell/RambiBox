using System.Windows.Threading;
using RambiBox.Core.Api;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.App.Services;

/// <summary>
/// The application side of the Prismatik API: reads come from a snapshot refreshed on every commit plus the engine's
/// thread-safe stats/colors; changes are applied on the UI thread through <see cref="AppHost.Commit"/>, exactly like
/// a change made in the UI (engine updated immediately, file saved debounced).
/// </summary>
internal sealed class PrismatikHostAdapter(AppHost host, Dispatcher dispatcher) : IPrismatikHost
{
    private sealed record SettingsPart(bool Enabled, LightMode Mode, double Brightness, int SmoothingMs, double Gamma, IReadOnlyList<LedZone> Zones);

    private SettingsPart _settings = new(true, LightMode.Ambilight, 1, 100, 1, Array.Empty<LedZone>());

    /// <summary>UI thread: takes a snapshot of the live settings.</summary>
    public void Refresh(AppSettings s) =>
        Volatile.Write(ref _settings, new SettingsPart(s.Enabled, s.Mode, s.Color.Brightness, s.Color.SmoothingMs, s.Color.Gamma,
            s.Layout.Zones.ToArray()));

    public PrismatikHostState GetState()
    {
        var p = Volatile.Read(ref _settings);
        var stats = host.Engine.GetStats();
        var status = !p.Enabled ? PrismatikStatus.Off
            : host.Device.Status switch
            {
                DeviceStatus.Connected => PrismatikStatus.On,
                DeviceStatus.PortBusy or DeviceStatus.Searching => PrismatikStatus.DeviceError,
                _ => PrismatikStatus.Unknown,
            };
        return new PrismatikHostState(status, p.Mode, p.Brightness, p.SmoothingMs, p.Gamma, Math.Round(stats.OutputFps, 2),
            ScreenRect(stats.Monitor), p.Zones, host.Options.Simulate ? "Virtual" : "Adalight");
    }

    private PixelRect ScreenRect(MonitorInfo? current)
    {
        var m = current;
        if (m is null)
        {
            try { m = host.Sampler.GetMonitors().FirstOrDefault(x => x.IsPrimary) ?? host.Sampler.GetMonitors().FirstOrDefault(); }
            catch { m = null; }
        }
        return m is null ? new PixelRect(0, 0, 1920, 1080) : new PixelRect(m.X, m.Y, m.Width, m.Height);
    }

    public byte[] GetColors() => host.Engine.GetPreviewColors();

    public IReadOnlyList<PixelRect> GetMonitors()
    {
        try { return host.Sampler.GetMonitors().Select(m => new PixelRect(m.X, m.Y, m.Width, m.Height)).ToArray(); }
        catch { return Array.Empty<PixelRect>(); }
    }

    public Task<bool> ApplyAsync(PrismatikChange change) =>
        dispatcher.InvokeAsync(() =>
        {
            var s = host.Settings;
            if (change.Enabled is { } enabled) s.Enabled = enabled;
            if (change.Mode is { } mode) s.Mode = mode;
            if (change.Brightness is { } b) s.Color.Brightness = b;
            if (change.SmoothingMs is { } ms) s.Color.SmoothingMs = ms;
            if (change.Gamma is { } g) s.Color.Gamma = g;
            if (change.Zones is { } zones)
            {
                var list = s.Layout.Zones.ToList();
                foreach (var z in zones)
                    if (z.Index >= 0 && z.Index < list.Count)
                        list[z.Index] = new LedZone(z.Left, z.Top, z.Right, z.Bottom, list[z.Index].Enabled);
                s.Layout.Zones = list;
            }
            host.Commit(this);
            return true;
        }).Task;

    public void OnApiControlChanged(PrismatikControl control) =>
        dispatcher.BeginInvoke(() => host.OnApiControlChanged(control));
}

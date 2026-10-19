using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using RambiBox.Core;
using static RambiBox.App.Services.NativeMethods;

namespace RambiBox.App.Services;

/// <summary>
/// Listens to Windows: USB plug/unplug (rescans the LED controller), display on/off, sleep/resume,
/// lock/unlock and session end. Uses one hidden (never shown) top-level window, which — unlike a
/// message-only window — also receives the broadcast WM_DEVICECHANGE messages.
/// </summary>
public sealed class SystemIntegration : IDisposable
{
    private readonly AppHost _host;
    private readonly HwndSource _source;
    private readonly IntPtr _powerNotify, _deviceNotify;
    private readonly DispatcherTimer _rescanDebounce;

    public SystemIntegration(AppHost host)
    {
        _host = host;
        var p = new HwndSourceParameters("RambiBox system events")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not visible
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        _rescanDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _rescanDebounce.Tick += (_, _) =>
        {
            _rescanDebounce.Stop();
            _host.OnDevicesChanged();
        };

        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _powerNotify = RegisterPowerSettingNotification(_source.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_powerNotify == IntPtr.Zero) Log.Warn($"RegisterPowerSettingNotification failed ({Marshal.GetLastWin32Error()})");

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
        };
        _deviceNotify = RegisterDeviceNotification(_source.Handle, ref filter,
            DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        if (_deviceNotify == IntPtr.Zero) Log.Warn($"RegisterDeviceNotification failed ({Marshal.GetLastWin32Error()})");

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DEVICECHANGE:
            {
                int evt = wParam.ToInt32();
                if (evt is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
                {
                    // Plugging a USB device produces a burst of messages; act once it settles.
                    _rescanDebounce.Stop();
                    _rescanDebounce.Start();
                }
                break;
            }
            case WM_POWERBROADCAST when wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero:
            {
                // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[1]; }
                var setting = Marshal.PtrToStructure<Guid>(lParam);
                if (setting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    int state = Marshal.ReadInt32(lParam, 20);
                    Log.Info($"Display state: {(state switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => state.ToString() })}");
                    _host.OnDisplayState(state);
                }
                break;
            }
            case WM_ENDSESSION when wParam != IntPtr.Zero:
                _host.ShutdownLeds();
                break;
        }
        return IntPtr.Zero;
    }

    // SystemEvents are raised on their own thread.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Log.Info("System is going to sleep");
                _host.OnSuspend(true);
                Thread.Sleep(150); // give the engine a moment to send black before the USB port powers down
                break;
            case PowerModes.Resume:
                Log.Info("System resumed");
                _host.OnSuspend(false);
                break;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => null,
        };
        if (locked is { } l)
        {
            Log.Info(l ? "Workstation locked" : "Workstation unlocked");
            _source.Dispatcher.BeginInvoke(() => _host.OnLockChanged(l));
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        Log.Info($"Session ending ({e.Reason})");
        _host.ShutdownLeds();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _rescanDebounce.Stop();
        if (_powerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotify);
        if (_deviceNotify != IntPtr.Zero) UnregisterDeviceNotification(_deviceNotify);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

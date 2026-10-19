# RambiBox

A modern, lightweight replacement for AmbiBox for Adalight LED strips (Arduino + WS281x over USB serial) on Windows 10/11.

## Install

1. Run `install.cmd` (no admin needed). It builds `dist\RambiBox.exe` if needed, copies it to
   `%LocalAppData%\Programs\RambiBox`, adds a Start menu shortcut and starts it.
2. On first start RambiBox imports your AmbiBox settings automatically (LED layout, white balance, saturation,
   smoothing, static color, port) and finds the LED controller by itself.
3. If AmbiBox is still running or starts with Windows, RambiBox offers to close it and remove it from startup
   (reversible — the shortcut is kept in `%AppData%\RambiBox\AmbiBox.lnk.disabled`).

RambiBox lives in the system tray: left-click opens the window, right-click gives on/off, mode, brightness and exit.

## Features

- **Screen ambilight** via DXGI Desktop Duplication with GPU downscaling: ~1 % of one CPU core, link-limited ~32 fps
  on a 115200-baud Adalight with 112 LEDs, capture → LED latency of a few milliseconds plus the serial wire time.
- **HDR** desktops are tone-mapped; **letterbox / pillarbox black bars** are detected and skipped.
- **Static color** and **mood light** (color cycle, rainbow, breathe).
- **Automatic device detection**, also when the USB cable is moved to another port (new COM number).
  Unknown serial devices are never probed repeatedly, Bluetooth and modem ports never.
- **Phone color calibration**: scan the QR code and point the phone at the screen. RambiBox runs a color loop on the
  screen and the LEDs together (room light → white → dark grays → white → red/green/blue, each frame tagged with a
  visual step code) and corrects white balance and per-channel dark tones live, including the wall's own tint; it
  also measures an LED color matrix that you accept or reject in an A/B comparison by eye. Finally the phone becomes a
  remote to fine-tune white and dark tones by eye (cameras don't see narrow-band LEDs like your eyes do).
  Live camera needs HTTPS: RambiBox creates a name-constrained local root certificate (private IPs and `.local` only,
  key DPAPI-protected in `%AppData%\RambiBox\phone-ca`); the phone installs it once (the page walks you through it).
  Without that setup the phone records a ~25 s video instead. Phone and PC must be on the same network; allow
  RambiBox in the Windows Firewall (button on the Calibration page, private networks only).
- LEDs go off when the display turns off, the PC sleeps or shuts down (and optionally when locked).
- Start with Windows, light/dark theme, per-monitor DPI.

## Files

| What | Where |
|---|---|
| Settings | `%AppData%\RambiBox\settings.json` (+ `.bak`) |
| Log | `%LocalAppData%\RambiBox\logs\rambibox.log` |

Command line: `--tray` (start hidden), `--exit` (close a running instance, LEDs off), `--simulate` (no hardware).

## Build & test

Requires the .NET 10 SDK.

```
dotnet build RambiBox.slnx
dotnet test tests/RambiBox.Tests --filter "Category!=Hardware"
dotnet test tests/RambiBox.Tests --filter "Category=Hardware"   # needs the LED controller + monitor; close RambiBox first
publish.cmd                                                      # single self-contained dist\RambiBox.exe
```

Layout: `src/RambiBox.Core` (engine, capture, Adalight device, calibration, settings), `src/RambiBox.App`
(WPF tray app), `tests/RambiBox.Tests` (xUnit).

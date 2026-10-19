# Contributing to RambiBox

Thanks for your interest! Bug reports, hardware reports ("works with my board"), ideas and pull requests are all
welcome. Please follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting bugs and ideas

- **Bugs:** [open a bug report](https://github.com/Ferrywell/RambiBox/issues/new?template=bug_report.yml). Include your
  board and sketch, the number of LEDs, Windows version, HDR on/off, and the relevant part of the log
  (`%LocalAppData%\RambiBox\logs\rambibox.log`).
- **Ideas:** [open a feature request](https://github.com/Ferrywell/RambiBox/issues/new?template=feature_request.yml).
  For bigger changes, please open an issue first so we can agree on the approach before you write a lot of code.
- **Security issues:** report them privately. See [SECURITY.md](SECURITY.md).

## Development setup

You need:

- Windows 10 (19041+) or Windows 11, x64
- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- any editor; Visual Studio 2022 17.14+, Rider and VS Code with C# Dev Kit all work

Then build and run:

```bat
git clone https://github.com/Ferrywell/RambiBox
cd RambiBox
dotnet build RambiBox.slnx
dotnet run --project src/RambiBox.App -- --simulate
```

### Running without hardware

| Switch | What it does |
|---|---|
| `--simulate` | Uses a simulated LED controller instead of real serial ports. The real screen is still captured. Settings go to `settings.simulate.json`, so your real configuration is never touched. Can run next to an installed RambiBox. |
| `--demo` | Like `--simulate`, but shows a synthetic animated "movie scene" instead of capturing the desktop. Starts from fresh default settings in `%Temp%\RambiBox.Demo` and skips the AmbiBox import. Use it for screenshots: no personal screen content, no paths. |
| `--page=<home\|colors\|static\|layout\|device\|calibration\|settings>` | Opens the window on that page. |
| `--theme=<light\|dark>` | Forces the theme instead of following Windows. |
| `--tray` | Starts hidden in the notification area (used by "Start with Windows"). |
| `--exit` | Asks a running instance to exit cleanly (LEDs off). Combine it with `--simulate` or `--demo` to stop those instances. |

Never run a development build *without* `--simulate`/`--demo` while your installed RambiBox is running: only one
program can open the LED controller's COM port.

## Tests

```bat
dotnet test tests/RambiBox.Tests --filter "Category!=Hardware"
```

About 220 xUnit tests cover the engine, color math, zone math, the Adalight protocol, auto-detection, AmbiBox import,
the calibration analysis (with synthetic phone photos) and the calibration HTTP server. CI runs them on every push
and pull request.

| Category | Needs | Runs in CI |
|---|---|---|
| *(none)* | nothing | yes |
| `Hardware` | the LED controller, a real monitor and GPU desktop duplication. Close RambiBox first. | no |
| `Local` | an AmbiBox installation on this PC | no |

New tests that touch real hardware, the desktop or machine-specific state must carry the matching
`[Trait("Category", "...")]`, so CI stays green on a headless runner.

## Project structure

```
src/RambiBox.Core      engine, capture, Adalight device, calibration, settings (no UI)
  Capture/             DXGI desktop duplication, GPU downscale, HDR, black bars, zone averaging
  Devices/             Adalight protocol, serial link, port enumeration, auto-detect
  Engine/              LightEngine (frame loop, smoothing, overrides), ColorProcessor
  Calibration/         phone calibration: HTTP(S) server, local CA, color loop analysis
  Config/              settings model + JSON, AmbiBox importer, layout generator
src/RambiBox.App       WPF tray app (Fluent theme): views, view models, Windows integration
  Dev/                 SimulatedLedDevice (--simulate), DemoScreenSampler (--demo)
tests/RambiBox.Tests   xUnit tests
docs/                  the website (GitHub Pages, static HTML/CSS)
tools/                 icon generator
```

## Coding guidelines

- Follow the existing style: nullable enabled, file-scoped namespaces, `var` where the type is obvious, XML doc
  comments on public types and on anything non-obvious.
- The build must have **zero warnings**.
- **Performance matters.** RambiBox runs all day:
  - keep the per-frame path allocation-free;
  - avoid timers or polling while the window is closed;
  - measure before and after if you touch capture or the engine.
- **Threading.** Settings are changed on the UI thread through `AppHost.Commit`. The engine works on immutable
  snapshots. Be explicit about which thread a member may be called from.
- **Never surprise other hardware.** Opening a serial port resets many Arduino-like boards. Respect the probing rules
  in `AdalightDevice`.
- **UI text.**
  - Write short, friendly English and avoid jargon.
  - Use "LED controller", not "device", when talking to the user.
  - Make sure everything works with the keyboard and a screen reader (set `AutomationProperties.Name`).
- **Screenshots.** Take screenshots for pull requests and the website with `--demo`.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each pull request focused on one topic. Add or update tests.
3. Make sure that:
   - `dotnet build RambiBox.slnx -c Release` has no warnings;
   - the tests pass.
4. Describe what you tested, including on real hardware if your change touches capture, the device or the calibration.

By contributing, you agree that your contributions are licensed under the
[GNU General Public License v3.0 or later](LICENSE).

## Releases (maintainers)

1. Update `<Version>` in `Directory.Build.props`.
2. Tag the commit and push the tag:

   ```bat
   git tag v1.2.3
   git push origin v1.2.3
   ```

The **Release** workflow then:

- runs the tests;
- builds the single-file `RambiBox.exe` (same as `publish.cmd`);
- computes the SHA-256 checksum;
- creates a GitHub release with generated release notes.

The website is deployed from `docs/` by the **Website** workflow on every push to `main`.

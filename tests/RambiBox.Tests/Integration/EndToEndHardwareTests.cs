using System.Diagnostics;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;
using Xunit.Abstractions;

namespace RambiBox.Tests.Integration;

/// <summary>Full pipeline on the real monitor + LED controller. Run alone: dotnet test --filter Category=Hardware</summary>
[Collection("Hardware")]
public class EndToEndHardwareTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Hardware")]
    public void Real_capture_drives_real_strip_efficiently()
    {
        if (Environment.GetEnvironmentVariable("RAMBIBOX_NO_HW") is not null) return;
        using var device = new AdalightDevice();
        if (!device.ListPorts().Any(p => p.UsbId == "1A86:7523")) return;

        var s = AmbiBoxImporter.TryImport(out _) ?? new AppSettings();
        if (s.Layout.Zones.Count == 0) s.Layout.Zones = LayoutGenerator.Generate(s.Layout);
        s.Color.Brightness = 0.4; // keep it gentle during tests

        using var engine = new LightEngine(new DxgiScreenSampler(), device);
        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        engine.Start(s);

        // connect
        while (engine.GetStats().DeviceStatus != DeviceStatus.Connected && sw.Elapsed.TotalSeconds < 6) Thread.Sleep(50);
        Assert.Equal(DeviceStatus.Connected, engine.GetStats().DeviceStatus);
        output.WriteLine($"Connected after {sw.ElapsedMilliseconds} ms on {engine.GetStats().PortName}");

        // Phase 1: animated content (a window cycling colors) → output should run near link rate
        var cpu0 = proc.TotalProcessorTime;
        var t0 = sw.Elapsed;
        double maxFps = 0;
        using (var anim = new AnimatedWindow())
        {
            for (int i = 0; i < 16; i++)
            {
                Thread.Sleep(500);
                var st = engine.GetStats();
                maxFps = Math.Max(maxFps, st.OutputFps);
                output.WriteLine($"  t={sw.Elapsed.TotalSeconds:F1}s out={st.OutputFps:F1} cap={st.CaptureFps:F1} capMs={st.CaptureMs:F2} procMs={st.ProcessMs:F3} {st.StatusText}");
            }
        }
        proc.Refresh();
        double busyCpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (sw.Elapsed - t0).TotalMilliseconds / Environment.ProcessorCount * 100;
        output.WriteLine($"Animated: max output {maxFps:F1} fps, process CPU {busyCpu:F2}% of machine (test host incl. animation window)");
        Assert.True(maxFps > 25, $"output fps {maxFps}");

        // Phase 2: static desktop → engine should idle
        Thread.Sleep(1000);
        cpu0 = proc.TotalProcessorTime;
        t0 = sw.Elapsed;
        Thread.Sleep(5000);
        proc.Refresh();
        double idleCpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (sw.Elapsed - t0).TotalMilliseconds / Environment.ProcessorCount * 100;
        var idle = engine.GetStats();
        output.WriteLine($"Static: output {idle.OutputFps:F1} fps, captures {idle.CaptureFps:F1}/s, process CPU {idleCpu:F3}% of machine, working set {proc.WorkingSet64 / 1e6:F0} MB");

        // Blackout on stop
        engine.Stop();
        output.WriteLine($"Stopped; total {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>A small topmost window in the bottom-left corner that cycles colors (simulates video).</summary>
    private sealed class AnimatedWindow : IDisposable
    {
        private readonly Thread _t;
        private volatile bool _stop;
        private System.Windows.Forms.Form? _form;

        public AnimatedWindow()
        {
            var ready = new ManualResetEventSlim();
            _t = new Thread(() =>
            {
                var f = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                };
                var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                f.Bounds = new System.Drawing.Rectangle(b.Left, b.Bottom - 400, 600, 400);
                var timer = new System.Windows.Forms.Timer { Interval = 16 };
                int k = 0;
                timer.Tick += (_, _) =>
                {
                    if (_stop) { f.Close(); return; }
                    k++;
                    f.BackColor = System.Drawing.Color.FromArgb((k * 7) % 256, (k * 3) % 256, (k * 5) % 256);
                };
                f.Shown += (_, _) => { timer.Start(); ready.Set(); };
                _form = f;
                System.Windows.Forms.Application.Run(f);
            }) { IsBackground = true };
            _t.SetApartmentState(ApartmentState.STA);
            _t.Start();
            ready.Wait(3000);
        }

        public void Dispose()
        {
            _stop = true;
            _t.Join(2000);
        }
    }
}

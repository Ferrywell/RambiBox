using RambiBox.Core.Updates;

namespace RambiBox.Tests.Updates;

public class UpdateInstallerTests
{
    private const string Current = @"C:\Users\u\AppData\Local\Programs\RambiBox\RambiBox.exe";
    private const string Old = Current + ".old";
    private const string Download = @"C:\Users\u\AppData\Local\RambiBox\updates\1.2.0\RambiBox.exe";
    private const string Token = "0f8fad5bd9cb469fa16570867728950e";

    private static InstallRequest Request(bool tray = false) =>
        new(Current, Download, SemVersion.Parse("1.1.0"), SemVersion.Parse("1.2.0"), tray, Token);

    private sealed class FakeFs : IUpdateFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            [Current] = "v1.1.0",
            [Download] = "v1.2.0",
        };
        /// <summary>Paths that can't be moved or deleted (in use).</summary>
        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Lock that disappears after this many failed deletes (a killed process letting go of its exe).</summary>
        public int LockedDeletesLeft { get; set; } = int.MaxValue;
        public bool FailCopy { get; set; }
        public List<string> Log { get; } = new();

        public bool Exists(string path) => Files.ContainsKey(path);

        public void Move(string from, string to)
        {
            Log.Add($"move {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
            if (Locked.Contains(from)) throw new IOException("in use");
            if (!Files.Remove(from, out var c)) throw new FileNotFoundException(from);
            if (Files.ContainsKey(to)) throw new IOException("exists");
            Files[to] = c;
        }

        public void Copy(string from, string to)
        {
            Log.Add($"copy {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
            if (FailCopy) throw new IOException("disk full");
            if (Files.ContainsKey(to)) throw new IOException("exists");
            Files[to] = Files[from];
        }

        public void Delete(string path)
        {
            Log.Add($"delete {Path.GetFileName(path)}");
            if (Locked.Contains(path))
            {
                if (--LockedDeletesLeft < 0) Locked.Remove(path);
                else throw new IOException("in use");
            }
            Files.Remove(path);
        }
    }

    private sealed class FakeProcess : IUpdateProcess
    {
        public int Id => 4242;
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public void Kill() { Killed = true; HasExited = true; }
        public bool WaitForExit(TimeSpan timeout) => HasExited;
        public void Dispose() { }
    }

    private sealed class FakeLauncher(FakeFs fs) : IUpdateProcessLauncher
    {
        public List<(string Path, string Content, IReadOnlyList<string> Args)> Started { get; } = new();
        public FakeProcess Process { get; } = new();
        public bool Fail { get; set; }

        public IUpdateProcess Start(string exePath, IReadOnlyList<string> args)
        {
            if (Fail) throw new System.ComponentModel.Win32Exception("blocked");
            Started.Add((exePath, fs.Files.GetValueOrDefault(exePath, "?"), args));
            return Process;
        }
    }

    private sealed class FakeHandshakes(HandshakeOutcome outcome, Action<FakeProcess>? onWait = null) : IUpdateHandshakeFactory
    {
        public List<string> Tokens { get; } = new();
        public TimeSpan? WaitedFor { get; private set; }

        public IUpdateHandshake Create(string token)
        {
            Tokens.Add(token);
            return new H(this);
        }

        private HandshakeOutcome OnWait(IUpdateProcess process, TimeSpan timeout)
        {
            WaitedFor = timeout;
            onWait?.Invoke((FakeProcess)process);
            return outcome;
        }

        private sealed class H(FakeHandshakes f) : IUpdateHandshake
        {
            public HandshakeOutcome Wait(IUpdateProcess process, TimeSpan timeout) => f.OnWait(process, timeout);
            public void Dispose() { }
        }
    }

    private static UpdateInstaller Make(FakeFs fs, FakeLauncher launcher, FakeHandshakes hs) => new(fs, launcher, hs, _ => { });

    [Fact]
    public void Success_swaps_the_exe_and_starts_the_new_version_with_the_handshake()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs);
        var hs = new FakeHandshakes(HandshakeOutcome.Ok);

        var r = Make(fs, launcher, hs).Install(Request(tray: true));

        Assert.Equal(InstallOutcome.Installed, r.Outcome);
        Assert.Equal("v1.2.0", fs.Files[Current]);
        Assert.Equal("v1.1.0", fs.Files[Old]); // still running from here; deleted at a later start
        var start = Assert.Single(launcher.Started);
        Assert.Equal(Current, start.Path);
        Assert.Equal("v1.2.0", start.Content);
        Assert.Equal(["--updated-from", "1.1.0", "--update-token", Token, "--tray"], start.Args);
        Assert.Equal([Token], hs.Tokens);
        Assert.Equal(UpdateInstaller.DefaultHandshakeTimeout, hs.WaitedFor);
        Assert.Equal(["move RambiBox.exe -> RambiBox.exe.old", "copy RambiBox.exe -> RambiBox.exe"], fs.Log);
    }

    [Fact]
    public void Window_open_means_no_tray_argument()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs);
        Make(fs, launcher, new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request(tray: false));
        Assert.DoesNotContain("--tray", launcher.Started[0].Args);
    }

    [Fact]
    public void New_version_crashing_during_startup_is_rolled_back()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs);
        var hs = new FakeHandshakes(HandshakeOutcome.ProcessExited, p => p.HasExited = true);
        var installer = Make(fs, launcher, hs);

        var r = installer.Install(Request());

        Assert.Equal(InstallOutcome.RolledBack, r.Outcome);
        Assert.Contains("exited", r.Detail);
        Assert.Equal("v1.1.0", fs.Files[Current]);
        Assert.False(fs.Exists(Old));
        Assert.False(launcher.Process.Killed);
        Assert.Equal(Current, r.RelaunchPath);

        // the old version is restarted and told about the failure
        Assert.True(installer.RelaunchAfterFailure(Request(), r));
        Assert.Equal(Current, launcher.Started[^1].Path);
        Assert.Equal("v1.1.0", launcher.Started[^1].Content);
        Assert.Equal(["--update-failed", "1.2.0"], launcher.Started[^1].Args);
    }

    [Fact]
    public void New_version_that_never_signals_is_killed_and_rolled_back()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs);
        var r = Make(fs, launcher, new FakeHandshakes(HandshakeOutcome.TimedOut)).Install(Request());

        Assert.Equal(InstallOutcome.RolledBack, r.Outcome);
        Assert.Contains("45 s", r.Detail);
        Assert.True(launcher.Process.Killed);
        Assert.Equal("v1.1.0", fs.Files[Current]);
        Assert.False(fs.Exists(Old));
    }

    [Fact]
    public void Killed_process_still_holding_its_exe_for_a_moment_is_waited_for()
    {
        var fs = new FakeFs { LockedDeletesLeft = 3 };
        var launcher = new FakeLauncher(fs);
        var hs = new FakeHandshakes(HandshakeOutcome.TimedOut, _ => fs.Locked.Add(Current));
        var r = Make(fs, launcher, hs).Install(Request());
        Assert.Equal(InstallOutcome.RolledBack, r.Outcome);
        Assert.Equal("v1.1.0", fs.Files[Current]);
    }

    [Fact]
    public void New_exe_that_stays_locked_reports_rollback_failed_and_relaunches_the_old_file()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs);
        var hs = new FakeHandshakes(HandshakeOutcome.TimedOut, _ => fs.Locked.Add(Current));
        var installer = Make(fs, launcher, hs);
        var r = installer.Install(Request());

        Assert.Equal(InstallOutcome.RollbackFailed, r.Outcome);
        Assert.Equal(Old, r.RelaunchPath);
        Assert.Equal("v1.1.0", fs.Files[Old]);
        installer.RelaunchAfterFailure(Request(), r);
        Assert.Equal("v1.1.0", launcher.Started[^1].Content);
    }

    [Fact]
    public void Copy_failure_restores_the_old_exe_without_starting_anything()
    {
        var fs = new FakeFs { FailCopy = true };
        var launcher = new FakeLauncher(fs);
        var hs = new FakeHandshakes(HandshakeOutcome.Ok);
        var r = Make(fs, launcher, hs).Install(Request());

        Assert.Equal(InstallOutcome.RolledBack, r.Outcome);
        Assert.Contains("copy", r.Detail);
        Assert.Equal("v1.1.0", fs.Files[Current]);
        Assert.False(fs.Exists(Old));
        Assert.Empty(launcher.Started);
        Assert.Empty(hs.Tokens);
    }

    [Fact]
    public void Running_exe_that_cannot_be_renamed_changes_nothing()
    {
        var fs = new FakeFs();
        fs.Locked.Add(Current);
        var launcher = new FakeLauncher(fs);
        var r = Make(fs, launcher, new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request());

        Assert.Equal(InstallOutcome.NotStarted, r.Outcome);
        Assert.Equal("v1.1.0", fs.Files[Current]);
        Assert.False(fs.Exists(Old));
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public void Locked_leftover_old_file_changes_nothing()
    {
        var fs = new FakeFs();
        fs.Files[Old] = "v1.0.0";
        fs.Locked.Add(Old);
        var r = Make(fs, new FakeLauncher(fs), new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request());
        Assert.Equal(InstallOutcome.NotStarted, r.Outcome);
        Assert.Contains("RambiBox.exe.old", r.Detail);
        Assert.Equal("v1.1.0", fs.Files[Current]);
    }

    [Fact]
    public void Unlocked_leftover_old_file_is_replaced()
    {
        var fs = new FakeFs();
        fs.Files[Old] = "v1.0.0";
        var r = Make(fs, new FakeLauncher(fs), new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request());
        Assert.Equal(InstallOutcome.Installed, r.Outcome);
        Assert.Equal("v1.1.0", fs.Files[Old]);
    }

    [Fact]
    public void Start_failure_is_rolled_back()
    {
        var fs = new FakeFs();
        var launcher = new FakeLauncher(fs) { Fail = true };
        var r = Make(fs, launcher, new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request());
        Assert.Equal(InstallOutcome.RolledBack, r.Outcome);
        Assert.Equal("v1.1.0", fs.Files[Current]);
        Assert.False(fs.Exists(Old));
    }

    [Fact]
    public void Missing_download_changes_nothing()
    {
        var fs = new FakeFs();
        fs.Files.Remove(Download);
        var r = Make(fs, new FakeLauncher(fs), new FakeHandshakes(HandshakeOutcome.Ok)).Install(Request());
        Assert.Equal(InstallOutcome.NotStarted, r.Outcome);
        Assert.Empty(fs.Log);
    }

    [Fact]
    public void Handshake_tokens_are_guids()
    {
        Assert.True(UpdateHandshake.IsValidToken(Token));
        Assert.True(UpdateHandshake.IsValidToken(Guid.NewGuid().ToString()));
        Assert.False(UpdateHandshake.IsValidToken(@"..\..\Global\x"));
        Assert.False(UpdateHandshake.IsValidToken(null));
        Assert.False(UpdateHandshake.Signal(@"not-a-token"));
        Assert.False(UpdateHandshake.Signal(Guid.NewGuid().ToString("N"))); // nobody waiting
    }

    [Fact]
    public void Real_handshake_sees_the_signal_of_the_new_process()
    {
        var token = Guid.NewGuid().ToString("N");
        using var hs = new RealUpdateHandshakeFactory().Create(token);
        Assert.True(UpdateHandshake.Signal(token));
        Assert.Equal(HandshakeOutcome.Ok, hs.Wait(new FakeProcess(), TimeSpan.FromSeconds(2)));

        using var hs2 = new RealUpdateHandshakeFactory().Create(Guid.NewGuid().ToString("N"));
        Assert.Equal(HandshakeOutcome.ProcessExited, hs2.Wait(new FakeProcess { HasExited = true }, TimeSpan.FromSeconds(2)));
        Assert.Equal(HandshakeOutcome.TimedOut, hs2.Wait(new FakeProcess(), TimeSpan.FromMilliseconds(300)));
    }
}

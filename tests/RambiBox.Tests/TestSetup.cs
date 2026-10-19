using System.Runtime.CompilerServices;

namespace RambiBox.Tests;

internal static class TestSetup
{
    /// <summary>Runs before any test touches RambiBox.Core.Log: keep test output out of the user's real log file.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("RAMBIBOX_LOG_DIR", Path.Combine(Path.GetTempPath(), "RambiBoxTests", "logs"));
}

using System.Runtime.CompilerServices;

namespace RambiBox.Tests;

internal static class TestSetup
{
    /// <summary>Runs before any test touches RambiBox.Core.Log: keep test output out of the user's real log file.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("RAMBIBOX_LOG_DIR", Path.Combine(Path.GetTempPath(), "RambiBoxTests", "logs"));
        // Many tests wait synchronously while fake servers run on the thread pool. On a 2-core CI runner the pool
        // starts with 2 workers and grows slowly, which made loopback servers answer seconds late. Start wider.
        ThreadPool.GetMinThreads(out int worker, out int io);
        ThreadPool.SetMinThreads(Math.Max(worker, 32), Math.Max(io, 32));
    }
}

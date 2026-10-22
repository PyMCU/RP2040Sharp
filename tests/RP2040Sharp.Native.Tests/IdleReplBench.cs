using System.Diagnostics;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Throughput of the idle MicroPython REPL (RPI_PICO v1.28.0) under st_rp2040_run_until_edge with every pad
/// watched, as Proteus drives it. A measurement, not a check: it runs only with RP2040_NATIVE_PERF=1.
/// </summary>
public class IdleReplBench(ITestOutputHelper output)
{
    private const uint AllPads = (1u << 30) - 1;

    [SkippableTheory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(0u)] // plain st_rp2040_run, for reference
    public void Idle_repl_throughput(uint guard)
    {
        Skip.If(Environment.GetEnvironmentVariable("RP2040_NATIVE_PERF") != "1", "set RP2040_NATIVE_PERF=1 to measure");
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var (h, host) = MicroPythonTests.BootToRepl(uf2!, 0);
        try
        {
            int from = host.Cdc.Length;
            Abi.SerialWrite(h, 2, "print('ready')\r");
            Assert.True(MicroPythonTests.RunUntil(h, host, "ready", 125_000_000, from: from));
            const ulong simulated = 125_000_000 / 5; // 200 ms
            var start = Abi.Cycles(h);
            long calls = 0;
            var sw = Stopwatch.StartNew();
            while (Abi.Cycles(h) - start < simulated)
            {
                if (guard == 0) Abi.Run(h, 1_000_000);
                else Abi.RunUntilEdge(h, 1_000_000, guard, AllPads);
                calls++;
            }
            sw.Stop();
            var cycles = Abi.Cycles(h) - start;
            var ratio = cycles / 125e6 / sw.Elapsed.TotalSeconds;
            output.WriteLine($"guard {guard}: {cycles / 125_000.0:F0} ms simulated in {sw.Elapsed.TotalMilliseconds:F0} ms wall " +
                             $"= {ratio:F3}x real time, {cycles / sw.Elapsed.TotalSeconds / 1e6:F1} Mcycles/s, {calls} calls");
            Assert.Equal(0, Abi.Status(h));
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }
}

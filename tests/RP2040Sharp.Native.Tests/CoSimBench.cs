using System.Diagnostics;
using RP2040.Peripherals;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Co-simulation throughput under st_rp2040_run_until_edge with every pad watched, as Proteus drives it: the idle
/// MicroPython REPL (RPI_PICO v1.28.0), the same REPL with a 1 kHz PWM on GP14, and a hand-assembled busy loop on
/// core 0; plus the plain interpreter (Machine.Run) for parity checks. Measurements, not checks: they run only with
/// RP2040_NATIVE_PERF=1. Best of three, wall time.
/// </summary>
public class CoSimBench(ITestOutputHelper output)
{
    private const uint AllPads = (1u << 30) - 1;

    private static void SkipUnlessPerf() =>
        Skip.If(Environment.GetEnvironmentVariable("RP2040_NATIVE_PERF") != "1", "set RP2040_NATIVE_PERF=1 to measure");

    [SkippableTheory]
    [InlineData("idle", 1u)]
    [InlineData("idle", 75u)]
    [InlineData("idle", 300u)]
    [InlineData("pwm", 1u)]
    [InlineData("pwm", 75u)]
    [InlineData("pwm", 300u)]
    public void MicroPython_throughput(string workload, uint guard)
    {
        SkipUnlessPerf();
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var (h, host) = MicroPythonTests.BootToRepl(uf2!, 0);
        try
        {
            int from = host.Cdc.Length;
            Abi.SerialWrite(h, 2, workload == "pwm"
                ? "from machine import Pin, PWM\rp=PWM(Pin(14), freq=1000, duty_u16=20000)\rprint('rea'+'dy')\r"
                : "print('rea'+'dy')\r");
            Assert.True(MicroPythonTests.RunUntil(h, host, "ready", 125_000_000, from: from));
            Measure(h, workload, guard, 125_000_000 / 5, host);
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    [SkippableTheory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(300u)]
    public void Busy_loop_throughput(uint guard)
    {
        SkipUnlessPerf();
        var h = Abi.Create(0);
        try
        {
            Assert.Equal(1, Abi.LoadUf2(h, Firmware.Uf2(Firmware.BusyLoop())));
            Measure(h, "busy", guard, 125_000_000 / 20, null);
        }
        finally { Abi.Destroy(h); }
    }

    /// <summary>
    /// Best of three per mode, the modes alternated on the same chip: plain slices with every peripheral ticked per
    /// slice (the reference, as before deferred ticks) against the defaults (deferred ticks, stretches, bounded runs).
    /// </summary>
    private void Measure(nint h, string workload, uint guard, ulong simulated, Host? host)
    {
        var chip = SiliconTwin.Native.Exports.Lookup(h)!;
        var best = new[] { double.MaxValue, double.MaxValue };
        var calls = new long[2];
        for (int rep = 0; rep < 6; rep++)
        {
            int mode = rep % 2;   // 0 = plain/eager, 1 = fast
            chip.DeferPeripheralTicks = mode == 1;
            chip.SleepingStretches = chip.AwakeBoundedRuns = mode == 1;
            var start = Abi.Cycles(h);
            calls[mode] = 0;
            var sw = Stopwatch.StartNew();
            while (Abi.Cycles(h) - start < simulated)
            {
                Abi.RunUntilEdge(h, 1_000_000, guard, AllPads);
                calls[mode]++;
            }
            sw.Stop();
            best[mode] = Math.Min(best[mode], sw.Elapsed.TotalSeconds / ((Abi.Cycles(h) - start) / 125e6));
            Assert.Equal(0, Abi.Status(h));
        }
        output.WriteLine($"{workload} guard {guard}: plain {best[0]:F4} -> fast {best[1]:F4} host s per simulated s " +
                         $"({1 / best[0]:F2}x -> {1 / best[1]:F2}x real time), {calls[1]} calls per {simulated / 125_000.0:F0} ms");
    }

    /// <summary>The interpreter alone (RP2040Machine.Run, 16 Ki-instruction slices, every peripheral ticked per slice):
    /// MIPS of the busy loop, for checking that the CPU loop did not get slower.</summary>
    [SkippableFact]
    public void Plain_interpreter_mips()
    {
        SkipUnlessPerf();
        using var m = new RP2040Machine();
        m.LoadFlash(Firmware.BusyLoop());
        m.Run(1_000_000);
        double best = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 2_000; i++) m.Run(16_384);
            sw.Stop();
            best = Math.Max(best, 2_000 * 16_384 / sw.Elapsed.TotalSeconds / 1e6);
        }
        output.WriteLine($"plain Machine.Run busy loop: {best:F1} MIPS (best of 5)");
    }
}

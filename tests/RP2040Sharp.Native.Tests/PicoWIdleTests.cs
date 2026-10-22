using System.Diagnostics;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The Pico W image (MicroPython v1.28.0 RPI_PICO_W, ST_RP2040_CYW43) after <c>WLAN(STA_IF).active(True)</c>, idling at
/// the REPL while the host types into it, driven as the Proteus model drives it: run_until_edge at guard 75 with the
/// pads of the symbol watched (the W symbol has no GP23/24/25/29). Once the interface is up, cyw43's gSPI state
/// machine (PIO1 SM0) waits in <c>out pins, 1</c> on autopull with its TX FIFO empty; it used to be due at every divider
/// edge (clkdiv 2), which refused every sleeping stretch and bounded run (<see cref="Idle_after_wlan_active_probe"/>).
/// </summary>
public class PicoWIdleTests(ITestOutputHelper output)
{
    private const ulong Ms = 125_000;
    internal const uint ProteusWatch = DiffRun.AllPads & ~((1u << 23) | (1u << 24) | (1u << 25) | (1u << 29));

    /// <summary>Host lines and the simulated time (ms after WLAN is up) at which the host types them.</summary>
    private static readonly (ulong AtMs, string Line)[] Script =
    [
        (5, "1+1\r"),
        (15, "w.active()\r"),
        (25, "w.status()\r"),
        (35, "w.ifconfig()\r"),
        (45, "w.config('mac')\r"),
        (60, "print('x'*60)\r"),
        (75, "import time; time.sleep_ms(3); print('slept')\r"),
    ];

    private static void BootAndWlan(DiffRun run, byte[] image, uint guard, uint watch)
    {
        Assert.True(Abi.LoadUf2(run.H, image) > 1000);
        var end = run.Cycles + 20 * 1000 * Ms;
        while (run.Cycles < end && !run.Host.Cdc.Contains(">>> ", StringComparison.Ordinal)) run.Run(1_000_000);
        Assert.Contains(">>> ", run.Host.Cdc);
        int from = run.Host.Cdc.Length;
        Abi.SerialWrite(run.H, 2, MicroPythonDifferentialTests.WlanLine);
        end = run.Cycles + 30 * 1000 * Ms;
        while (run.Cycles < end && run.Host.Cdc.IndexOf("wlan True 6", from, StringComparison.Ordinal) < 0)
            run.RunUntilEdge(30_000, guard, watch);
        Assert.Contains("wlan True 6", run.Host.Cdc);
    }

    /// <summary>
    /// Boot (plain runs), WLAN.active(True) and 100 ms of the idle REPL taking host lines, at the Proteus model's call
    /// size: the deferred and fast paths (stalled-SM deadlines on) against the eager machine (off: the gSPI SM re-run at
    /// every divider edge). Identical calls, pad events with cycles, CDC chunks with cycles, cores, SRAM, peripherals.
    /// </summary>
    [SkippableTheory]
    [InlineData(75u, ProteusWatch)]
    [InlineData(75u, DiffRun.AllPads)]
    [InlineData(300u, ProteusWatch)]
    public void Wlan_idle_is_the_same_in_every_mode(uint guard, uint watch)
    {
        var uf2 = Firmware.MicroPythonPicoW();
        Skip.If(uf2 is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        var image = File.ReadAllBytes(uf2!);
        string cdc = "";
        int pads = 0;
        var probe = Differential.Check(run =>
        {
            BootAndWlan(run, image, guard, watch);
            var t0 = run.Cycles;
            int next = 0;
            while (run.Cycles < t0 + 100 * Ms)
            {
                if (next < Script.Length && run.Cycles >= t0 + Script[next].AtMs * Ms)
                    Abi.SerialWrite(run.H, 2, Script[next++].Line);
                run.RunUntilEdge(30_000, guard, watch);
            }
            cdc = run.Host.Cdc;
            pads = run.Host.Pins.Count;
        }, flags: Abi.Cyw43);
        output.WriteLine($"guard {guard} watch 0x{watch:X8}: stretched {probe.Stretched}, bounded {probe.Bounded}, plain {probe.Plain}, {pads} pad events");
        output.WriteLine(cdc[Math.Max(0, cdc.Length - 400)..].Replace("\r", "").Replace("\n", " | "));
        Assert.Contains("slept", cdc);
        Assert.Contains("xxxxxxxxxx", cdc);
        Assert.Contains("0.0.0.0", cdc);
        Assert.True(pads > 1000, "the gSPI traffic moved the pads");
    }

    /// <summary>Where the idle time goes and the host time per simulated second, with and without stalled-SM
    /// deadlines. Prints only.</summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Idle_after_wlan_active_probe(bool stalledDeadlines)
    {
        var uf2 = Firmware.MicroPythonPicoW();
        Skip.If(uf2 is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        using var run = new DiffRun(SliceMode.Fast, Abi.Cyw43);
        var m = run.Chip.Machine;
        m.Pio0.StalledDeadlines = m.Pio1.StalledDeadlines = stalledDeadlines;
        BootAndWlan(run, File.ReadAllBytes(uf2!), 75, ProteusWatch);
        for (int i = 0; i < 20; i++) run.RunUntilEdge(30_000, 75, ProteusWatch);
        foreach (var pio in new[] { m.Pio0, m.Pio1 })
            for (int s = 0; s < 4; s++)
            {
                var st = pio.GetSmState(s);
                if (!st.Enabled) continue;
                output.WriteLine($"PIO{pio.BlockIndex} SM{s}: pc={st.Pc} instr=0x{pio.GetProgram()[st.Pc]:X4} {st} next event {pio.CyclesUntilEvent()}");
            }
        var c = run.Chip;
        long s0 = c.SleepStretchCycles, b0 = c.AwakeRunSlots, p0 = c.PlainSlices;
        var start = run.Cycles;
        var sw = Stopwatch.StartNew();
        while (run.Cycles < start + 200 * Ms) run.RunUntilEdge(30_000, 75, ProteusWatch);
        sw.Stop();
        var idle = (double)(run.Cycles - start);
        output.WriteLine($"stalled deadlines {stalledDeadlines}: {sw.Elapsed.TotalSeconds / (idle / 125e6):F3} host s per simulated s; " +
                         $"stretched {c.SleepStretchCycles - s0} of {idle} cycles, bounded {c.AwakeRunSlots - b0} slots, plain {c.PlainSlices - p0} slices");
    }
}

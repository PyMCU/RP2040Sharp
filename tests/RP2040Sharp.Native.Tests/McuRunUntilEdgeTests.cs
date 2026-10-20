using RP2040.Peripherals;
using RP2040.Peripherals.PinLevel;
using SiliconTwin.Abstractions;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// RP2040Mcu.RunUntilEdge, what a generic st_mcu_* library's run_until_edge calls (McuDescriptor.RunUntilEdge), runs the
/// co-simulation loop as st_rp2040_run_until_edge does: the same calls on the same pico-sdk firmware return after the
/// same slices, with the same pad events at the same cycles. Mirrors RP2350Sharp's StMcuRunUntilEdgeTests (which drives
/// st_mcu_run_until_edge itself; no st_mcu_* library is built from this repository).
/// </summary>
public class McuRunUntilEdgeTests(ITestOutputHelper output)
{
    private const uint Watch = (1u << 30) - 1;

    private static string? Image(string dir, string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "RP2040Sharp.IntegrationTests", "Firmware", dir, name + ".uf2");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // Driven state (level AND output enable, output enable) changes, as SiliconTwin.Abstractions reports them.
    private static List<(int, bool, bool, long)> Driven(IEnumerable<(int Gpio, bool Level, bool Oe, long Cycle)> events)
    {
        var last = new Dictionary<int, (bool, bool)>();
        var r = new List<(int, bool, bool, long)>();
        foreach (var e in events)
        {
            var s = (e.Level && e.Oe, e.Oe);
            if (last.TryGetValue(e.Gpio, out var p) ? p == s : s == (false, false)) { last[e.Gpio] = s; continue; }
            last[e.Gpio] = s;
            r.Add((e.Gpio, s.Item1, s.Item2, e.Cycle));
        }
        return r;
    }

    private static (List<long> Calls, List<(int, bool, bool, long)> Pads) Native(byte[] uf2, int calls, uint max, uint guard)
    {
        var h = Abi.CreateEx(0, Abi.BusAll);
        var host = new Host();
        try
        {
            Abi.Attach(h, host);
            Assert.True(Abi.LoadUf2(h, uf2) > 0);
            var c = new List<long>();
            ulong before = Abi.Cycles(h);
            for (int i = 0; i < calls; i++)
            {
                ulong now = Abi.RunUntilEdge(h, max, guard, Watch);
                c.Add((long)(now - before));
                before = now;
                if (c[^1] >= max / 2) break;   // no more edges: the budgets (slots here, cycles there) differ
            }
            Assert.True(Abi.Status(h) == 0, Abi.LastError(h));
            return (c, Driven(host.Pins.Select(p => (p.Gpio, p.Level != 0, p.Oe != 0, (long)p.Cycle))));
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    private static (List<long> Calls, List<(int, bool, bool, long)> Pads) Mcu(byte[] uf2, int calls, long max, int guard)
    {
        using var mcu = new RP2040Mcu(new RP2040McuOptions { PinLevelBuses = PinLevelBus.All }) { MaxSliceInstructions = 512 };
        mcu.Load(FirmwareImage.FromUf2(uf2));
        mcu.Reset();
        var events = new List<(int, bool, bool, long)>();
        mcu.Banks[0].PinChanged += (in PinChange e) =>
        {
            for (var m = e.Changed; m != 0; m &= m - 1)
            {
                int g = System.Numerics.BitOperations.TrailingZeroCount(m);
                events.Add((g, (e.Levels >> g & 1) != 0, (e.OutputEnable >> g & 1) != 0, e.Cycle));
            }
        };
        var c = new List<long>();
        for (int i = 0; i < calls; i++)
        {
            long before = mcu.Cycles;
            mcu.RunUntilEdge(max, guard, Watch);
            c.Add(mcu.Cycles - before);
        }
        return (c, Driven(events));
    }

    [SkippableTheory]
    [InlineData("gpio", "blink", 6, 75, 100_000_000)]
    [InlineData("gen", "uart bursts", 400, 75, 4_000_000)]
    [InlineData("gen", "uart bursts", 400, 1, 4_000_000)]
    public void Mcu_run_until_edge_returns_where_st_rp2040_run_until_edge_does(string dir, string firmware, int calls, int guard, int max)
    {
        // blink: GP25 every 250 ms, asleep in between (sleeping stretches). uart bursts: pin-level UART0 TX on GP0 at
        // 8N1, 125 cycles a bit (the event guard, bounded runs).
        byte[] uf2;
        if (dir == "gen") uf2 = Firmware.Uf2(PinLevelFirmware.UartBursts(7, 52, 0x60, bursts: 3));
        else
        {
            var path = Image(dir, firmware);
            Skip.If(path is null, $"{dir}/{firmware}.uf2 not found");
            uf2 = File.ReadAllBytes(path!);
        }
        var native = Native(uf2, calls, (uint)max, (uint)guard);
        int n = native.Calls.Count;
        bool tail = native.Calls[^1] >= max / 2;   // the last call ran out of edges: left out
        var mcu = Mcu(uf2, n, max, guard);
        if (tail) { native.Calls.RemoveAt(n - 1); mcu.Calls.RemoveAt(n - 1); }
        long upTo = native.Calls.Sum();
        native.Pads.RemoveAll(e => e.Item4 > upTo);
        mcu.Pads.RemoveAll(e => e.Item4 > upTo);
        output.WriteLine($"{firmware} guard {guard}: {native.Calls.Count} calls, {native.Pads.Count} pad changes (st_rp2040), " +
                         $"{mcu.Pads.Count} (RP2040Mcu); calls {string.Join(",", native.Calls.Take(6))} ...");
        Assert.True(native.Calls.Count >= 4, "calls that stopped on an edge");
        Assert.Equal(native.Calls, mcu.Calls);
        Assert.Equal(native.Pads, mcu.Pads);
    }
}

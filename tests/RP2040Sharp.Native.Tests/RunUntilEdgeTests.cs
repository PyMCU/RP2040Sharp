using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// st_rp2040_run_until_edge: it stops after the slice of a watched pad change, and the cycle of a change a CPU
/// instruction makes is that instruction's, whatever the guard (not the end of the slice).
/// </summary>
public class RunUntilEdgeTests
{
    private const int Gp15 = 15;
    private const uint Watch = 1u << Gp15;
    private const ulong Span = 125 * 1000; // 1 ms of the square wave

    private sealed record Trace(List<(int Gpio, int Level, int Oe, ulong Cycle)> Pins, List<(ulong Returned, int Events)> Calls);

    private static Trace RunWave(uint guard, uint watch = Watch, uint max = 1_000_000)
    {
        var h = Abi.Create(0);
        using var host = new Host();
        try
        {
            Assert.Equal(1, Abi.LoadUf2(h, Firmware.Uf2(Firmware.SquareWave(20, 37))));
            Abi.Attach(h, host);
            var calls = new List<(ulong, int)>();
            while (Abi.Cycles(h) < Span)
            {
                int before = host.Pins.Count;
                var c = Abi.RunUntilEdge(h, max, guard, watch);
                calls.Add((c, host.Pins.Count - before));
                Assert.Equal(0, Abi.Status(h));
            }
            return new Trace([.. host.Pins], calls);
        }
        finally { Abi.Destroy(h); }
    }

    [Fact]
    public void Square_wave_has_the_expected_edges()
    {
        var t = RunWave(1);
        // OUT_SET (level 1, not driven yet), OE_SET (driven), then a falling and a rising edge per period of 57 us.
        Assert.Equal((Gp15, 1, 0), (t.Pins[0].Gpio, t.Pins[0].Level, t.Pins[0].Oe));
        Assert.Equal((Gp15, 1, 1), (t.Pins[1].Gpio, t.Pins[1].Level, t.Pins[1].Oe));
        Assert.Equal(t.Pins[0].Cycle + 1, t.Pins[1].Cycle); // consecutive str instructions
        var edges = t.Pins.Skip(2).ToList();
        Assert.InRange(edges.Count, 2 * 16, 2 * 18);
        for (int i = 0; i < edges.Count; i++) Assert.Equal(i % 2 == 0 ? 0 : 1, edges[i].Level);
        // Low for 37 us, high for 20 us, at 125 cycles per microsecond: each wait starts at a fraction of a microsecond
        // (it reads the timer after the edge) and overshoots by a loop or two.
        for (int i = 0; i + 1 < edges.Count; i += 2)
            Assert.InRange((long)(edges[i + 1].Cycle - edges[i].Cycle), 36 * 125, 37 * 125 + 20);
        for (int i = 1; i + 1 < edges.Count; i += 2)
            Assert.InRange((long)(edges[i + 1].Cycle - edges[i].Cycle), 19 * 125, 20 * 125 + 20);
    }

    [Theory]
    [InlineData(75u)]
    [InlineData(300u)]
    public void Every_guard_sees_the_same_pad_events_at_the_same_cycles(uint guard)
    {
        var fine = RunWave(1);
        var coarse = RunWave(guard);
        int n = Math.Min(fine.Pins.Count, coarse.Pins.Count);
        Assert.True(n > 30);
        Assert.Equal(fine.Pins.Take(n), coarse.Pins.Take(n));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(300u)]
    public void Low_pulse_width_is_exact_at_every_guard(uint guard)
    {
        var t = RunWave(guard);
        // The first low pulse: falling edge (index 2) to rising edge (index 3).
        var width = t.Pins[3].Cycle - t.Pins[2].Cycle;
        Assert.Equal(RunWave(1).Pins[3].Cycle - RunWave(1).Pins[2].Cycle, width);
        Assert.InRange((long)width, 36 * 125, 37 * 125 + 20);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(300u)]
    public void It_stops_after_the_slice_of_a_watched_change(uint guard)
    {
        var t = RunWave(guard);
        // Every call but the last returned because of an edge, within one slice (guard instructions, a few cycles
        // each) of it; no call ran past an edge into the next one.
        var stops = t.Calls.Where(c => c.Events > 0).ToList();
        Assert.True(stops.Count >= 30);
        foreach (var (returned, events) in t.Calls.Take(t.Calls.Count - 1)) Assert.True(events > 0);
        int seen = 0;
        foreach (var (returned, events) in t.Calls)
        {
            seen += events;
            if (events == 0) continue;
            var last = t.Pins[seen - 1].Cycle;
            Assert.True(returned >= last);
            Assert.True(returned - last < guard * 6 + 6, $"guard {guard}: returned {returned - last} cycles after the edge");
        }
        // Edges 20 us apart never share a call, except the two setup writes (one instruction apart).
        Assert.All(t.Calls.Skip(1), c => Assert.True(c.Events <= 1));
    }

    [Fact]
    public void Unwatched_or_unguarded_run_is_a_plain_run()
    {
        var a = Abi.Create(0);
        var b = Abi.Create(0);
        var c = Abi.Create(0);
        try
        {
            var uf2 = Firmware.Uf2(Firmware.SquareWave(20, 37));
            Abi.LoadUf2(a, uf2); Abi.LoadUf2(b, uf2); Abi.LoadUf2(c, uf2);
            var ra = Abi.Run(a, 200_000);
            Assert.Equal(ra, Abi.RunUntilEdge(b, 200_000, 75, 0));
            Assert.Equal(ra, Abi.RunUntilEdge(c, 200_000, 0, Watch));
        }
        finally { Abi.Destroy(a); Abi.Destroy(b); Abi.Destroy(c); }
    }

    [Fact]
    public void Sleeping_cores_take_twice_the_guard()
    {
        var h = Abi.Create(0);
        using var host = new Host();
        try
        {
            Abi.LoadUf2(h, Firmware.Uf2(Firmware.SleepForever()));
            Abi.Attach(h, host);
            Abi.RunUntilEdge(h, 100, 1, Watch); // routes GP15 and drives it: stops right there
            Assert.Single(host.Pins);
            Assert.Equal((Gp15, 0, 1), (host.Pins[0].Gpio, host.Pins[0].Level, host.Pins[0].Oe));
            var chip = Exports.Lookup(h)!;
            Abi.RunUntilEdge(h, 100, 1, Watch);
            Assert.True(chip.Machine.Core0Waiting);
            var before = Abi.Cycles(h);
            Abi.RunUntilEdge(h, 1000, 10, Watch);
            // A sleeping core spends one cycle per instruction slot: all 1000 ran, in slices of 20.
            Assert.Equal(before + 1000, Abi.Cycles(h));
            Assert.Single(host.Pins);
        }
        finally { Abi.Destroy(h); }
    }

    // PWM slice 7 on GP14 (DIV 3, TOP 1499, CC 500 + 7 per wrap, Firmware.PwmIsr): each edge carries the cycle of the
    // counter step that made it (RP2040Machine.ExactPwmPadEdges), not the end of the slice it was found in: the period
    // is 1500 x 3 cycles exactly at every guard. (The counter starts counting with the slice the enable write is in, so
    // the phase follows where that slice starts: a coarse guard's edges are the fine guard's moved by a constant.)
    [Theory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(300u)]
    public void Pwm_edges_carry_their_counter_step_at_every_guard(uint guard)
    {
        List<long> Rises(uint g)
        {
            var h = Abi.Create(0);
            using var host = new Host();
            try
            {
                Assert.Equal(1, Abi.LoadUf2(h, Firmware.Uf2(Firmware.PwmIsr())));
                Abi.Attach(h, host);
                while (Abi.Cycles(h) < 200_000) Abi.RunUntilEdge(h, 1_000_000, g, 1u << 14);
                return host.Pins.Where(p => p.Gpio == 14 && p.Level == 1 && p.Oe == 1).Skip(1).Select(p => (long)p.Cycle).ToList();
            }
            finally { Abi.Destroy(h); }
        }
        var rises = Rises(guard);
        Assert.True(rises.Count > 20);
        Assert.Equal([4500L], rises.Zip(rises.Skip(1), (a, b) => b - a).Distinct());
        var fine = Rises(1);
        Assert.Single(fine.Zip(rises, (a, b) => b - a).Distinct());
    }
}

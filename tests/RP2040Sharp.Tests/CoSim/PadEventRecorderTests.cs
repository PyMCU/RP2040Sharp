using RP2040.CoSim;
using RP2040.Peripherals;

namespace RP2040.CoSim.Tests;

/// <summary>
/// <see cref="PadEventRecorder"/>: what counts as a change, cycle order (stable) and the clamp to core 0's clock on a
/// drain, immediate delivery, watched pads, and exact PWM edges through IO_BANK0.
/// </summary>
public class PadEventRecorderTests
{
    private const uint IoBank0 = 0x40014000, Sio = 0xD0000000;

    private static RP2040Machine Machine()
    {
        var m = new RP2040Machine();
        foreach (var pin in new[] { 14, 15 }) m.Bus.WriteWord(IoBank0 + (uint)pin * 8 + 4, 5);   // FUNCSEL SIO
        return m;
    }

    private static void Toggle(RP2040Machine m, int pin) => m.Bus.WriteWord(Sio + 0x1C, 1u << pin);   // GPIO_OUT_XOR
    private static void Drive(RP2040Machine m, int pin) => m.Bus.WriteWord(Sio + 0x24, 1u << pin);    // GPIO_OE_SET

    [Fact]
    public void A_drain_is_in_cycle_order_stable_and_clamped_to_core_0()
    {
        using var m = Machine();
        Drive(m, 14); Drive(m, 15);
        m.Cpu.Cycles = 100;
        var r = new PadEventRecorder(m);
        r.Baseline();
        Toggle(m, 15); r.Record(1u << 15, 50);
        Toggle(m, 14); r.Record(1u << 14, 20);
        Toggle(m, 15); r.Record(1u << 15, 20);
        Toggle(m, 14); r.Record(1u << 14, 400);   // past core 0's clock (core 1 ran longer): reported at it
        Assert.Equal(4, r.Pending);
        var got = new List<(int, bool, bool, ulong)>();
        r.Drain((g, l, oe, c) => got.Add((g, l, oe, c)));
        Assert.Equal([(14, true, true, 20UL), (15, false, true, 20UL), (15, true, true, 50UL), (14, false, true, 100UL)], got);
        Assert.Equal(100UL, r.ReportedTo);
        Assert.Equal(0, r.Pending);
    }

    // Every change of a call is reported, a pulse included; the changes of one pad at one cycle are one (none when the
    // pad is back where it was), as in RP2350Sharp: a zero-width pulse depends on how a sweep batched the work.
    [Fact]
    public void A_drain_reports_every_change_and_folds_one_pad_s_changes_at_one_cycle()
    {
        using var m = Machine();
        Drive(m, 14); Drive(m, 15);
        m.Cpu.Cycles = 100;
        var r = new PadEventRecorder(m);
        r.Baseline();
        Toggle(m, 14); r.Record(1u << 14, 10);
        Toggle(m, 14); r.Record(1u << 14, 11);   // a pulse: both edges
        Toggle(m, 15); r.Record(1u << 15, 30);
        Toggle(m, 15); r.Record(1u << 15, 30);   // zero width: nothing
        Toggle(m, 14); r.Record(1u << 14, 40);
        Toggle(m, 14); r.Record(1u << 14, 40);
        Toggle(m, 14); r.Record(1u << 14, 40);   // three at one cycle: the last state
        var got = new List<(int, bool, bool, ulong)>();
        r.Drain((g, l, oe, c) => got.Add((g, l, oe, c)));
        Assert.Equal([(14, true, true, 10UL), (14, false, true, 11UL), (14, true, true, 40UL)], got);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void An_undriven_level_change_counts_only_without_LevelsOnlyWhenDriven(bool onlyDriven, int expected)
    {
        using var m = Machine();
        var r = new PadEventRecorder(m) { LevelsOnlyWhenDriven = onlyDriven };
        r.Attach();
        Toggle(m, 15);   // GPIO_OUT moves, OE is off
        Assert.Equal(expected, r.Pending);
    }

    [Fact]
    public void Immediate_delivery_hands_each_batch_over_at_once_and_keeps_nothing()
    {
        using var m = Machine();
        m.Cpu.Cycles = 1234;
        m.Bus.WriteWord(Sio + 0x14, 1u << 15);   // GPIO_OUT_SET GP15 (not driven yet)
        var batches = new List<(ulong, ulong, ulong, ulong)>();
        var r = new PadEventRecorder(m) { Mode = PadReportMode.Immediate, Immediate = (c, oe, lv, cy) => batches.Add((c, oe, lv, cy)) };
        r.Attach();
        m.Bus.WriteWord(Sio + 0x24, 3u << 14);   // OE on GP14 and GP15 in one write
        Assert.Equal([(3UL << 14, 3UL << 14, 1UL << 15, 1234UL)], batches);
        r.ImmediateWatch = 1u << 14;
        Toggle(m, 15);
        Assert.Single(batches);                  // GP15 is no longer reported
        Toggle(m, 14);
        Assert.Equal((1UL << 14, 1UL << 14, 1UL << 14, 1234UL), batches[1]);
        Assert.Equal(0, r.Pending);
    }

    [Fact]
    public void A_watched_change_raises_WatchedEdge()
    {
        using var m = Machine();
        int edges = 0;
        var r = new PadEventRecorder(m) { Watch = 1u << 15, WatchedEdge = () => edges++ };
        r.Attach();
        Drive(m, 14);
        Assert.False(r.WatchedChanged);
        Drive(m, 15);
        Assert.True(r.WatchedChanged);
        Assert.Equal(1, edges);
    }

    // PWM slice 7 on GP14 (TOP 99, CC 30, DIV 1): with exact PWM edges the recorder sees each edge at its counter step,
    // in deferred and eager machines alike.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_pwm_edges_carry_the_cycle_of_their_counter_step(bool defer)
    {
        using var m = new RP2040Machine { DeferPeripheralTicks = defer };
        m.LoadFlash([0x30, 0xBF, 0xFD, 0xE7]);   // wfi; b .-2
        m.Bus.WriteWord(IoBank0 + 14 * 8 + 4, 4);
        m.Bus.WriteWord(0x40050090, 0x10);
        m.Bus.WriteWord(0x4005009C, 99);
        m.Bus.WriteWord(0x40050098, 30);
        m.ExactPwmPadEdges = true;
        var r = new PadEventRecorder(m);
        r.Attach();
        m.Bus.WriteWord(0x4005008C, 1);          // enable: counting starts with the next slice
        long start = m.Cpu.Cycles;
        for (int i = 0; i < 200; i++) m.Run(7 + i % 13);
        var got = new List<(int Gpio, bool Level, bool Oe, ulong Cycle)>();
        r.Drain((g, l, oe, c) => got.Add((g, l, oe, c)));
        var falls = got.Where(e => e.Gpio == 14 && !e.Level).Select(e => (long)e.Cycle - start).ToList();
        var rises = got.Where(e => e.Gpio == 14 && e.Level && e.Oe).Select(e => (long)e.Cycle - start).ToList();
        Assert.True(falls.Count > 10);
        // CTR reaches 30 at the 30th step, wraps to 0 at the 100th: falls at 30 + 100 k, rises at 100 k.
        Assert.All(falls, f => Assert.Equal(30, f % 100));
        Assert.All(rises.Skip(1), x => Assert.Equal(0, x % 100));
    }
}

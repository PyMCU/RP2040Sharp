using SiliconTwin.Native;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The pin-level SPI master, UART and I2C master against a lock-step host through the C ABI: the host answers every
/// pad edge only after the call that reported it returns (SPI MOSI wired to MISO, UART TX to RX, an I2C slave that
/// ACKs and serves reads). At guard 1 every call returns after one instruction, so the answers are on time; the
/// event-bounded guard makes guards 75 and 300 return at the same instruction after each engine edge, so the whole
/// exchange (pad events and their cycles, the host's inputs and their cycles, what firmware received) is the same.
/// </summary>
public class PinLevelLockstepTests(ITestOutputHelper output)
{
    internal sealed record Result(List<string> Pads, List<string> Inputs, IReadOnlyList<string> Received, uint Bursts,
        long Calls, long CutSlices, ulong End);

    internal static Result Lockstep(BusScenario sc, uint guard, bool eventGuard = true, uint max = 20_000)
    {
        var h = Abi.CreateEx(0, Abi.BusAll);
        var host = new Host();
        try
        {
            Abi.Attach(h, host);
            var chip = Exports.Lookup(h)!;
            chip.EventGuard = eventGuard;
            Assert.True(Abi.LoadUf2(h, Firmware.Uf2(sc.Image)) > 0);
            var bus = new BusHost(h, host);
            sc.Wire(bus);
            long calls = 0;
            while (Abi.Cycles(h) < sc.Span) { bus.Step(max, guard, sc.Watch); calls++; }
            var pads = host.Pins.Where(p => p.Cycle < sc.Span).Select(p => $"pad {p.Gpio}={p.Level} oe {p.Oe} @{p.Cycle}").ToList();
            var inputs = bus.Inputs.Where(i => i.Cycle < sc.Span).Select(i => $"in {i.Gpio}={i.Level} @{i.Cycle}").ToList();
            return new Result(pads, inputs, PinLevelScenarios.Received(sc, chip), chip.Machine.Cpu.Registers.R7, calls,
                chip.CutSlices, Abi.Cycles(h));
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    public static IEnumerable<object[]> Names() => PinLevelScenarios.Names();

    public static IEnumerable<object[]> NamesAndGuards() =>
        from n in PinLevelScenarios.Catalog.Keys from g in new[] { 75u, 300u } select new object[] { n, g };

    [Theory]
    [MemberData(nameof(Names))]
    public void At_guard_1_firmware_receives_what_it_sent(string name)
    {
        var sc = PinLevelScenarios.Catalog[name]();
        var r = Lockstep(sc, 1);
        output.WriteLine($"{name}: {r.Calls} calls, {r.Pads.Count} pad events, {r.Inputs.Count} inputs, end {r.End}");
        Assert.Equal(sc.Expected(null!), r.Received);
        Assert.True(r.Bursts >= 2, $"{r.Bursts} bursts");
    }

    [Theory]
    [MemberData(nameof(NamesAndGuards))]
    public void Coarse_guards_give_the_guard_1_exchange(string name, uint guard)
    {
        var sc = PinLevelScenarios.Catalog[name]();
        var fine = Lockstep(sc, 1);
        var coarse = Lockstep(sc, guard);
        output.WriteLine($"{name} guard {guard}: {coarse.Calls} calls ({fine.Calls} at guard 1), {coarse.CutSlices} cut slices, " +
                         $"{coarse.Pads.Count} pad events");
        if (name.EndsWith("irq"))
        {
            // Asleep in WFI, guard 1 runs slices of two slots (twice the guard), so the SPI interrupt that wakes the
            // core is taken up to a cycle later than with a coarse guard, whose sleeping slice ends on the engine's
            // edge itself. The exchange is the same up to that cycle.
            Assert.Equal(fine.Pads.Count, coarse.Pads.Count);
            for (int i = 0; i < fine.Pads.Count; i++)
                Assert.InRange(Cycle(coarse.Pads[i]) - Cycle(fine.Pads[i]), -2, 2);
        }
        else
        {
            DiffRun.AssertSame(fine.Pads, coarse.Pads, "pad events");
            DiffRun.AssertSame(fine.Inputs, coarse.Inputs, "host inputs");
        }
        Assert.Equal(fine.Received, coarse.Received);
        Assert.Equal(fine.Bursts, coarse.Bursts);
        Assert.Equal(sc.Expected(null!), coarse.Received);
    }

    private static long Cycle(string padLine) => long.Parse(padLine[(padLine.LastIndexOf('@') + 1)..]);

    [Fact]
    public void Without_the_event_guard_a_coarse_guard_answers_late_and_the_data_is_wrong()
    {
        // An 8-cycle SCK half period: the host's MISO must follow MOSI within a few cycles.
        var sc = PinLevelScenarios.Spi(0, 8, 16, PinLevelFirmware.SpiVariant.Poll);
        var late = Lockstep(sc, 300, eventGuard: false);
        var guarded = Lockstep(sc, 300);
        output.WriteLine($"without: {late.Calls} calls; with: {guarded.Calls} calls, {guarded.CutSlices} cut slices");
        Assert.NotEqual(sc.Expected(null!), late.Received);
        Assert.Equal(sc.Expected(null!), guarded.Received);
    }

    [Fact]
    public void Cycles_until_edge_is_the_engines_next_edge_on_a_watched_pad()
    {
        var sc = PinLevelScenarios.Spi(0, 8, 20, PinLevelFirmware.SpiVariant.Poll, bursts: 1);
        var h = Abi.CreateEx(0, Abi.BusAll);
        var host = new Host();
        try
        {
            Abi.Attach(h, host);
            Assert.True(Abi.LoadUf2(h, Firmware.Uf2(sc.Image)) > 0);
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(h, PinLevelScenarios.SpiPads));   // nothing routed yet
            var bus = new BusHost(h, host);
            sc.Wire(bus);
            // Run to the first SCK edge: mid-frame, the next edge is at most a half period (10 cycles) away.
            while (!host.Pins.Any(p => p.Gpio == 18 && p.Level == 1)) bus.Step(20_000, 75, PinLevelScenarios.SpiPads);
            var next = Abi.CyclesUntilEdge(h, PinLevelScenarios.SpiPads);
            output.WriteLine($"after the first SCK edge at {host.Pins.First(p => p.Gpio == 18 && p.Level == 1).Cycle}: next edge in {next} cycles (now {Abi.Cycles(h)})");
            Assert.InRange(next, 0ul, 10ul);
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(h, 1u << 15));                  // not an SPI pad
            Assert.Equal(next, Abi.CyclesUntilEdge(h, 1u << 18 | 1u << 15));                 // any watched pad of the block
            // The burst done, the engine idles: no edge is coming.
            while (Abi.Cycles(h) < sc.Span) bus.Step(20_000, 75, PinLevelScenarios.SpiPads);
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(h, PinLevelScenarios.SpiPads));
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(0, PinLevelScenarios.SpiPads)); // invalid handle
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    // A level the host drives must only be seen by cycles that run after it was driven, in every mode: the engine is
    // brought up to the call's end before the input changes. SPI mode 0, half period 10: MISO is sampled 10, 30, 50 ...
    // cycles after the word is queued; the host drives MISO high 40 cycles in.
    [Theory]
    [InlineData(SliceMode.Eager, false)]
    [InlineData(SliceMode.Deferred, false)]
    [InlineData(SliceMode.Fast, false)]
    [InlineData(SliceMode.Eager, true)]
    [InlineData(SliceMode.Deferred, true)]
    [InlineData(SliceMode.Fast, true)]
    public void Miso_driven_between_calls_is_not_sampled_retroactively(SliceMode mode, bool viaRunUntilEdge)
    {
        using var run = new DiffRun(mode, Abi.BusSpi);
        Assert.Equal(1, Abi.LoadUf2(run.H, Firmware.Uf2(Firmware.SleepForever())));
        run.Run(100);                                        // asleep in WFI: one cycle per slot from here
        var m = run.Chip.Machine;
        foreach (int pin in new[] { 16, 18, 19 }) m.IoBank0.WriteWord((uint)(pin * 8 + 4), 1);
        m.Spi0.WriteWord(0x00, 7);                           // 8-bit frames, mode 0
        m.Spi0.WriteWord(0x10, 20);                          // SCK period 20 cycles
        m.Spi0.WriteWord(0x04, 2);
        Abi.PinSetInput(run.H, 16, 0);
        var t0 = run.Cycles;
        m.Spi0.WriteWord(0x08, 0xA5);
        if (viaRunUntilEdge) run.RunUntilEdge(40, 75, 1u << 17);   // a quiet pad: runs the full 40
        else run.Run(40);
        Assert.Equal(t0 + 40, run.Cycles);
        Abi.PinSetInput(run.H, 16, 1);
        for (int i = 0; i < 5; i++) run.Run(40);
        Assert.Equal(0x3Fu, m.Spi0.ReadWord(0x08));          // samples at 10 and 30 are old, the other six new
    }
}

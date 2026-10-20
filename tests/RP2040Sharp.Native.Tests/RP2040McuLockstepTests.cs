using System.Text;
using RP2040.Peripherals;
using RP2040.Peripherals.PinLevel;
using RP2040.Peripherals.Usb;
using SiliconTwin.Abstractions;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The managed path of a fixed-step host (iCircuit): <see cref="RP2040Mcu.Run"/>(750) per step (6 us at 125 MHz), the
/// circuit's answers handed back with <see cref="IPinBank.SetInputs"/>, PWM read with <see cref="IPwmSource.TryGetPwm"/>.
/// It does not go through the native chip; the deferred machine (the adapter's default) must give the results of the
/// eager one (DeferPeripheralTicks = false), pad events and their cycles included. Mirrors RP2350Sharp's
/// RP2350McuLockstepTests.
/// </summary>
public class RP2040McuLockstepTests(ITestOutputHelper output)
{
    private const int Step = 750;
    private const uint PwmBase = 0x40050000, IoBank0 = 0x40014000, Data = 0x20010000;

    private static RP2040Mcu Boot(bool defer, byte[] image, PinLevelBus buses = PinLevelBus.None, bool coSim = true)
    {
        var mcu = new RP2040Mcu(new RP2040McuOptions { DeferPeripheralTicks = defer, PinLevelBuses = buses, CoSim = coSim });
        mcu.Load(FirmwareImage.FromBin(image));
        mcu.Reset();
        return mcu;
    }

    // GP0/GP1 to PWM slice 0 (TOP 999, DIV 1: a 1000-cycle period), A at 300, B at 700; CC.A then steps every 256
    // passes of the loop, which also copies CTR to RAM.
    private static byte[] PwmProgram() => new ThumbAsm()
        .Ldr(0, IoBank0 + 4).Movs(1, 4).Str(1, 0, 0).Str(1, 0, 8)
        .Ldr(0, PwmBase).Ldr(6, Data)
        .Ldr(1, 999).Str(1, 0, 0x10).Ldr(1, 300u | 700u << 16).Str(1, 0, 0x0C).Movs(1, 0x10).Str(1, 0, 4).Movs(1, 1).Str(1, 0, 0)
        .Movs(4, 0)
        .Label("loop").Adds(4, 1).Lsls(5, 4, 24).B(ThumbAsm.NE, "skip").Lsrs(5, 4, 8).Ldr(7, 700u << 16).Orrs(5, 7).Str(5, 0, 0x0C)
        .Label("skip").Ldr(5, 0, 8).Str(5, 6, 0).B("loop")
        .Build();

    internal sealed record Trace(List<(long Cycle, ulong Changed, ulong Oe, ulong Levels)> Events, List<PwmInfo?> Pwm, long Cycles, string Text);

    private static Trace LockStep(RP2040Mcu mcu, int steps, int pin, Action<int>? beforeStep = null, Func<string>? text = null)
    {
        var events = new List<(long, ulong, ulong, ulong)>();
        mcu.Banks[0].PinChanged += (in PinChange e) => events.Add((e.Cycle, e.Changed, e.OutputEnable, e.Levels));
        var pwm = new List<PwmInfo?>();
        for (int i = 0; i < steps; i++)
        {
            beforeStep?.Invoke(i);
            mcu.Run(Step);
            pwm.Add(mcu.TryGetPwm(0, pin, out var info) ? info : null);
        }
        return new Trace(events, pwm, mcu.Cycles, text?.Invoke() ?? "");
    }

    // Both run paths: the co-simulation loop (the default) and RunCycles (RP2040Mcu.CoSim = false).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pwm_firmware_in_lockstep_matches_the_eager_machine(bool coSim)
    {
        Trace Go(bool defer)
        {
            using var mcu = Boot(defer, PwmProgram(), coSim: coSim);
            return LockStep(mcu, 4000, 0);
        }
        var eager = Go(false);
        var deferred = Go(true);
        output.WriteLine($"{eager.Events.Count} events, {eager.Pwm.Count(p => p is { Enabled: true })} steps with PWM on");
        Assert.True(eager.Events.Count > 1000, "the PWM pins toggled");
        Assert.True(eager.Pwm.Select(p => p!.Value.Duty).Distinct().Count() > 3, "firmware moved the duty");
        Assert.Equal(eager.Pwm, deferred.Pwm);
        Assert.Equal(eager.Events, deferred.Events);
        Assert.Equal(eager.Cycles, deferred.Cycles);

        // The edges carry the cycle of the counter step, not of the step boundary: channel B (CC 700, never written
        // after the start) rises every 1000 cycles exactly and stays high 700 of them. (The first rise is the enable
        // write; the counter counts from the start of that slice, so the first period is shorter.)
        const ulong B = 1UL << 1;
        var rises = new List<long>();
        var falls = new List<long>();
        foreach (var e in eager.Events)
            if ((e.Changed & B) != 0) ((e.Levels & B) != 0 ? rises : falls).Add(e.Cycle);
        rises.RemoveAt(0);
        Assert.True(rises.Count > 100);
        var periods = rises.Zip(rises.Skip(1), (a, b) => b - a).Distinct().ToList();
        Assert.Equal([1000L], periods);
        var highs = rises.Select(r => falls.FirstOrDefault(f => f > r) - r).Where(d => d > 0).Distinct().ToList();
        Assert.Equal([700L], highs);
        // B's edges (no register write moves B) arrive in cycle order. A CC write is reported at its instruction and the
        // counter steps of that slice at the sweep, so across pins (and on A) a delivered event can be earlier than the
        // one before it: the native chip sorts them before it reports, a PinChanged handler sees them as they happen.
        var bEvents = eager.Events.Where(e => (e.Changed & B) != 0).Select(e => e.Cycle).ToList();
        Assert.True(bEvents.Zip(bEvents.Skip(1), (a, b) => b >= a).All(x => x), "B's events in cycle order");
    }

    // MicroPython v1.28.0 (RPI_PICO): machine.PWM on GP15, the duty and the frequency changed from a sleep_ms script
    // typed on the USB CDC port.
    [SkippableFact]
    public void MicroPython_pwm_script_in_lockstep_matches_the_eager_machine()
    {
        var path = Firmware.MicroPythonPico();
        Skip.If(path is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var uf2 = File.ReadAllBytes(path!);
        string[] lines =
        [
            "from machine import Pin,PWM", "import time", "p=PWM(Pin(15),freq=2000,duty_u16=16384)", "time.sleep_ms(2)",
            "p.duty_u16(49152)", "time.sleep_ms(2)", "p.freq(5000)", "print('PWMDONE')",
        ];
        var script = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\r")));

        Trace Go(bool defer)
        {
            using var mcu = new RP2040Mcu(new RP2040McuOptions { DeferPeripheralTicks = defer });
            mcu.MaxSliceInstructions = 512;   // USB enumeration and the REPL, as the native chip slices them
            mcu.Load(FirmwareImage.FromUf2(uf2));
            mcu.Reset();
            mcu.Machine.Ssi.UniqueId = [0xE6, 0x60, 0x58, 0x38, 0x83, 0x4F, 0x2A, 0x2D];
            var text = new StringBuilder();
            var cdc = new UsbCdcHost(mcu.Machine.Usb) { OnSerialData = d => text.Append(Encoding.ASCII.GetString(d)) };
            for (int i = 0; i < 4000 && !text.ToString().Contains(">>> "); i++) mcu.Run(1_250_000);
            Assert.Contains(">>> ", text.ToString());
            int sentAt = -1;
            var trace = LockStep(mcu, 4000, 15, beforeStep: i =>
            {
                if (sentAt < 0) { sentAt = i; cdc.SendSerialBytes(script); }
            }, text: () => text.ToString());
            output.WriteLine($"defer={defer}: {trace.Events.Count} events; {trace.Text[Math.Max(0, trace.Text.Length - 300)..].Replace("\r", " ").Replace("\n", "|")}");
            return trace;
        }
        var eager = Go(false);
        var deferred = Go(true);
        Assert.Contains("PWMDONE", eager.Text);
        Assert.Equal(eager.Text, deferred.Text);
        Assert.True(eager.Pwm.Count(p => p is { Enabled: true }) > 100, "PWM ran");
        Assert.True(eager.Pwm.Where(p => p is { Enabled: true }).Select(p => p!.Value).Distinct().Count() >= 3, "duty and frequency changed");
        Assert.True(eager.Events.Count(e => (e.Changed & 1UL << 15) != 0) > 20, "GP15 edges reported");
        Assert.Equal(eager.Pwm, deferred.Pwm);
        Assert.Equal(eager.Events, deferred.Events);
        Assert.Equal(eager.Cycles, deferred.Cycles);
    }

    // ── SPI loopback (pin-level SPI0: GP16 MISO, GP17 CSn, GP18 SCK, GP19 MOSI) ─────────────────────────

    private const ulong Miso = 1UL << 16, Mosi = 1UL << 19;
    private const int Bursts = 200;

    private static RP2040Mcu SpiMcu(bool defer, int cpsdvsr, bool coSim = true) =>
        Boot(defer, PinLevelFirmware.SpiBursts(0, 8, cpsdvsr, PinLevelFirmware.SpiVariant.Poll, gap: 60, bursts: Bursts), PinLevelBus.Spi, coSim);

    private static (int Words, int Bad, int Bursts) CheckBursts(RP2040Mcu mcu)
    {
        var m = mcu.Machine;
        int bursts = (int)m.Cpu.Registers.R7, words = bursts * 8, bad = 0;
        for (int i = 0; i < words; i++)
            if (m.Bus.ReadWord(PinLevelFirmware.RxBuffer + 4 * (uint)i) != PinLevelFirmware.SpiWord(i, 8)) bad++;
        return (words, bad, bursts);
    }

    // The wire answers inside the PinChanged event of the edge, as a bit-level device model does: MISO follows MOSI at
    // once, whatever the step.
    [Theory]
    [InlineData(126, true)]   // ~1 MHz
    [InlineData(32, true)]    // ~3.9 MHz
    [InlineData(126, false)]
    [InlineData(32, false)]
    public void Spi_loopback_answered_from_the_pin_event_is_correct_at_750_cycle_steps(int cpsdvsr, bool coSim)
    {
        var traces = new List<List<(long, ulong, ulong, ulong)>>();
        foreach (var defer in new[] { false, true })
        {
            using var mcu = SpiMcu(defer, cpsdvsr, coSim);
            var bank = mcu.Banks[0];
            var events = new List<(long, ulong, ulong, ulong)>();
            bank.PinChanged += (in PinChange e) =>
            {
                events.Add((e.Cycle, e.Changed, e.OutputEnable, e.Levels));
                if (e.Affects(Mosi)) bank.SetInputs(Miso, (e.Levels & Mosi) != 0 ? Miso : 0);
            };
            for (int i = 0; i < 400; i++) mcu.Run(Step);
            var (words, bad, bursts) = CheckBursts(mcu);
            output.WriteLine($"cpsdvsr {cpsdvsr} coSim={coSim} defer={defer}: {bursts} bursts, {words} words, {bad} bad, {events.Count} events");
            Assert.True(bursts > 10);
            Assert.Equal(0, bad);
            traces.Add(events);
        }
        Assert.Equal(traces[0], traces[1]);
    }

    // A handler that drives an unrelated pad (GP2) on every MOSI edge, from inside the sweep or the access that brought
    // the engine there: the host-input sync must not move time (no cycles ticked twice), so cycles, swept cycles, the
    // engine's own time and RAM are those of the run without it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Host_input_from_inside_a_sweep_does_not_move_time(bool defer)
    {
        (long, long, long, int, string) Go(bool extra)
        {
            using var mcu = SpiMcu(defer, 32);
            var bank = mcu.Banks[0];
            int n = 0;
            bank.PinChanged += (in PinChange e) =>
            {
                if (!e.Affects(Mosi)) return;
                bank.SetInputs(Miso, (e.Levels & Mosi) != 0 ? Miso : 0);
                if (extra) bank.SetInputs(1UL << 2, (ulong)(n++ & 1) << 2);
            };
            for (int i = 0; i < 200; i++) mcu.Run(Step);
            var m = mcu.Machine;
            var ram = new StringBuilder();
            for (uint i = 0; i < 256; i++) ram.Append(m.Bus.ReadWord(PinLevelFirmware.RxBuffer + 4 * i).ToString("X"));
            return (mcu.Cycles, m.SweptCycles, m.Spi0.PinEngine!.Cycle, CheckBursts(mcu).Bad, ram.ToString());
        }
        var plain = Go(false);
        var extra = Go(true);
        output.WriteLine($"defer={defer}: cycles {plain.Item1}, engine {plain.Item3}");
        Assert.Equal(0, plain.Item4);
        Assert.Equal(plain, extra);
    }

    // The circuit answers once per step, from the MOSI level it sees at the step boundary, as a fixed-step solver does.
    // The wire is then a step late, so the words are not the ones sent, but they must be the same on both machines.
    [Fact]
    public void Spi_loopback_answered_at_the_step_boundary_is_the_same_deferred_and_eager()
    {
        uint[] Go(bool defer)
        {
            using var mcu = SpiMcu(defer, 126);
            var bank = mcu.Banks[0];
            for (int i = 0; i < 400; i++)
            {
                mcu.Run(Step);
                bank.SetInputs(Miso, (bank.OutputLevels & Mosi) != 0 ? Miso : 0);
            }
            var words = new uint[64];
            for (uint i = 0; i < words.Length; i++) words[i] = mcu.Machine.Bus.ReadWord(PinLevelFirmware.RxBuffer + 4 * i);
            output.WriteLine($"defer={defer}: {CheckBursts(mcu)}");
            return words;
        }
        Assert.Equal(Go(false), Go(true));
    }

    // ── Host inputs answered from inside a run ──────────────────────────────────────────────────────

    // GP3 is PWM slice 1's B input, gating its counter (DIVMODE 1, TOP 0xFFFF): how far the counter is is the time the
    // gate was high, so a host input applied at the wrong time shows in CTR. The loop folds CTR and GPIO_IN into R7
    // (r7 = r7 << 1 ^ value) on every pass and toggles GP2 (SIO) every 64 passes. With `pwmEdges`, GP0 is PWM slice 0's
    // A output (TOP 999, CC 300), whose edges are reported from inside the sweep that steps the counter.
    private static byte[] GatedCounterProgram(bool pwmEdges)
    {
        const uint Sio = 0xD0000000, Slice1 = PwmBase + 0x14;
        var a = new ThumbAsm()
            .Ldr(0, IoBank0 + 2 * 8 + 4).Movs(1, 5).Str(1, 0, 0)           // GP2: SIO
            .Ldr(0, IoBank0 + 3 * 8 + 4).Ldr(1, 0x2004).Str(1, 0, 0);       // GP3: PWM (slice 1 B), OEOVER disable: an input
        if (pwmEdges)
            a.Ldr(0, IoBank0 + 4).Movs(1, 4).Str(1, 0, 0)                             // GP0: PWM (slice 0 A)
             .Ldr(3, PwmBase).Ldr(1, 999).Str(1, 3, 0x10).Ldr(1, 300).Str(1, 3, 0x0C).Movs(1, 1).Str(1, 3, 0);
        return a
            .Ldr(2, Slice1).Ldr(1, 0xFFFF).Str(1, 2, 0x10).Movs(1, 0x11).Str(1, 2, 0)   // TOP, CSR = EN | DIVMODE gated
            .Ldr(0, Sio).Movs(1, 4).Str(1, 0, 0x24)                                         // GPIO_OE_SET GP2
            .Movs(4, 0).Movs(7, 0)
            .Label("loop").Adds(4, 1).Lsls(5, 4, 26).B(ThumbAsm.NE, "skip").Movs(1, 4).Str(1, 0, 0x1C)   // GPIO_XOR GP2
            .Label("skip").Ldr(5, 2, 8).Lsls(7, 7, 1).Eors(7, 5).Ldr(5, 0, 4).Lsls(7, 7, 1).Eors(7, 5).B("loop")
            .Build();
    }

    // A bit-level device answering the chip from the PinChanged event, as a wire: GP3 follows GP2 (a SIO write, so the
    // handler runs inside a CPU run: a plain slice, a bounded run) or GP0 (a PWM counter step, so it runs inside the
    // sweep, from the PWM's own tick). RP2040Machine.SyncForHostInput brings the deferred peripherals to the sweeps so
    // far, which is where the eager machine has them at that point, so the deferred machine gives the eager machine's
    // counter, GPIO_IN samples, events and cycles, whatever the step: 750-cycle steps (one slice each) and 125,000-cycle
    // steps in 512-instruction slices (bounded runs of core 0 between the edges).
    [Theory]
    [InlineData(false, 750, 16_384)]
    [InlineData(false, 125_000, 512)]
    [InlineData(true, 750, 16_384)]
    [InlineData(true, 125_000, 512)]
    public void Host_input_answered_from_the_pin_event_matches_the_eager_machine(bool fromSweep, int step, int slice)
    {
        const ulong Gp0 = 1UL << 0, Gp2 = 1UL << 2, Gp3 = 1UL << 3;
        ulong from = fromSweep ? Gp0 : Gp2;
        (uint R7, uint Ctr, long Cycles, List<(long, ulong, ulong, ulong)> Events, long Bounded) Go(bool defer)
        {
            using var mcu = Boot(defer, GatedCounterProgram(fromSweep));
            mcu.MaxSliceInstructions = slice;
            var bank = mcu.Banks[0];
            var events = new List<(long, ulong, ulong, ulong)>();
            bank.PinChanged += (in PinChange e) =>
            {
                events.Add((e.Cycle, e.Changed, e.OutputEnable, e.Levels));
                if (e.Affects(from)) bank.SetInputs(Gp3, (e.Levels & from) != 0 ? Gp3 : 0);
            };
            int steps = (int)(3_000_000L / step);
            for (int i = 0; i < steps; i++) mcu.Run(step);
            var m = mcu.Machine;
            var cosim = (RP2040.CoSim.RP2040CoSim)typeof(RP2040Mcu)
                .GetField("_cosim", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(mcu)!;
            return (m.Cpu.Registers.R7, m.Bus.ReadWord(PwmBase + 0x14 + 8), mcu.Cycles, events, cosim.BoundedSlices);
        }
        var eager = Go(false);
        var deferred = Go(true);
        int answered = eager.Events.Count(e => (e.Item2 & from) != 0);
        output.WriteLine($"fromSweep={fromSweep} step {step}: {eager.Events.Count} events ({answered} answered), CTR {eager.Ctr}, " +
                         $"bounded slices (deferred) {deferred.Bounded}");
        Assert.True(answered > 100, "the wire answered");
        Assert.True(eager.Ctr != 0, "the gate opened");
        if (!fromSweep && step > 100 * slice) Assert.True(deferred.Bounded > 0, "bounded runs between the edges");
        Assert.Equal(eager.Events, deferred.Events);
        Assert.Equal(eager.Ctr, deferred.Ctr);
        Assert.Equal(eager.R7, deferred.R7);
        Assert.Equal(eager.Cycles, deferred.Cycles);
    }

    // ── Inputs applied with peripheral time owed ─────────────────────────────────────────────────────

    // SPI0 with its pads routed and nothing else running: a word queued, then the machine run in raw slices (which
    // leave the deferred SPI behind), MISO driven high between them. The samples before the drive see the old level.
    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 40)]
    [InlineData(false, 41)]
    [InlineData(true, 41)]
    [InlineData(true, 45)]
    public void Spi_miso_driven_with_ticks_owed_is_not_sampled_retroactively(bool defer, int ran)
    {
        // A core-0 program that only sleeps: the cycles are all the SPI's.
        using var mcu = Boot(defer, new ThumbAsm().Label("l").Wfi().B("l").Build(), PinLevelBus.Spi);
        var m = mcu.Machine;
        mcu.Run(100);
        const uint Spi0 = 0x4003C000;
        foreach (int pin in new[] { 16, 18, 19 }) m.Bus.WriteWord(IoBank0 + (uint)(pin * 8 + 4), 1);
        m.Bus.WriteWord(Spi0, 7);          // 8-bit frames, mode 0
        m.Bus.WriteWord(Spi0 + 0x10, 20);  // SCK period 20 cycles, half 10
        m.Bus.WriteWord(Spi0 + 4, 2);
        mcu.Run(100);
        m.Bus.WriteWord(Spi0 + 8, 0xA5);
        long t0 = mcu.Cycles;
        while (mcu.Cycles - t0 < ran) m.Run(1);   // raw slices: nothing flushed
        output.WriteLine($"ran {mcu.Cycles - t0}");
        mcu.Banks[0].SetInputs(Miso, Miso);
        for (int i = 0; i < 8; i++) mcu.Run(40);
        // the samples at 10 and 30 come before the drive, the other six after it (the CSn setup is the first half
        // period, so the leading edges fall 10, 30, 50 ... cycles after the word was queued)
        Assert.Equal(0x3Fu, m.Bus.ReadWord(Spi0 + 8));
    }

    // A PIO state machine sampling a pad with `in pins` at a slow divider, driven between raw slices: eager and deferred
    // read the same words.
    [Fact]
    public void Pio_in_pins_driven_between_slices_reads_the_same_deferred_and_eager()
    {
        uint[] Go(bool defer)
        {
            const int pin = 5;
            using var mcu = Boot(defer, new ThumbAsm().Label("l").Wfi().B("l").Build());
            var m = mcu.Machine;
            const uint Pio0 = 0x50200000;
            m.Bus.WriteWord(IoBank0 + pin * 8 + 4, 6);     // FUNCSEL = PIO0
            m.Pio0.InReset = false;
            m.Bus.WriteWord(Pio0 + 0x48, 0x4001);          // in pins, 1
            m.Bus.WriteWord(Pio0 + 0xc8, 7u << 16);        // clkdiv 7
            m.Bus.WriteWord(Pio0 + 0xcc, 0);               // wrap 0..0
            m.Bus.WriteWord(Pio0 + 0xd0, 1u << 16);        // autopush, threshold 32, shift left
            m.Bus.WriteWord(Pio0 + 0xdc, (uint)pin << 15); // IN_BASE
            m.Bus.WriteWord(Pio0, 1);                      // enable SM0
            var words = new List<uint>();
            for (int i = 0; i < 300; i++)
            {
                m.Run(3 + i % 5);
                if (i % 7 == 0) mcu.Banks[0].SetInputs(1UL << pin, (ulong)(i / 7 % 2) << pin);
                if (i % 50 == 49) while ((m.Bus.ReadWord(Pio0 + 0x04) & 0x100) == 0) words.Add(m.Bus.ReadWord(Pio0 + 0x20));
            }
            return [.. words];
        }
        var eager = Go(false);
        Assert.NotEmpty(eager);
        Assert.Contains(eager, w => w != 0 && w != uint.MaxValue);
        Assert.Equal(eager, Go(true));
    }
}

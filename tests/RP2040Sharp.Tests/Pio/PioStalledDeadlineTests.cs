using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pio;

namespace RP2040.Peripherals.Tests.Pio;

/// <summary>
/// A state machine stalled on an access (OUT on autopull or a blocking PULL with the TX FIFO empty, autopush or a blocking
/// PUSH with the RX FIFO full, WAIT IRQ, IRQ WAIT) has no deadline of its own: the deferred block is ticked only when
/// something accesses it. The deferred block follows <see cref="DeferredTick"/>'s protocol (a sweep ticks it at its next
/// event and runs <see cref="PioPeripheral.SkipTick"/> otherwise; an access is <see cref="PioPeripheral.CatchUp"/>, the
/// access, and the next event asked again); the reference is the same block ticked on every sweep with
/// <see cref="PioPeripheral.StalledDeadlines"/> off. Every pad change at the same sweep, the same state and registers at
/// every access, the RX FIFO words in the same order.
/// </summary>
public class PioStalledDeadlineTests
{
    private const uint Ctrl = 0x000, Fstat = 0x004, Fdebug = 0x008, Flevel = 0x00C, Txf0 = 0x010, Rxf0 = 0x020,
        Irq = 0x030, IrqForce = 0x034, Sm0 = 0x0C8;

    private sealed class Rig
    {
        public readonly PioPeripheral Pio;
        public readonly bool Lazy;
        public readonly List<(long Cycle, uint Pins)> Edges = [];
        public readonly List<uint> Rx = [];
        public uint Pins;
        public long Now, MaxEvent, Ticks;
        private long _owed, _event;
        private bool _dirty = true;

        public Rig(bool lazy)
        {
            Lazy = lazy;
            Pio = new PioPeripheral(new CortexM0Plus(new BusInterconnect()), 0) { StalledDeadlines = lazy };
            Pio.WriteGpioPins = (v, m) =>
            {
                var next = (Pins & ~m) | (v & m);
                if (next != Pins) Edges.Add((Now, next));
                Pins = next;
            };
        }

        public void Sweep(int n)
        {
            Now += n;
            if (!Lazy) { Pio.Tick(n); return; }
            if (_dirty) { _event = Pio.CyclesUntilEvent(); _dirty = false; MaxEvent = Math.Max(MaxEvent, Math.Min(_event, 1L << 40)); }
            _owed += n;
            if (_owed >= _event) { Pio.Tick(_owed); _owed = 0; _dirty = true; Ticks++; }
            else Pio.SkipTick();
        }

        // What the bus bridge does around an access: the owed cycles, then the access, then the next event again.
        public void Access(Action<Rig> a)
        {
            if (Lazy && _owed > 0) { Pio.CatchUp(_owed); _owed = 0; }
            a(this);
            _dirty = true;
        }
    }

    private static (Rig Eager, Rig Lazy) Run(ushort[] words, uint shift, Action<Rig, Random> access, int sweeps = 6000,
        uint clkdiv = 2u << 16, int seed = 1, uint enable = 1, uint pinctrl1 = 0, Action<PioPeripheral>? setup = null)
    {
        var eager = new Rig(false);
        var lazy = new Rig(true);
        var rngE = new Random(seed); var rngL = new Random(seed);
        foreach (var r in new[] { eager, lazy })
        {
            var p = r.Pio;
            for (int i = 0; i < words.Length; i++) p.WriteWord(0x048 + 4u * (uint)i, words[i]);
            for (uint sm = 0; sm < 4; sm++)
            {
                var b = Sm0 + sm * 0x18;
                p.WriteWord(b + 0x00, clkdiv);
                p.WriteWord(b + 0x04, (uint)(words.Length - 1) << 12);
                p.WriteWord(b + 0x08, shift);
                p.WriteWord(b + 0x14, sm == 1 && pinctrl1 != 0 ? pinctrl1 : (1u << 29) | (1u << 10) | (1u << 26) | (1u << 20));
            }
            setup?.Invoke(p);
            p.WriteWord(Ctrl, enable);
        }
        var sizes = new Random(seed + 1000);
        for (int i = 0; i < sweeps; i++)
        {
            int n = sizes.Next(1, 40);
            eager.Sweep(n); lazy.Sweep(n);
            if (sizes.Next(60) == 0)
            {
                foreach (var (r, rng) in new[] { (eager, rngE), (lazy, rngL) })
                    r.Access(x => access(x, rng));
                eager.Access(_ => { }); lazy.Access(_ => { });
                for (int sm = 0; sm < 4; sm++) Assert.Equal(eager.Pio.GetSmState(sm), lazy.Pio.GetSmState(sm));
                foreach (var reg in new[] { Ctrl, Fstat, Fdebug, Flevel, Irq, 0x03Cu, 0x040u, 0x128u, Sm0 + 0x04, Sm0 + 0x0C })
                    Assert.Equal(eager.Pio.ReadWord(reg), lazy.Pio.ReadWord(reg));
            }
        }
        Assert.Equal(eager.Edges, lazy.Edges);
        Assert.Equal(eager.Rx, lazy.Rx);
        return (eager, lazy);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Out_on_autopull_with_an_empty_tx_fifo_waits_for_the_cpu(int seed)
    {
        // out pins, 1 side 0 ; nop side 1 (autopull at 8 bits): the gSPI program's idle point.
        var (eager, lazy) = Run([0x6001, 0xB042], (1u << 17) | (8u << 25),
            (r, rng) => { if (rng.Next(3) == 0) r.Pio.WriteWord(Txf0, (uint)rng.Next()); }, seed: seed);
        Assert.True(eager.Edges.Count > 100, "the SM shifted the words out");
        Assert.True(lazy.MaxEvent > 1000, "the stalled SM had no deadline: " + lazy.MaxEvent);
    }

    [Fact]
    public void Blocking_pull_waits_for_the_cpu_and_a_cleared_txstall_comes_back()
    {
        // pull block side 1 ; out pins, 1 side 0 ; out pins, 1 side 1 ; out null, 30. Clearing FDEBUG makes the
        // stalled PULL due again: its next re-run sets TXSTALL.
        var (eager, lazy) = Run([0x90A0, 0x6001, 0x7001, 0x607E], 0,
            (r, rng) =>
            {
                if (rng.Next(4) == 0) r.Pio.WriteWord(Fdebug, 0xFFFFFFFF);
                if (rng.Next(2) == 0) r.Pio.WriteWord(Txf0, (uint)rng.Next());
            });
        Assert.True(eager.Edges.Count > 50);
        Assert.True(lazy.MaxEvent > 1000);
    }

    [Fact]
    public void Autopush_with_a_full_rx_fifo_waits_for_the_cpu_to_read()
    {
        // in x, 32 side 1 (autopush at 32) ; jmp x-- 0 side 0
        var (eager, lazy) = Run([0x5020, 0x0040], 1u << 16,
            (r, rng) => { for (int k = rng.Next(3); k > 0; k--) r.Rx.Add(r.Pio.ReadWord(Rxf0)); });
        Assert.True(eager.Rx.Count > 50);
        Assert.True(lazy.MaxEvent > 1000);
    }

    [Fact]
    public void Wait_irq_waits_for_the_cpu_to_force_the_flag()
    {
        // wait 1 irq 0 side 0 ; nop side 1 [3]
        var (eager, lazy) = Run([0x20C0, 0xB342], 0,
            (r, rng) => { if (rng.Next(2) == 0) r.Pio.WriteWord(IrqForce, 1); });
        Assert.True(eager.Edges.Count > 50);
        Assert.True(lazy.MaxEvent > 1000);
    }

    [Fact]
    public void Irq_wait_and_the_cpu_clearing_its_flag_agree()
    {
        // irq wait 1 side 1 ; nop side 0 [2]. (Here a stalled IRQ WAIT raises its flag again on every re-run.)
        Run([0xD021, 0xA242], 0, (r, rng) => { if (rng.Next(2) == 0) r.Pio.WriteWord(Irq, 2); });
    }

    [Fact]
    public void Two_machines_handing_an_irq_over_keep_their_edges()
    {
        // SM0: irq set 1 side 1 ; wait 1 irq 0 side 0 ; jmp 0. SM1 (from 3, side-set on GPIO2): wait 1 irq 1 side 1 ;
        // irq set 0 side 0 ; jmp 3. One waits while the other runs; the CPU forces and clears flags now and then.
        uint pinctrl1 = (1u << 29) | (2u << 10) | (1u << 26) | (1u << 20);
        var (eager, _) = Run([0xD001, 0x20C0, 0x0000, 0x30C1, 0xC000, 0x0003], 0,
            (r, rng) =>
            {
                if (rng.Next(4) == 0) r.Pio.WriteWord(IrqForce, (uint)rng.Next(1, 4));
                if (rng.Next(4) == 0) r.Pio.WriteWord(Irq, (uint)rng.Next(1, 4));
            }, enable: 3, pinctrl1: pinctrl1, setup: p => p.WriteWord(Sm0 + 0x18 + 0x10, 0x0003));
        Assert.True(eager.Edges.Count > 20);
    }

    [Fact]
    public void Gpio_waits_and_conflicting_side_sets_stay_due()
    {
        // wait 1 gpio 0 side 1: GPIO0 is the SM's own OUT pin, never high; the stall re-reads a pad, so it is not quiet.
        var (_, lazy) = Run([0x3080, 0xA042], 0, (r, rng) => { }, sweeps: 2000);
        Assert.True(lazy.MaxEvent < 64, "a GPIO wait keeps the SM due at its divider edges: " + lazy.MaxEvent);
        // Two SMs stalled on empty TX FIFOs side-set GPIO1 to opposite levels on every re-run: neither is quiet.
        var (_, both) = Run([0x6001, 0x7001], 1u << 17, (r, rng) => { }, sweeps: 2000, enable: 3,
            setup: p => p.WriteWord(Sm0 + 0x18 + 0x10, 0x0001));
        Assert.True(both.MaxEvent < 64, "conflicting side-sets keep the block due: " + both.MaxEvent);
    }
}

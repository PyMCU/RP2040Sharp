using System.Numerics;

namespace RP2040.Peripherals;

/// <summary>A pad change: the pad's effective output and the core-0 cycle it changed at.</summary>
public readonly record struct PadEvent(int Gpio, bool Level, bool OutputEnable, ulong Cycle);

/// <summary>How a <see cref="PadEventRecorder"/> hands its events over.</summary>
public enum PadDelivery
{
    /// <summary>Kept until <see cref="PadEventRecorder.Drain"/>, which reports them in cycle order (the C ABI: the host
    /// sees a call's pad changes when it returns).</summary>
    Drain,
    /// <summary>Handed to <see cref="PadEventRecorder.Immediate"/> while they happen, so a handler can answer at once (a
    /// bit-level device model on the managed adapter). Nothing is kept.</summary>
    Immediate,
}

/// <summary>A batch of changes delivered at once: the pads that moved and the core-0 cycle of the change.</summary>
public delegate void PadBatchHandler(uint changed, ulong cycle);

/// <summary>
/// Records every pad change that goes through IO_BANK0 while it happens, stamped with its own core-0 cycle, and hands
/// the changes over in cycle order (<see cref="PadDelivery.Drain"/>) or at once (<see cref="PadDelivery.Immediate"/>).
/// <list type="bullet">
/// <item>A CPU write (SIO, IO_BANK0, PADS), on either core: the cycle of that instruction; core 1's is mapped onto core
/// 0's clock from the start of its turn (<see cref="RP2040Machine.ExecCycle"/>).</item>
/// <item>A pin-level UART / SPI / I2C engine, or a PWM counter step with <see cref="RP2040Machine.ExactPwmPadEdges"/>:
/// the cycle of the edge itself (<see cref="IoBank0Peripheral.LastPadEdgeCycle"/>).</item>
/// <item>Anything else in a sweep (PIO, DMA into SIO), and <see cref="Poll"/>: core 0's clock, the end of the slice.</item>
/// </list>
/// Nothing is reported past core 0's own clock (core 1 may run more cycles in a slice): a stamp is clamped to it when
/// it is delivered. The events of a drain are sorted by cycle, stably (they are already in order but for core 1's,
/// which follow core 0's of the same slice).
/// </summary>
/// <remarks>Chip-free in its contract (pads, levels, cycles): the RP2350 side keeps a recorder with the same shape.</remarks>
public sealed class PadEventRecorder
{
    /// <summary>The 30 user pads of IO_BANK0.</summary>
    public const uint AllPads = (1u << 30) - 1;

    private readonly RP2040Machine _m;
    private readonly Action<uint> _hook;
    private uint _lastOe, _lastLevel;
    private bool _attached;
    private List<PadEvent> _pending = [];
    private List<PadEvent> _reporting = [];

    public PadEventRecorder(RP2040Machine machine)
    {
        _m = machine;
        _hook = OnPadsChanged;
    }

    /// <summary>How events are handed over (default <see cref="PadDelivery.Drain"/>).</summary>
    public PadDelivery Delivery { get; set; } = PadDelivery.Drain;

    /// <summary>The <see cref="PadDelivery.Immediate"/> handler.</summary>
    public PadBatchHandler? Immediate { get; set; }

    /// <summary>
    /// Count a level change only on a driven pad (an output register moving under a pad nobody drives is no change),
    /// as the SiliconTwin.Abstractions contract has it. Off (the C ABI), every level and output-enable change counts.
    /// </summary>
    public bool LevelsOnlyWhenDriven { get; set; }

    /// <summary>Pads whose changes raise <see cref="WatchedEdge"/> and set <see cref="WatchedChanged"/>.</summary>
    public uint Watch { get; set; }

    /// <summary>Set by a change on a <see cref="Watch"/>ed pad; the run loop clears it.</summary>
    public bool WatchedChanged { get; set; }

    /// <summary>Raised on a change to a watched pad, while it happens (the run loop ends a bounded run, cuts a slice).</summary>
    public Action? WatchedEdge { get; set; }

    /// <summary>Events recorded and not yet drained.</summary>
    public int Pending => _pending.Count;

    /// <summary>Listen to IO_BANK0, starting from the pads as they are now (<paramref name="baseline"/>) or from every pad
    /// undriven and low, as a chip that reports its power-on state does.</summary>
    public void Attach(bool baseline = true)
    {
        if (_attached) return;
        _attached = true;
        if (baseline) Baseline();
        _m.IoBank0.PadsChanged += _hook;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _m.IoBank0.PadsChanged -= _hook;
    }

    /// <summary>Take the pads as they are now as the reference, without recording anything.</summary>
    public void Baseline()
    {
        _m.IoBank0.SyncPadBaseline();
        _m.IoBank0.GetPadOutputState(out _lastOe, out _lastLevel);
        _lastOe &= AllPads;
        _lastLevel &= AllPads;
    }

    /// <summary>The core-0 cycle of the change being reported right now (see the class summary), not yet clamped.</summary>
    public ulong Stamp()
    {
        var io = _m.IoBank0;
        return (ulong)(io.FunctionDriving ? io.LastPadEdgeCycle : _m.ExecCycle());
    }

    private void OnPadsChanged(uint mask) => Record(mask, Stamp());

    /// <summary>Compare the pads in <paramref name="mask"/> with the reference and record the changes at
    /// <paramref name="cycle"/>.</summary>
    public void Record(uint mask, ulong cycle)
    {
        mask &= AllPads;
        if (mask == 0) return;
        var io = _m.IoBank0;
        uint changed = 0;
        for (var rest = mask; rest != 0; rest &= rest - 1)
        {
            var g = BitOperations.TrailingZeroCount(rest);
            var bit = 1u << g;
            bool oe = io.GetPadOutputEnable(g), lvl = io.GetPadOutputLevel(g);
            bool wasOe = (_lastOe & bit) != 0, wasLvl = (_lastLevel & bit) != 0;
            var moved = oe != wasOe || (lvl != wasLvl && (!LevelsOnlyWhenDriven || oe));
            _lastOe = oe ? _lastOe | bit : _lastOe & ~bit;
            _lastLevel = lvl ? _lastLevel | bit : _lastLevel & ~bit;
            if (!moved) continue;
            changed |= bit;
            if (Delivery == PadDelivery.Drain) _pending.Add(new PadEvent(g, lvl, oe, cycle));
        }
        if (changed == 0) return;
        if (Delivery == PadDelivery.Immediate) Immediate?.Invoke(changed, Clamp(cycle, (ulong)_m.Cpu.Cycles));
        if ((changed & Watch) != 0)
        {
            WatchedChanged = true;
            WatchedEdge?.Invoke();
        }
    }

    /// <summary>Compare the pads in <paramref name="mask"/> now, at core 0's clock: for changes IO_BANK0 never reports
    /// (pads muxed to PWM without <see cref="RP2040Machine.ExactPwmPadEdges"/>).</summary>
    public void Poll(uint mask) => Record(mask, (ulong)_m.Cpu.Cycles);

    /// <summary>
    /// Hand every event recorded since the last drain to <paramref name="sink"/> in cycle order, clamped to core 0's
    /// clock, then forget them. The sink may make the pads move again (a host answering with an input): those land in a
    /// fresh list for the next drain.
    /// </summary>
    public void Drain(Action<PadEvent> sink)
    {
        if (_pending.Count == 0) return;
        (_reporting, _pending) = (_pending, _reporting);
        var events = _reporting;
        var now = (ulong)_m.Cpu.Cycles;
        SortByCycle(events);
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                sink(e with { Cycle = Clamp(e.Cycle, now) });
            }
        }
        finally { events.Clear(); }
    }

    /// <summary>Nothing is reported past core 0's own clock.</summary>
    public static ulong Clamp(ulong cycle, ulong now) => Math.Min(cycle, now);

    /// <summary>Stable insertion sort by cycle: the list is already in order but for core 1's changes, which follow
    /// core 0's.</summary>
    public static void SortByCycle(List<PadEvent> events)
    {
        for (var i = 1; i < events.Count; i++)
        {
            var e = events[i];
            var j = i - 1;
            if (events[j].Cycle <= e.Cycle) continue;
            while (j >= 0 && events[j].Cycle > e.Cycle) { events[j + 1] = events[j]; j--; }
            events[j + 1] = e;
        }
    }
}

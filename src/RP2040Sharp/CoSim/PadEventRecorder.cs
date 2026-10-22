using System.Numerics;
using RP2040.Peripherals;

namespace RP2040.CoSim;

/// <summary>How a <see cref="PadEventRecorder"/> hands its events to the host.</summary>
public enum PadReportMode
{
    /// <summary>Events are kept and handed over by <see cref="PadEventRecorder.Drain"/> in cycle order: a host that
    /// answers between calls (the C ABI). Every change is kept, not only a pad's last one.</summary>
    Deferred,

    /// <summary>Each change is raised at once, from inside the run, through <see cref="PadEventRecorder.Immediate"/>: a
    /// host that answers from the event (a bit-level device model, the managed <see cref="RP2040Mcu"/>). Nothing is kept.</summary>
    Immediate,
}

/// <summary>Receives a batch of pad changes in <see cref="PadReportMode.Immediate"/> mode: the pads that moved, the output
/// enable and the driven level (level AND output enable) of every watched pad, and the core-0 cycle of the change.</summary>
public delegate void PadChangeHandler(ulong changed, ulong outputEnable, ulong levels, ulong cycle);

/// <summary>
/// Records every pad change that goes through IO_BANK0 while it happens, stamped with its own core-0 cycle, and hands
/// the changes over in cycle order (<see cref="PadReportMode.Deferred"/>) or at once (<see cref="PadReportMode.Immediate"/>).
/// <list type="bullet">
/// <item>A CPU write (SIO, IO_BANK0, PADS), on either core: the cycle of that instruction; core 1's is mapped onto core
/// 0's clock from the start of its turn (<see cref="RP2040Machine.ExecCycle"/>).</item>
/// <item>A pin-level UART / SPI / I2C engine, or a PWM counter step with <see cref="RP2040Machine.ExactPwmPadEdges"/>:
/// the cycle of the edge itself (<see cref="IoBank0Peripheral.LastPadEdgeCycle"/>).</item>
/// <item>Anything else in a sweep (PIO, DMA into SIO), and <see cref="Poll"/>: core 0's clock, the end of the slice.</item>
/// </list>
/// Nothing is reported past core 0's own clock (core 1 may run more cycles in a slice) nor before the previous report
/// (<see cref="ReportedTo"/>): a stamp is clamped into that window when it is delivered. The events of a drain are sorted
/// by cycle, pad order among equal cycles, one pad's changes at one cycle folded into one (see <see cref="Drain"/>).
/// </summary>
/// <remarks>The public surface follows RP2350Sharp's RP2350.CoSim.PadEventRecorder (Mode, Immediate, ImmediateWatch,
/// Drain, Snapshot, ReportedTo, Attached), so the two can be unified; see docs/cosim.md for what differs.</remarks>
public sealed class PadEventRecorder
{
    /// <summary>The 30 user pads of IO_BANK0.</summary>
    public const uint AllPads = (1u << 30) - 1;

    private readonly RP2040Machine _m;
    private readonly Action<uint> _hook;
    private uint _lastOe, _lastLevel;            // pad state as of the last record
    private uint _repOe, _repLevel;              // pad state as of the last drain
    private ulong _immOe, _immLevel;             // immediate mode: the state last raised (driven levels)
    private ulong _immWatch = AllPads;
    private ulong _reportedTo;
    private List<(int Gpio, bool Level, bool Oe, ulong Cycle)> _pending = [];
    private List<(int Gpio, bool Level, bool Oe, ulong Cycle)> _reporting = [];

    /// <summary>Builds a recorder for the 30 pads of <paramref name="machine"/>; not attached yet.</summary>
    public PadEventRecorder(RP2040Machine machine)
    {
        _m = machine;
        _hook = OnPadsChanged;
    }

    /// <summary>Number of pads covered.</summary>
    public int PinCount => 30;

    /// <summary>Deferred (the default) or immediate reporting.</summary>
    public PadReportMode Mode { get; set; }

    /// <summary>Immediate mode: raised for every change of a pad in <see cref="ImmediateWatch"/>.</summary>
    public PadChangeHandler? Immediate;

    /// <summary>Immediate mode: the pads reported (all by default). Pads entering it start from their current state.</summary>
    public ulong ImmediateWatch
    {
        get => _immWatch;
        set
        {
            value &= AllPads;
            Snapshot(value & ~_immWatch);
            _immWatch = value;
        }
    }

    /// <summary>
    /// Count a level change only on a driven pad (an output register moving under a pad nobody drives is no change),
    /// as the SiliconTwin.Abstractions contract has it; immediate mode always does. Off (the C ABI), every level and
    /// output-enable change counts.
    /// </summary>
    public bool LevelsOnlyWhenDriven { get; set; }

    /// <summary>Pads whose changes raise <see cref="WatchedEdge"/> and set <see cref="WatchedChanged"/>: the run loop's.</summary>
    public uint Watch { get; set; }

    /// <summary>Set by a change on a <see cref="Watch"/>ed pad; the run loop clears it.</summary>
    public bool WatchedChanged { get; set; }

    /// <summary>Raised on a change to a watched pad, while it happens (the run loop ends a bounded run, cuts a slice).</summary>
    public Action? WatchedEdge { get; set; }

    /// <summary>Events recorded and not yet drained (deferred mode).</summary>
    public int Pending => _pending.Count;

    /// <summary>Core-0 cycle of the last <see cref="Drain"/>: no stamp is delivered before it.</summary>
    public ulong ReportedTo => _reportedTo;

    /// <summary>True while IO_BANK0's pad changes are hooked.</summary>
    public bool Attached { get; private set; }

    /// <summary>Hooks IO_BANK0, starting from the pads as they are now (<paramref name="baseline"/>), or from every pad
    /// undriven and low, as a chip that reports its power-on state does. Idempotent.</summary>
    public void Attach(bool baseline = true)
    {
        if (Attached) return;
        Attached = true;
        if (baseline) Baseline();
        _m.IoBank0.PadsChanged += _hook;
    }

    /// <summary>Unhooks IO_BANK0 (nothing is recorded). Idempotent.</summary>
    public void Detach()
    {
        if (!Attached) return;
        Attached = false;
        _m.IoBank0.PadsChanged -= _hook;
    }

    /// <summary>Take the pads as they are now as the reference, without recording anything.</summary>
    public void Baseline()
    {
        _m.IoBank0.SyncPadBaseline();
        _m.IoBank0.GetPadOutputState(out _lastOe, out _lastLevel);
        _lastOe &= AllPads;
        _lastLevel &= AllPads;
        (_repOe, _repLevel) = (_lastOe, _lastLevel);
        Snapshot(AllPads);
    }

    /// <summary>Immediate mode: take the current state of <paramref name="mask"/> as already reported.</summary>
    public void Snapshot(ulong mask)
    {
        mask &= AllPads;
        _m.IoBank0.GetPadOutputState(out var oe, out var lv);
        _immOe = (_immOe & ~mask) | (oe & mask);
        _immLevel = (_immLevel & ~mask) | (lv & oe & mask);
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
        bool onlyDriven = LevelsOnlyWhenDriven || Mode == PadReportMode.Immediate;
        for (var rest = mask; rest != 0; rest &= rest - 1)
        {
            var g = BitOperations.TrailingZeroCount(rest);
            var bit = 1u << g;
            bool oe = io.GetPadOutputEnable(g), lvl = io.GetPadOutputLevel(g);
            bool wasOe = (_lastOe & bit) != 0, wasLvl = (_lastLevel & bit) != 0;
            var moved = oe != wasOe || (lvl != wasLvl && (!onlyDriven || oe));
            _lastOe = oe ? _lastOe | bit : _lastOe & ~bit;
            _lastLevel = lvl ? _lastLevel | bit : _lastLevel & ~bit;
            if (!moved) continue;
            changed |= bit;
            if (Mode == PadReportMode.Deferred) _pending.Add((g, lvl, oe, cycle));
        }
        if (changed == 0) return;
        if (Mode == PadReportMode.Immediate) Raise(changed, cycle);
        if ((changed & Watch) != 0)
        {
            WatchedChanged = true;
            WatchedEdge?.Invoke();
        }
    }

    private void Raise(uint changed, ulong cycle)
    {
        var mask = changed & _immWatch;
        if (mask == 0) return;
        _immOe = (_immOe & ~mask) | (_lastOe & mask);
        _immLevel = (_immLevel & ~mask) | (_lastLevel & _lastOe & mask);
        Immediate?.Invoke(mask, _immOe & _immWatch, _immLevel & _immWatch, Clamp(cycle, _reportedTo, (ulong)_m.Cpu.Cycles));
    }

    /// <summary>Compare the pads in <paramref name="mask"/> now, at core 0's clock: for changes IO_BANK0 never reports
    /// (pads muxed to PWM without <see cref="RP2040Machine.ExactPwmPadEdges"/>).</summary>
    public void Poll(uint mask) => Record(mask, (ulong)_m.Cpu.Cycles);

    /// <summary>
    /// Deferred mode: hand every event recorded since the last drain to <paramref name="sink"/> (gpio, level, output
    /// enable, cycle), clamped into [<see cref="ReportedTo"/>, core 0's clock], in cycle order (pad order among equal
    /// cycles), then forget them; the changes of one pad at one cycle are one change, to the last state (none when that
    /// is where the pad was): a zero-width pulse depends on how a sweep batched the work, not on the signal. No sink only
    /// moves the report point. The sink may make the pads move again (a host answering with an input): those land in a
    /// fresh list for the next drain. Immediate mode: moves the report point. (RP2350Sharp's recorder reports the same.)
    /// </summary>
    public void Drain(Action<int, bool, bool, ulong>? sink)
    {
        var now = (ulong)_m.Cpu.Cycles;
        var from = _reportedTo;
        _reportedTo = now;
        uint pOe = _repOe, pLv = _repLevel;   // as reported so far
        (_repOe, _repLevel) = (_lastOe, _lastLevel);
        if (_pending.Count == 0) return;
        (_reporting, _pending) = (_pending, _reporting);
        var events = _reporting;
        try
        {
            if (sink is null) return;
            for (var i = 0; i < events.Count; i++) events[i] = events[i] with { Cycle = Clamp(events[i].Cycle, from, now) };
            SortByCycle(events);
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                bool group = false;
                while (i + 1 < events.Count && events[i + 1].Gpio == e.Gpio && events[i + 1].Cycle == e.Cycle) { e = events[++i]; group = true; }
                var bit = 1u << e.Gpio;
                if (group && e.Oe == ((pOe & bit) != 0) && e.Level == ((pLv & bit) != 0)) continue;
                pOe = e.Oe ? pOe | bit : pOe & ~bit;
                pLv = e.Level ? pLv | bit : pLv & ~bit;
                sink(e.Gpio, e.Level, e.Oe, e.Cycle);
            }
        }
        finally { events.Clear(); }
    }

    /// <summary>A stamp is delivered inside the report window: never before the last report, never past core 0's clock.</summary>
    public static ulong Clamp(ulong cycle, ulong from, ulong now) => cycle < from ? from : cycle > now ? now : cycle;

    /// <summary>Stable insertion sort by (cycle, gpio): the list is already in order but for core 1's changes, which
    /// follow core 0's, and pads that moved at the same cycle.</summary>
    private static void SortByCycle(List<(int Gpio, bool Level, bool Oe, ulong Cycle)> events)
    {
        for (var i = 1; i < events.Count; i++)
        {
            var e = events[i];
            var j = i - 1;
            if (!After(events[j], e)) continue;
            while (j >= 0 && After(events[j], e)) { events[j + 1] = events[j]; j--; }
            events[j + 1] = e;
        }
    }

    private static bool After((int Gpio, bool Level, bool Oe, ulong Cycle) a, (int Gpio, bool Level, bool Oe, ulong Cycle) b) =>
        a.Cycle > b.Cycle || (a.Cycle == b.Cycle && a.Gpio > b.Gpio);
}

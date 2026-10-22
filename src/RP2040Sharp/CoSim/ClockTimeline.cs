namespace RP2040.CoSim;

/// <summary>
/// Simulated time of a co-simulation: core-0 cycles to picoseconds. The RP2040 core runs a fixed clk_sys
/// (<see cref="Peripherals.RP2040Machine.CLK_HZ"/>, it does not model clock reconfiguration), so the history is one
/// segment; the type is RP2350Sharp's RP2350.CoSim.ClockTimeline (same members and rules), where firmware does change
/// clk_sys and a run that ends at another frequency starts a new segment at its last clock register write.
/// </summary>
public sealed class ClockTimeline
{
    /// <summary>A stretch of constant clk_sys: from core-0 cycle <see cref="Cycle"/> (at <see cref="Ps"/>) at <see cref="Hz"/>.</summary>
    public readonly record struct Segment(ulong Cycle, ulong Ps, uint Hz);

    private readonly List<Segment> _segments = [];
    private uint _hz;
    private long _write = -1;   // core-0 cycle of the last clock register write since the last Track

    /// <summary>Starts the history at cycle 0 with clk_sys = <paramref name="hz"/>.</summary>
    public ClockTimeline(uint hz)
    {
        _hz = hz;
        _segments.Add(new Segment(0, 0, hz));
    }

    /// <summary>The clk_sys of the latest segment.</summary>
    public uint Hz => _hz;

    /// <summary>The history, oldest first.</summary>
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Picoseconds of <paramref name="cycles"/> cycles at <paramref name="hz"/>, exact to the picosecond without
    /// 128-bit math (whole seconds, then two 1e6 stages).</summary>
    public static ulong PsIn(ulong cycles, uint hz)
    {
        ulong sec = cycles / hz, rem = cycles % hz;
        ulong a = rem * 1_000_000UL, us = a / hz;
        return sec * 1_000_000_000_000UL + us * 1_000_000UL + (a % hz) * 1_000_000UL / hz;
    }

    /// <summary>Simulated picoseconds elapsed at core-0 cycle <paramref name="cycle"/> (a past or the current one).</summary>
    public ulong CycleToPs(ulong cycle)
    {
        int i = _segments.Count - 1;
        while (i > 0 && _segments[i].Cycle > cycle) i--;
        var s = _segments[i];
        return s.Ps + PsIn(cycle > s.Cycle ? cycle - s.Cycle : 0, s.Hz);
    }

    /// <summary>A clock register is being written at core-0 cycle <paramref name="cycle"/> (where clk_sys may change).</summary>
    public void NoteWrite(long cycle) => _write = cycle;

    /// <summary>After a run ending at core-0 cycle <paramref name="now"/> with clk_sys = <paramref name="hz"/>: starts a
    /// new segment when the frequency changed. Returns true when it did. (Never on the RP2040, whose clock is fixed.)</summary>
    public bool Track(uint hz, ulong now)
    {
        var write = _write;
        _write = -1;
        if (hz == _hz) return false;
        _hz = hz;
        var cyc = now;
        if (write >= 0 && (ulong)write >= _segments[^1].Cycle && (ulong)write < cyc)
        {
            cyc = (ulong)write;
            // Written where the last segment starts (e.g. the host's clock setup before the first run): it ran at the new rate.
            if (cyc == _segments[^1].Cycle) { _segments[^1] = _segments[^1] with { Hz = hz }; return true; }
        }
        _segments.Add(new Segment(cyc, CycleToPs(cyc), hz));
        return true;
    }
}

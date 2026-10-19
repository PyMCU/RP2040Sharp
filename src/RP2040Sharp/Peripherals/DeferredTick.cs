using System.Runtime.CompilerServices;

namespace RP2040.Peripherals;

/// <summary>
/// A peripheral whose <see cref="ITickable.Tick"/> can be put off until it has something to do. Ticking it once with
/// the sum of several sweeps' cycles must leave it exactly where ticking it once per sweep would have, as long as
/// none of those sweeps reached an event; <see cref="CyclesUntilEvent"/> says how far that holds.
/// </summary>
public interface IDeferrableTickable : ITickable
{
    /// <summary>
    /// Swept cycles, counted from the state as of the last tick, before the first sweep whose tick does more than
    /// count: raises or lowers an interrupt or DREQ, moves a pad, calls out, or changes anything a register read or
    /// a later tick could tell from counting. 0 means the next sweep, <see cref="long.MaxValue"/> never. A lower
    /// bound is always safe (the tick just runs early); a value past the event is a bug.
    /// </summary>
    long CyclesUntilEvent();

    /// <summary>
    /// Bring the state forward by <paramref name="cycles"/> that sweeps already covered without reaching an event,
    /// outside the peripheral's turn in a sweep (a register access, a host call). Defaults to a tick.
    /// </summary>
    void CatchUp(long cycles) => Tick(cycles);

    /// <summary>
    /// The peripheral's turn in a sweep at which its tick is put off: whatever a tick does on every sweep even with
    /// nothing to count (PIO's interrupt heartbeat). Must be idempotent. Defaults to nothing.
    /// </summary>
    void SkipTick() { }
}

/// <summary>
/// The deferred tick of one peripheral (see docs/peripheral-deadlines.md). Sweeps add their cycles to the machine's
/// <see cref="RP2040Machine.SweptCycles"/>; the peripheral is ticked with what it owes only when that reaches its
/// next event, or brought up to date (<see cref="Sync"/>) right before anything reads or changes its state, so every
/// access sees what a tick per sweep would have left there.
/// </summary>
public sealed class DeferredTick
{
    private readonly RP2040Machine _machine;
    internal readonly ITickable Target;
    internal readonly IDeferrableTickable? Deferrable;
    internal readonly int Index;

    /// <summary>Swept cycles this peripheral has been ticked or caught up to.</summary>
    internal long Applied;

    private long _eventAt;
    private bool _dirty = true;

    /// <summary>Ask the peripheral every time instead of caching: for ones whose next event moves with state that
    /// other peripherals change (DMA, the SPI TX FIFO) and that answer in a compare.</summary>
    internal bool AlwaysAsk;

    internal DeferredTick(RP2040Machine machine, ITickable target, int index)
    {
        _machine = machine;
        Target = target;
        Deferrable = target as IDeferrableTickable;
        Index = index;
    }

    /// <summary>Swept cycles at which the next event falls (<see cref="long.MaxValue"/>: none).</summary>
    internal long EventAt
    {
        get
        {
            if (_dirty || AlwaysAsk)
            {
                _dirty = false;
                var d = Deferrable!.CyclesUntilEvent();
                _eventAt = d >= long.MaxValue - Applied ? long.MaxValue : Applied + Math.Max(0, d);
            }
            return _eventAt;
        }
    }

    /// <summary>Apply the cycles of the sweeps this peripheral has already been through. Free when there are none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sync()
    {
        if (Deferrable == null) return;
        var target = _machine.SweptFor(Index);
        if (target > Applied) SyncSlow(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SyncSlow(long target)
    {
        var c = target - Applied;
        Applied = target;
        Deferrable!.CatchUp(c);
    }

    /// <summary>The peripheral's state changed outside a tick (an access, a host call): ask for its next event again.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Touch()
    {
        _dirty = true;
        _machine.ScheduleEpoch++;
    }

    /// <summary>This peripheral's turn in a sweep that brought the swept total to <paramref name="swept"/>.</summary>
    internal void OnSweep(long swept)
    {
        if (swept >= EventAt)
        {
            var c = swept - Applied;
            Applied = swept;
            Target.Tick(c);
            _dirty = true;
            _machine.ScheduleEpoch++;
        }
        else Deferrable!.SkipTick();
    }

    /// <summary>Eager mode: the tick runs every sweep, so nothing is ever owed.</summary>
    internal void Reset(long swept)
    {
        Applied = swept;
        _dirty = true;
    }
}

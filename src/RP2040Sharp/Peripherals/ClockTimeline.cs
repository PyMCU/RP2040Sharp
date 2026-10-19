namespace RP2040.Peripherals;

/// <summary>
/// Maps the peripherals' time base (the machine's swept cycles, <see cref="RP2040Machine.SweptCycles"/>) to core 0's
/// cycle counter, the clock every pad event is stamped with. The peripherals sweep at the end of each slice with the
/// longer of the two cores' cycles; the slice's window of swept cycles lines up with core 0's from its start, and a
/// position past core 0's end (core 1 ran longer) maps to that end, as nothing is reported past core 0's clock.
/// A tick that reaches an event always falls in the last sweep's window (a sweep ticks a deferred peripheral when it
/// reaches the peripheral's next event), so the last window is all a peripheral needs to stamp its own edges.
/// </summary>
/// <remarks>Chip-free on purpose (no machine reference): the RP2350 side keeps the same type for its clock changes.
/// The RP2040 core runs a fixed 125 MHz, so swept cycles and core-0 cycles only differ by where a window starts.</remarks>
public sealed class ClockTimeline
{
    private long _sweptEnd, _sweptDelta, _cycleEnd, _cycleDelta;

    /// <summary>Swept total at the end of the last window.</summary>
    public long SweptEnd => _sweptEnd;

    /// <summary>Core-0 cycle at the end of the last window.</summary>
    public long CycleEnd => _cycleEnd;

    /// <summary>A sweep of <paramref name="sweptDelta"/> cycles brought the swept total to <paramref name="sweptEnd"/>, at
    /// the end of a slice in which core 0 ran <paramref name="cycleDelta"/> cycles up to <paramref name="cycleEnd"/>.</summary>
    public void Mark(long sweptEnd, long sweptDelta, long cycleEnd, long cycleDelta)
    {
        _sweptEnd = sweptEnd;
        _sweptDelta = sweptDelta;
        _cycleEnd = cycleEnd;
        _cycleDelta = cycleDelta;
    }

    /// <summary>Core-0 cycle of swept position <paramref name="swept"/> in the last window (clamped to it).</summary>
    public long CycleAt(long swept)
    {
        var intoWindow = swept - (_sweptEnd - _sweptDelta);
        if (intoWindow <= 0) return _cycleEnd - _cycleDelta;
        return Math.Min(_cycleEnd, _cycleEnd - _cycleDelta + intoWindow);
    }
}

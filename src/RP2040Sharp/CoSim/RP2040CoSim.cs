using System.Runtime.CompilerServices;
using RP2040.Peripherals;
using RP2040.Peripherals.PinLevel;

namespace RP2040.CoSim;

/// <summary>The speed-ups and stops of a co-simulation run (<see cref="RunRequest.Flags"/>).</summary>
[Flags]
public enum CoSimFlags
{
    None = 0,
    /// <summary>With pin-level buses, a watch and a guard above 1: core 0's slices end at the next engine edge on a
    /// watched pad and right after an instruction that moved one (docs/pin-level-buses.md).</summary>
    EventGuard = 1,
    /// <summary>While both cores sleep quietly, the sleeping slices before the next event are booked at once.</summary>
    SleepingStretches = 2,
    /// <summary>While core 0 is awake and core 1 inert, its slices run in one bounded interpreter loop.</summary>
    BoundedRuns = 4,
    /// <summary>Return after the slice in which a watched pad changed (the C ABI's run_until_edge).</summary>
    StopOnWatchedEdge = 8,
    /// <summary>Watched slices are twice the guard while both cores sleep (a wake-up can only follow an event).</summary>
    AdaptiveSleep = 16,
    /// <summary>Everything: what the native chip runs with.</summary>
    All = EventGuard | SleepingStretches | BoundedRuns | StopOnWatchedEdge | AdaptiveSleep,
}

/// <summary>Why <see cref="RP2040CoSim.RunUntilEdge"/> returned.</summary>
public enum StopReason
{
    /// <summary><see cref="RunRequest.MaxSlots"/> instruction slots ran.</summary>
    Budget,
    /// <summary>Core 0 reached <see cref="RunRequest.TargetCycle"/>.</summary>
    Target,
    /// <summary>A watched pad changed (<see cref="CoSimFlags.StopOnWatchedEdge"/>).</summary>
    WatchedEdge,
    /// <summary>A watchdog reboot restarted core 0's counter during a targeted run: the target no longer applies.</summary>
    Reset,
}

/// <summary>One <see cref="RP2040CoSim.RunUntilEdge"/> call.</summary>
public struct RunRequest
{
    /// <summary>Upper bound of instruction slots per core (a sleeping core's slots count as cycles).</summary>
    public int MaxSlots;
    /// <summary>Core-0 cycle to stop at: no slot of core 0 starts at or past it, so the run ends at the first instruction
    /// boundary at or past it (a sleeping core exactly on it). 0: none.</summary>
    public long TargetCycle;
    /// <summary>Slots per slice while a pad is watched (twice that while both cores sleep, with
    /// <see cref="CoSimFlags.AdaptiveSleep"/>). 0 with no watch: slices of <see cref="RP2040CoSim.Quantum"/>.</summary>
    public int Guard;
    /// <summary>Watched pads (bit n = GPIO n).</summary>
    public ulong Watch;
    /// <summary>What the call may use (see <see cref="CoSimFlags"/>).</summary>
    public CoSimFlags Flags;
}

/// <summary>What a <see cref="RP2040CoSim.RunUntilEdge"/> call did: the instruction slots run per core, the core-0 cycles
/// they took (across a watchdog reboot: up to the end of the slice it happened in) and why it returned.</summary>
public readonly record struct RunResult(int Slots, long Cycles, StopReason Reason);

/// <summary>
/// The co-simulation loop of an <see cref="RP2040Machine"/>, shared by the C ABI chip (st_rp2040_run_until_edge) and the
/// managed adapter (<see cref="RP2040Mcu.Run"/>): slices of a guard or of <see cref="Quantum"/> instructions per core,
/// each followed by the peripheral sweep, with pad changes recorded at their own cycles (<see cref="Pads"/>). Sleeping
/// stretches, bounded awake runs and the event-bounded guard take several of those slices at once, or end one early,
/// with exactly their result (docs/peripheral-deadlines.md, docs/cosim.md).
/// </summary>
/// <remarks>Mirrors RP2350Sharp's RP2350CoSim (same request, result and flag names), so the chip-free parts can be
/// shared. iOS AOT friendly: no generic virtual methods; the interpreter loops it uses are closed struct instantiations.</remarks>
public sealed class RP2040CoSim
{
    public const uint AllPads = PadEventRecorder.AllPads;

    public RP2040Machine Machine { get; }

    /// <summary>Every pad change with its own cycle (deferred reporting unless the owner changes it).</summary>
    public PadEventRecorder Pads { get; }

    /// <summary>Core-0 cycles to simulated picoseconds (one segment: the RP2040 clock is fixed).</summary>
    public ClockTimeline Clock { get; } = new(RP2040Machine.CLK_HZ);

    /// <summary>
    /// Most instructions a slice runs before the peripherals advance (they tick once per slice). USB enumeration and
    /// the MicroPython REPL need them at least this often; the native chip and the TestKit use 512.
    /// </summary>
    public int Quantum { get; set; } = 512;

    /// <summary>Conservative per-core retired-instruction count: a slice is credited to a core only if that core was
    /// neither asleep (WFI/WFE), locked up nor (core 1) unlaunched at its start and at its end. A lower bound, exact
    /// for CPU-bound code, low by up to one slice per sleep/wake transition.</summary>
    public readonly ulong[] Retired = new ulong[2];

    /// <summary>Probes: slices booked by stretches and by bounded runs, slices run plainly, slices a cut ended early.</summary>
    public long StretchedSlices, BoundedSlices, PlainSlices, CutSlices;

    /// <summary>Called before every slice that runs instructions (fault-injection tests). A bounded run degrades to
    /// plain slices while it is set.</summary>
    public Action? BeforeSlice;

    private bool _stopOnEdge;

    public RP2040CoSim(RP2040Machine machine, PadEventRecorder? pads = null)
    {
        Machine = machine;
        Pads = pads ?? new PadEventRecorder(machine);
        // A watched pad's change ends a bounded run and cuts a cut slice right after the instruction that made it.
        Pads.WatchedEdge = () =>
        {
            if (!_stopOnEdge) return;
            Machine.EndBoundedRun = true;
            Machine.RequestCut();
        };
        // A bounded run books slice ends without coming back to the loop: poll the watched PWM pads at each of them.
        Machine.BoundaryCheck = () =>
        {
            uint pwm = Machine.IoBank0.PwmMuxedPins & Pads.Watch;
            if (pwm != 0 && !Machine.ExactPwmPadEdges) Pads.Poll(pwm);
            return _stopOnEdge && Pads.WatchedChanged;
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Parked(int core) => core == 0
        ? Machine.Cpu.Registers.Waiting || Machine.Cpu.IsLockedUp
        : !Machine.Core1Launched || Machine.Cpu1.Registers.Waiting || Machine.Cpu1.IsLockedUp;

    /// <summary>
    /// <paramref name="n"/> instruction slots per core in slices of <see cref="Quantum"/>, with the slice boundaries
    /// counted across calls (st_rp2040_run): they fall at the same executed-slot counts whatever the host's call size,
    /// so a long call and the same slots in shorter calls differ only by the extra sweep where a call ends inside a
    /// slice. A call that starts on a boundary is one <see cref="RunUntilEdge"/>; one that starts inside a slice
    /// first finishes it. The boundaries depend only on the calls, never on what the firmware does. Uses
    /// <see cref="RunFlags"/>.
    /// </summary>
    public void Run(int n)
    {
        int quantum = Math.Max(1, Quantum);
        if (_runPhase >= quantum) _runPhase = 0;   // the quantum changed
        while (n > 0)
        {
            int k = _runPhase == 0 ? n : Math.Min(n, quantum - _runPhase);
            k = RunUntilEdge(new RunRequest { MaxSlots = k, Flags = RunFlags }).Slots;
            if (k <= 0) return;
            _runPhase = (int)((_runPhase + (long)k) % quantum);
            n -= k;
        }
    }

    private int _runPhase;

    /// <summary>What <see cref="Run"/> may use (<see cref="CoSimFlags.All"/>; only the speed-ups apply, nothing is
    /// watched). The native chip's switches set it.</summary>
    public CoSimFlags RunFlags { get; set; } = CoSimFlags.All;

    /// <summary>
    /// Run up to <see cref="RunRequest.MaxSlots"/> instructions per core and no further than
    /// <see cref="RunRequest.TargetCycle"/>, in slices of the guard whenever a pad is watched and the guard is nonzero
    /// (twice that while both cores sleep, with <see cref="CoSimFlags.AdaptiveSleep"/>), else of
    /// <see cref="Quantum"/>; a slice never exceeds the quantum. Peripherals tick at the end of every slice, before the
    /// pads are looked at. With <see cref="CoSimFlags.StopOnWatchedEdge"/> the run returns after the slice in which a
    /// watched pad changed. Pad changes go to <see cref="Pads"/>, with their own cycles.
    /// </summary>
    public RunResult RunUntilEdge(in RunRequest request)
    {
        var m = Machine;
        var pads = Pads;
        var flags = request.Flags;
        long max = Math.Max(0, request.MaxSlots), target = request.TargetCycle > 0 ? request.TargetCycle : long.MaxValue;
        int guard = Math.Max(0, request.Guard);
        bool armed = request.Watch != 0 && guard > 0;
        uint watched = armed ? (uint)(request.Watch & AllPads) : 0;
        pads.Watch = watched;
        pads.WatchedChanged = false;
        _stopOnEdge = armed && (flags & CoSimFlags.StopOnWatchedEdge) != 0;
        // The event-bounded guard: guard 1 already ends every slice after one instruction.
        bool events = armed && (flags & CoSimFlags.EventGuard) != 0 && guard > 1 && m.PinLevelBuses != PinLevelBus.None;
        bool hasTarget = target != long.MaxValue;
        bool stretches = (flags & CoSimFlags.SleepingStretches) != 0, bounded = (flags & CoSimFlags.BoundedRuns) != 0;
        bool adaptive = (flags & CoSimFlags.AdaptiveSleep) != 0;
        bool pollPwm = armed && !m.ExactPwmPadEdges;
        long done = 0, startCycle = m.Cpu.Cycles, cycles = 0;
        var reason = StopReason.Budget;
        if (hasTarget) m.CutLimit = target;
        try
        {
            int quantum = Math.Max(1, Quantum);
            while (done < max)
            {
                if (hasTarget)
                {
                    long now = m.Cpu.Cycles;
                    if (now >= target) { reason = StopReason.Target; break; }
                    // A watchdog reboot restarts core 0's counter: the run ends there rather than run it all again.
                    if (now < startCycle) { reason = StopReason.Reset; break; }
                    // Both cores stopped (locked up): time still passes, so burn the rest of the way.
                    if (m.Cpu.IsLockedUp && (!m.Core1Launched || m.Cpu1.IsLockedUp))
                    {
                        m.ChargeStopped(target - now);
                        reason = StopReason.Target;
                        break;
                    }
                }
                bool sleeping = Parked(0) && Parked(1);
                int slice = quantum;
                if (armed)
                {
                    slice = sleeping && adaptive ? (int)Math.Min(2L * guard, int.MaxValue) : guard;
                    slice = Math.Min(slice, quantum);
                }
                long left = max - done;
                int n = (int)Math.Min(slice, left);
                long whole = left / slice;   // full slices left in the call
                // A slice takes at least its slots in cycles: with the target less than two slices away there is
                // nothing to take at once, and the bookkeeping would cost more than the slice (fixed-step hosts).
                if (hasTarget && target - m.Cpu.Cycles < 2L * slice) whole = 0;
                if (whole >= 2 && m.DeferPeripheralTicks)
                {
                    if (sleeping && stretches && m.CanSkipSleepingSlices())
                    {
                        // A sleeping slice is exactly its slots in cycles: the target bounds how many fit.
                        if (hasTarget) whole = Math.Min(whole, (target - m.Cpu.Cycles) / slice);
                        // The first sweep with something due is the j-th slice from here; the ones before it only count.
                        long until = m.SweptCyclesUntilEvent();
                        long j = until == long.MaxValue ? long.MaxValue : Math.Max(1, (until + slice - 1) / slice);
                        long skip = Math.Min(j - 1, whole - 1);
                        if (skip > 0)
                        {
                            m.SkipSleepingSlices(skip, slice);
                            StretchedSlices += skip;
                            done += skip * slice;
                            continue;
                        }
                    }
                    else if (!Parked(0) && bounded && m.Core1Inert && BeforeSlice == null)
                    {
                        done += RunBounded(slice, (int)Math.Min(whole, int.MaxValue), events);
                        if (pollPwm) PollPwm(watched);
                        if (_stopOnEdge && pads.WatchedChanged) { reason = StopReason.WatchedEdge; break; }
                        continue;
                    }
                }
                PlainSlices++;
                done += RunSlice(n, events || hasTarget);
                if (!armed) continue;
                // PWM-muxed pads move with the counter, which IO_BANK0 does not report without exact PWM edges.
                if (pollPwm) PollPwm(watched);
                if (_stopOnEdge && pads.WatchedChanged) { reason = StopReason.WatchedEdge; break; }
            }
        }
        finally
        {
            pads.Watch = 0;
            _stopOnEdge = false;
            m.CutLimit = long.MaxValue;
            cycles = m.Cpu.Cycles - startCycle;
        }
        if (cycles < 0) cycles = m.LastElapsedCycles;   // the reboot's slice
        return new RunResult((int)Math.Min(done, int.MaxValue), cycles, reason);
    }

    private void PollPwm(uint watched)
    {
        uint pwm = Machine.IoBank0.PwmMuxedPins & watched;
        if (pwm != 0) Pads.Poll(pwm);
    }

    /// <summary>
    /// One machine slice of <paramref name="n"/> instructions per core, then the peripheral ticks (a cut slice ends
    /// earlier, see <see cref="RP2040Machine.RunCut"/>). Credits <see cref="Retired"/>.
    /// </summary>
    private int RunSlice(int n, bool cut)
    {
        bool p0 = Parked(0), p1 = Parked(1);
        BeforeSlice?.Invoke();
        int k = n;
        if (cut) k = Machine.RunCut(n, Pads.Watch);
        else Machine.Run(n);
        if (k < n) CutSlices++;
        if (!p0 && !Parked(0)) Retired[0] += (ulong)k;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)k;
        return k;
    }

    /// <summary>
    /// Up to <paramref name="maxSlices"/> slices of <paramref name="n"/> with core 0 awake and core 1 inert, in one
    /// bounded run: the slices whose ends change nothing are booked (core 0 credited <paramref name="n"/> retired
    /// instructions each, as <see cref="RunSlice"/> would), the last one is finished as a plain slice.
    /// </summary>
    /// <returns>The slots run.</returns>
    private long RunBounded(int n, int maxSlices, bool cut)
    {
        var m = Machine;
        bool p1 = Parked(1);
        int booked = m.RunAwakeBounded(n, maxSlices, cut ? Pads.Watch : 0);
        int k = m.BoundedSliceSlots;
        BoundedSlices += booked;
        PlainSlices++;
        if (k < n) CutSlices++;
        Retired[0] += (ulong)booked * (ulong)n;
        // The last slice: core 0's part ran in the bounded loop; core 1's turn and the sweep run as in a plain slice.
        m.FinishSlice(k, m.BoundedSliceDelta);
        if (!Parked(0)) Retired[0] += (ulong)k;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)k;
        return (long)booked * n + k;
    }

    /// <summary>Core-0 cycles until the next edge a pin-level engine makes on a pad in <paramref name="watch"/>
    /// (<see cref="RP2040Machine.CyclesUntilPadEdge"/>), long.MaxValue when none has one coming.</summary>
    public long CyclesUntilPadEdge(ulong watch) => Machine.CyclesUntilPadEdge((uint)(watch & AllPads));

    /// <summary>
    /// Drive the pads in <paramref name="mask"/> from outside to <paramref name="levels"/>. Pads already held at their
    /// level are left alone (a circuit solver re-applies its inputs many times per step); for the rest the deferred
    /// peripherals are brought up first (<see cref="RP2040Machine.SyncForHostInput"/>), so the cycles already run see
    /// the old level.
    /// </summary>
    public void SetInputs(ulong mask, ulong levels)
    {
        var io = Machine.IoBank0;
        var bits = (uint)(mask & AllPads);
        bits &= ~io.ExternalHeldMask | (io.ExternalLevels ^ (uint)levels);
        if (bits == 0) return;
        Machine.SyncForHostInput();
        for (var rest = bits; rest != 0; rest &= rest - 1)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(rest);
            io.SetExternalInput(pin, ((levels >> pin) & 1) != 0);
        }
    }

    /// <summary>The pads driven from outside and their levels (meaningful only where driven).</summary>
    public (ulong Mask, ulong Levels) Inputs => (Machine.IoBank0.ExternalHeldMask, Machine.IoBank0.ExternalLevels);

    /// <summary>Stop driving the pads in <paramref name="mask"/> (they read their pulls); pads not driven are left alone.</summary>
    public void ReleaseInputs(ulong mask)
    {
        var io = Machine.IoBank0;
        var bits = (uint)(mask & AllPads) & io.ExternalHeldMask;
        if (bits == 0) return;
        Machine.SyncForHostInput();
        io.ReleaseExternalInput(bits);
    }

    /// <summary>Bring every deferred peripheral up to the end of the last slice (before a host reads state).</summary>
    public void SyncToNow() => Machine.SyncForHostRead();
}

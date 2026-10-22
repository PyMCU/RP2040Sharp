using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu;

/// <summary>
/// Decides, at the end of each slice of a bounded run (<see cref="CortexM0Plus.RunGated"/>), whether the run goes on
/// into the next slice. Called once per slice, never per instruction.
/// </summary>
public interface ISliceGate
{
    /// <summary>Core <paramref name="cpu"/> used up the slice in flight; true books its end and starts the next one.</summary>
    bool NextSlice(CortexM0Plus cpu);
}

/// <summary>
/// What <see cref="CortexM0Plus"/>'s run loop does when the instruction budget it was given runs out. A type argument
/// only: <see cref="NoRunLimit"/> folds away, so the plain <see cref="CortexM0Plus.Run"/> loop is unchanged.
/// </summary>
internal interface IRunLimit
{
    /// <summary>Called when the budget is spent; true continues with <paramref name="instructions"/> refilled.</summary>
    static abstract bool Refill(CortexM0Plus cpu, ref int instructions);

    /// <summary>Whether the run stops early at <see cref="CortexM0Plus.CutCycle"/> / <see cref="CortexM0Plus.CutRequest"/>
    /// (a constant per limit, so the check folds away where it is false).</summary>
    static abstract bool HasCut { get; }
}

/// <summary>The plain run: the budget is the whole run.</summary>
internal struct NoRunLimit : IRunLimit
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Refill(CortexM0Plus cpu, ref int instructions) => false;

    public static bool HasCut => false;
}

/// <summary>A run of slices of <see cref="CortexM0Plus.GateSlice"/> instructions; the gate decides at each slice end.</summary>
internal struct SliceRunLimit : IRunLimit
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Refill(CortexM0Plus cpu, ref int instructions)
    {
        if (!cpu.Gate!.NextSlice(cpu)) return false;
        instructions = cpu.GateSlice - 1;   // this slot is the first of the new slice
        return true;
    }

    public static bool HasCut => false;
}

/// <summary>A plain run that also stops at the cut cycle or on a cut request (<see cref="CortexM0Plus.RunCut"/>).</summary>
internal struct CutRunLimit : IRunLimit
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Refill(CortexM0Plus cpu, ref int instructions) => false;

    public static bool HasCut => true;
}

/// <summary>A bounded run (<see cref="SliceRunLimit"/>) that also stops at the cut (<see cref="CortexM0Plus.RunGatedCut"/>).</summary>
internal struct SliceCutRunLimit : IRunLimit
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Refill(CortexM0Plus cpu, ref int instructions) => SliceRunLimit.Refill(cpu, ref instructions);

    public static bool HasCut => true;
}

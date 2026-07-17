namespace RP2040.Core.Cpu;

/// <summary>
/// Observation hook invoked once per executed instruction on the profiling-only
/// execution path (<see cref="CortexM0Plus.RunProfiled"/>).
///
/// This is deliberately a separate path from the hot <see cref="CortexM0Plus.Run"/>
/// loop: the normal simulation never references an observer, so per-instruction
/// profiling overhead cannot leak into the fast path. (Same separation AVR8Sharp
/// uses between its struct LUT decoders and its <c>ProfilingDecoder</c>.)
///
/// The callback fires <b>before</b> the instruction is dispatched, so the observer
/// sees the architectural state (PC, registers, memory) as it was on entry to the
/// instruction at <paramref name="pc"/>.
/// </summary>
public interface IProfilingObserver
{
    /// <param name="pc">Program counter of the instruction about to execute (Thumb bit stripped).</param>
    /// <param name="opcode">The 16-bit halfword fetched at <paramref name="pc"/>.</param>
    /// <param name="cycles">Cycle counter immediately before this instruction executes.</param>
    void OnInstruction(uint pc, ushort opcode, long cycles);
}

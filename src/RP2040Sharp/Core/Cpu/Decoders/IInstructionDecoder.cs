using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Decoders;

/// <summary>
/// A struct decoder that turns one 16-bit Thumb opcode into a handler call. Implemented as a struct so
/// that <see cref="CortexM0Plus"/>'s generic execution loop monomorphizes and inlines <see cref="Dispatch16"/> —
/// no virtual call, no boxing. Two implementations trade off dispatch mechanism (native function-pointer
/// table vs switch/br_table over a handler id) so the fastest can be picked per runtime target
/// (native vs WASM).
/// </summary>
public interface IInstructionDecoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Dispatch16(ushort opcode, CortexM0Plus cpu);
}

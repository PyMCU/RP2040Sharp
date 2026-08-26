using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Decoders;

/// <summary>
/// Dispatches via a managed-delegate table (65536 <see cref="OpcodeAction"/> entries). Fully portable —
/// no unsafe, no function pointers — so it is a candidate "todoterreno" for runtimes (e.g. WASM) where a
/// managed delegate call may lower better than a raw <c>call_indirect</c> through native memory.
/// </summary>
public readonly struct LutDecoder : IInstructionDecoder
{
    private readonly OpcodeAction[] _table;

    public LutDecoder(OpcodeAction[] table) => _table = table;

    public static LutDecoder Create() => new(ThumbDecodeTables.ManagedTable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispatch16(ushort opcode, CortexM0Plus cpu)
    {
        _table[opcode](opcode, cpu);
    }
}

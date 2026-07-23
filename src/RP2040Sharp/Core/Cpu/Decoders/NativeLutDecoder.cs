using System.Runtime.CompilerServices;
using unsafe InstructionHandler = delegate* managed<ushort, RP2040.Core.Cpu.CortexM0Plus, void>;

namespace RP2040.Core.Cpu.Decoders;

/// <summary>
/// Dispatches via the native <c>delegate*</c> lookup table (one indirect call per instruction). This is
/// the classic RP2040Sharp mechanism and the default on the native runtime, where the JIT lowers the
/// indirect call cheaply. Wraps the table owned by <see cref="InstructionDecoder"/>.
/// </summary>
public readonly unsafe struct NativeLutDecoder : IInstructionDecoder
{
    private readonly InstructionHandler* _table;

    public NativeLutDecoder(InstructionHandler* table) => _table = table;

    public static NativeLutDecoder Create() => new(InstructionDecoder.Instance.Table);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispatch16(ushort opcode, CortexM0Plus cpu)
    {
        _table[opcode](opcode, cpu);
    }
}

using System.Numerics;
using System.Runtime.CompilerServices;
using RP2040.Core.Memory;

namespace RP2040.Core.Cpu.Instructions;

public static unsafe class MemoryOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F) << 2;

        cpu.Registers[rt] = ReadWordWithCycles(cpu, cpu.Registers[rn] + imm5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrLiteral(ushort opcode, CortexM0Plus cpu)
    {
        var rt = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF) << 2;
        var nextPc = cpu.Registers.PC + 2;
        var addr = (nextPc & 0xFFFFFFFC) + imm8;
        cpu.Registers[rt] = ReadWordWithCycles(cpu, addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        cpu.Registers[rt] = ReadWordWithCycles(cpu, cpu.Registers[rn] + cpu.Registers[rm]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrSpRelative(ushort opcode, CortexM0Plus cpu)
    {
        var rt = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF) << 2;
        cpu.Registers[rt] = ReadWordWithCycles(cpu, cpu.Registers.SP + imm8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pop(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);

        var sp = cpu.Registers.SP;
        var finalSp = sp + (regCount * 4);

        if ((sp >> 28) == BusInterconnect.REGION_SRAM)
        {
            var rawPtr = cpu.Bus.PtrSram + (sp & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = Unsafe.ReadUnaligned<uint>(rawPtr);

                rawPtr += 4;
                mask &= (mask - 1);
            }
        }
        else
        {
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = cpu.Bus.ReadWord(sp);
                sp += 4;
                mask &= (mask - 1);
            }
        }

        cpu.Registers.SP = finalSp;
        // rp2040js POP: base 1 (loop) + 1 per loaded register.
        cpu.Cycles += regCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PopPc(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);

        var sp = cpu.Registers.SP;
        var finalSp = sp + ((regCount + 1) * 4);

        uint newPc;
        if ((sp >> 28) == BusInterconnect.REGION_SRAM)
        {
            var rawPtr = cpu.Bus.PtrSram + (sp & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = Unsafe.ReadUnaligned<uint>(rawPtr);
                rawPtr += 4;
                mask &= (mask - 1);
            }
            newPc = Unsafe.ReadUnaligned<uint>(rawPtr);
        }
        else
        {
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = cpu.Bus.ReadWord(sp);
                sp += 4;
                mask &= (mask - 1);
            }
            newPc = cpu.Bus.ReadWord(sp);
        }

        // SP must reflect the post-pop value before ExceptionReturn unstacks the
        // architectural frame, otherwise the frame is read starting at the
        // EXC_RETURN word itself (corrupting R0..xPSR).
        cpu.Registers.SP = finalSp;

        // rp2040js POP with PC: base 1 (loop) + 1 per loaded register + 2 for the PC write.
        // Charged before the branch so an EXC_RETURN unstack (which adds 0 under the accurate
        // model) counts the same as a plain PC load.
        cpu.Cycles += 2 + regCount;

        if (newPc >= 0xFFFFFFF0)
            cpu.ExceptionReturn(newPc);
        else
            cpu.Registers.PC = newPc & 0xFFFFFFFE;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);
        var totalBytes = regCount * 4;

        var oldSp = cpu.Registers.SP;
        var newSp = oldSp - totalBytes;

        if ((newSp >> 28) == BusInterconnect.REGION_SRAM)
        {
            var rawPtr = cpu.Bus.PtrSram + (newSp & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);

                Unsafe.WriteUnaligned<uint>(rawPtr, cpu.Registers[regIdx]);

                rawPtr += 4;
                mask &= (mask - 1);
            }
        }
        else
        {
            var writePtr = newSp;
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                var val = cpu.Registers[regIdx];
                cpu.Bus.WriteWord(writePtr, val);

                writePtr += 4;
                mask &= (mask - 1);
            }
        }

        cpu.Registers.SP = newSp;
        // rp2040js PUSH: base 1 (loop) + 1 per stored register.
        cpu.Cycles += regCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushLr(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);
        var totalBytes = (regCount + 1) * 4; // +1 because of LR

        var oldSp = cpu.Registers.SP;
        var newSp = oldSp - totalBytes;

        if ((newSp >> 28) == BusInterconnect.REGION_SRAM)
        {
            var rawPtr = cpu.Bus.PtrSram + (newSp & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                Unsafe.WriteUnaligned<uint>(rawPtr, cpu.Registers[regIdx]);
                rawPtr += 4;
                mask &= (mask - 1);
            }
            Unsafe.WriteUnaligned<uint>(rawPtr, cpu.Registers.LR);
        }
        else
        {
            var writePtr = newSp;
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Bus.WriteWord(writePtr, cpu.Registers[regIdx]);
                writePtr += 4;
                mask &= (mask - 1);
            }
            cpu.Bus.WriteWord(writePtr, cpu.Registers.LR);
        }

        cpu.Registers.SP = newSp;
        // rp2040js PUSH {..., lr}: base 1 (loop) + 1 per low register only — the LR store adds no
        // cycle (see cortex-m0-core.ts: the bit-8 writeUint32 is outside the deltaCycles++ loop).
        cpu.Cycles += regCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldmia(ushort opcode, CortexM0Plus cpu)
    {
        var rn = (opcode >> 8) & 0x7;
        var mask = (uint)(opcode & 0xFF);

        var regCount = (uint)BitOperations.PopCount(mask);
        var baseAddr = cpu.Registers[rn];

        var isRnInList = (mask >> rn) & 1;
        var writeBackOffset = (regCount * 4) * (isRnInList ^ 1);

        if ((baseAddr >> 28) == BusInterconnect.REGION_SRAM)
        {
            var ptr = cpu.Bus.PtrSram + (baseAddr & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = Unsafe.ReadUnaligned<uint>(ptr);

                ptr += 4;
                mask &= (mask - 1);
            }
        }
        else // SLOW PATH
        {
            var readPtr = baseAddr;
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Registers[regIdx] = cpu.Bus.ReadWord(readPtr);

                readPtr += 4;
                mask &= (mask - 1);
            }
        }

        cpu.Registers[rn] += writeBackOffset;
        // rp2040js LDMIA: base 1 (loop) + 1 per loaded register.
        cpu.Cycles += regCount;
    }

    // ================================================================
    // STR variants (Store Word)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F) << 2;
        var address = cpu.Registers[rn] + imm5;
        WriteWordWithCycles(cpu, address, cpu.Registers[rt]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrSpRelative(ushort opcode, CortexM0Plus cpu)
    {
        var rt = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF) << 2;
        var address = cpu.Registers.SP + imm8;
        WriteWordWithCycles(cpu, address, cpu.Registers[rt]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        var address = cpu.Registers[rn] + cpu.Registers[rm];
        WriteWordWithCycles(cpu, address, cpu.Registers[rt]);
    }

    // ================================================================
    // STRB variants (Store Byte)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrbImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F);
        var address = cpu.Registers[rn] + imm5;
        WriteByteWithCycles(cpu, address, (byte)cpu.Registers[rt]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrbRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        var address = cpu.Registers[rn] + cpu.Registers[rm];
        WriteByteWithCycles(cpu, address, (byte)cpu.Registers[rt]);
    }

    // ================================================================
    // STRH variants (Store Halfword)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrhImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F) << 1;
        var address = cpu.Registers[rn] + imm5;
        WriteHalfWordWithCycles(cpu, address, (ushort)cpu.Registers[rt]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrhRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        var address = cpu.Registers[rn] + cpu.Registers[rm];
        WriteHalfWordWithCycles(cpu, address, (ushort)cpu.Registers[rt]);
    }

    // ================================================================
    // LDRB variants (Load Byte, zero-extend)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrbImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F);
        cpu.Registers[rt] = ReadByteWithCycles(cpu, cpu.Registers[rn] + imm5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrbRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        cpu.Registers[rt] = ReadByteWithCycles(cpu, cpu.Registers[rn] + cpu.Registers[rm]);
    }

    // ================================================================
    // LDRH variants (Load Halfword, zero-extend)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrhImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F) << 1;
        cpu.Registers[rt] = ReadHalfWordWithCycles(cpu, cpu.Registers[rn] + imm5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrhRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        cpu.Registers[rt] = ReadHalfWordWithCycles(cpu, cpu.Registers[rn] + cpu.Registers[rm]);
    }

    // ================================================================
    // LDRSB / LDRSH (Load Signed Byte/Halfword, sign-extend to 32 bits)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldrsb(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        cpu.Registers[rt] = (uint)(sbyte)ReadByteWithCycles(cpu, cpu.Registers[rn] + cpu.Registers[rm]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldrsh(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        cpu.Registers[rt] = (uint)(short)ReadHalfWordWithCycles(cpu, cpu.Registers[rn] + cpu.Registers[rm]);
    }

    // ================================================================
    // STMIA (Store Multiple Increment After)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stmia(ushort opcode, CortexM0Plus cpu)
    {
        var rn = (opcode >> 8) & 0x7;
        var mask = (uint)(opcode & 0xFF);

        var regCount = (uint)BitOperations.PopCount(mask);
        var baseAddr = cpu.Registers[rn];

        if ((baseAddr >> 28) == BusInterconnect.REGION_SRAM)
        {
            var ptr = cpu.Bus.PtrSram + (baseAddr & BusInterconnect.MASK_SRAM);

            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                Unsafe.WriteUnaligned<uint>(ptr, cpu.Registers[regIdx]);

                ptr += 4;
                mask &= (mask - 1);
            }
        }
        else
        {
            var writePtr = baseAddr;
            while (mask != 0)
            {
                var regIdx = BitOperations.TrailingZeroCount(mask);
                cpu.Bus.WriteWord(writePtr, cpu.Registers[regIdx]);

                writePtr += 4;
                mask &= (mask - 1);
            }
        }

        // STMIA always writes back (unlike LDMIA which skips if Rn is in list)
        cpu.Registers[rn] = baseAddr + (regCount * 4);
        // rp2040js STMIA: base 1 (loop) + 1 per stored register.
        cpu.Cycles += regCount;
    }

    // ================================================================
    // Private helpers
    // ================================================================

    // Single-register load/store wait-states: the address-dependent penalty of rp2040js cyclesIO()
    // is added on top of the loop's base cycle, straight-line where the address is already known.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadWordWithCycles(CortexM0Plus cpu, uint address)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        return cpu.Bus.ReadWord(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadByteWithCycles(CortexM0Plus cpu, uint address)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        return cpu.Bus.ReadByte(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadHalfWordWithCycles(CortexM0Plus cpu, uint address)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        return cpu.Bus.ReadHalfWord(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWordWithCycles(CortexM0Plus cpu, uint address, uint value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        cpu.Bus.WriteWord(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteByteWithCycles(CortexM0Plus cpu, uint address, byte value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        cpu.Bus.WriteByte(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHalfWordWithCycles(CortexM0Plus cpu, uint address, ushort value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        cpu.Bus.WriteHalfWord(address, value);
    }
}

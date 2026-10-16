using System.Numerics;
using System.Runtime.CompilerServices;
using RP2040.Core.Memory;

namespace RP2040.Core.Cpu.Instructions;

public static unsafe class MemoryOps
{
    /// <summary>True when a <paramref name="bytes"/>-long burst starting at <paramref name="addr"/> lies wholly inside
    /// SRAM (<paramref name="offset"/> is then its offset into the SRAM array). The multi-register fast paths
    /// advance a raw pointer from the start address, so a burst that runs off the end of SRAM has to take the
    /// per-word path, where the first unmapped word faults.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SramBurst(uint addr, uint bytes, out uint offset)
    {
        offset = addr - BusInterconnect.SRAM_START_ADDRESS;
        return offset <= BusInterconnect.SRAM_SIZE - bytes;
    }

    /// <summary>
    /// A load or store that cannot complete takes a precise HardFault (ARMv6-M B1.5.14): an unaligned address
    /// (A3.2: ARMv6-M always faults, CCR.UNALIGN_TRP is fixed at 1) or one the bus cannot decode (datasheet 2.2: a
    /// bus error). The stacked return address is the instruction itself; the destination register and any base
    /// writeback are left untouched because the caller returns without applying them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fault(CortexM0Plus cpu) => cpu.TriggerHardFaultAt(cpu.Registers.PC - 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm5 = (uint)((opcode >> 6) & 0x1F) << 2;

        if (TryReadWord(cpu, cpu.Registers[rn] + imm5, out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrLiteral(ushort opcode, CortexM0Plus cpu)
    {
        var rt = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF) << 2;
        var nextPc = cpu.Registers.PC + 2;
        var addr = (nextPc & 0xFFFFFFFC) + imm8;
        if (TryReadWord(cpu, addr, out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        if (TryReadWord(cpu, cpu.Registers[rn] + cpu.Registers[rm], out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrSpRelative(ushort opcode, CortexM0Plus cpu)
    {
        var rt = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF) << 2;
        if (TryReadWord(cpu, cpu.Registers.SP + imm8, out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pop(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);

        var sp = cpu.Registers.SP;
        var finalSp = sp + (regCount * 4);

        if ((sp & 3) != 0) { Fault(cpu); return; }

        if (SramBurst(sp, regCount * 4, out var sramOffset))
        {
            var rawPtr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryReadWord(sp, out var word)) { Fault(cpu); return; }
                cpu.Registers[regIdx] = word;
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

        if ((sp & 3) != 0) { Fault(cpu); return; }

        uint newPc;
        if (SramBurst(sp, (regCount + 1) * 4, out var sramOffset))
        {
            var rawPtr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryReadWord(sp, out var word)) { Fault(cpu); return; }
                cpu.Registers[regIdx] = word;
                sp += 4;
                mask &= (mask - 1);
            }
            if (!cpu.Bus.TryReadWord(sp, out newPc)) { Fault(cpu); return; }
        }

        // SP must reflect the post-pop value before ExceptionReturn unstacks the
        // architectural frame, otherwise the frame is read starting at the
        // EXC_RETURN word itself (corrupting R0..xPSR).
        cpu.Registers.SP = finalSp;

        // rp2040js POP with PC: base 1 (loop) + 1 per loaded register + 2 for the PC write.
        // Charged before the branch so an EXC_RETURN unstack (which adds 0 under the accurate
        // model) counts the same as a plain PC load.
        cpu.Cycles += 2 + regCount;

        // LoadWritePC is BXWritePC: EXC_RETURN in Handler mode, T == 0 faults.
        if ((newPc & 1) != 0 && newPc < 0xFFFFFFF0)
            cpu.Registers.PC = newPc & 0xFFFFFFFE;
        else
            cpu.BxWritePc(newPc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push(ushort opcode, CortexM0Plus cpu)
    {
        var mask = (uint)(opcode & 0xFF);
        var regCount = (uint)BitOperations.PopCount(mask);
        var totalBytes = regCount * 4;

        var oldSp = cpu.Registers.SP;
        var newSp = oldSp - totalBytes;

        if ((newSp & 3) != 0) { Fault(cpu); return; }

        if (SramBurst(newSp, totalBytes, out var sramOffset))
        {
            var rawPtr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryWriteWord(writePtr, val)) { Fault(cpu); return; }

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

        if ((newSp & 3) != 0) { Fault(cpu); return; }

        if (SramBurst(newSp, totalBytes, out var sramOffset))
        {
            var rawPtr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryWriteWord(writePtr, cpu.Registers[regIdx])) { Fault(cpu); return; }
                writePtr += 4;
                mask &= (mask - 1);
            }
            if (!cpu.Bus.TryWriteWord(writePtr, cpu.Registers.LR)) { Fault(cpu); return; }
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

        if ((baseAddr & 3) != 0) { Fault(cpu); return; }

        var isRnInList = (mask >> rn) & 1;
        var writeBackOffset = (regCount * 4) * (isRnInList ^ 1);

        if (SramBurst(baseAddr, regCount * 4, out var sramOffset))
        {
            var ptr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryReadWord(readPtr, out var word)) { Fault(cpu); return; }
                cpu.Registers[regIdx] = word;

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
        if (TryReadByte(cpu, cpu.Registers[rn] + imm5, out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrbRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        if (TryReadByte(cpu, cpu.Registers[rn] + cpu.Registers[rm], out var value)) cpu.Registers[rt] = value;
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
        if (TryReadHalfWord(cpu, cpu.Registers[rn] + imm5, out var value)) cpu.Registers[rt] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrhRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        if (TryReadHalfWord(cpu, cpu.Registers[rn] + cpu.Registers[rm], out var value)) cpu.Registers[rt] = value;
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
        if (TryReadByte(cpu, cpu.Registers[rn] + cpu.Registers[rm], out var value)) cpu.Registers[rt] = (uint)(sbyte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldrsh(ushort opcode, CortexM0Plus cpu)
    {
        var rt = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;
        if (TryReadHalfWord(cpu, cpu.Registers[rn] + cpu.Registers[rm], out var value)) cpu.Registers[rt] = (uint)(short)value;
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

        if ((baseAddr & 3) != 0) { Fault(cpu); return; }

        if (SramBurst(baseAddr, regCount * 4, out var sramOffset))
        {
            var ptr = cpu.Bus.PtrSram + sramOffset;

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
                if (!cpu.Bus.TryWriteWord(writePtr, cpu.Registers[regIdx])) { Fault(cpu); return; }

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
    // The Try forms also take the precise HardFault of Fault() when the access cannot complete, and then
    // return false so the caller leaves its destination register alone.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadWord(CortexM0Plus cpu, uint address, out uint value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        if ((address & 3) == 0 && cpu.Bus.TryReadWord(address, out value)) return true;
        value = 0;
        Fault(cpu);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadByte(CortexM0Plus cpu, uint address, out uint value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        if (cpu.Bus.TryReadByte(address, out var b)) { value = b; return true; }
        value = 0;
        Fault(cpu);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadHalfWord(CortexM0Plus cpu, uint address, out uint value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, false);
        if ((address & 1) == 0 && cpu.Bus.TryReadHalfWord(address, out var h)) { value = h; return true; }
        value = 0;
        Fault(cpu);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWordWithCycles(CortexM0Plus cpu, uint address, uint value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        if ((address & 3) != 0 || !cpu.Bus.TryWriteWord(address, value)) Fault(cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteByteWithCycles(CortexM0Plus cpu, uint address, byte value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        if (!cpu.Bus.TryWriteByte(address, value)) Fault(cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHalfWordWithCycles(CortexM0Plus cpu, uint address, ushort value)
    {
        cpu.Cycles += CortexM0Plus.CyclesIO(address, true);
        if ((address & 1) != 0 || !cpu.Bus.TryWriteHalfWord(address, value)) Fault(cpu);
    }
}

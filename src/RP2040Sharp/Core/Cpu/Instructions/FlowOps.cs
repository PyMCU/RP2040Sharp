using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Instructions;

public static class FlowOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bl(ushort opcodeH1, CortexM0Plus cpu)
    {
        ref var pc = ref cpu.Registers.PC;
        var opcodeH2 = cpu.Bus.ReadHalfWord(pc);

        pc += 2;
        var nextPc = pc;

        var s = (opcodeH1 >> 10) & 1;
        var j1 = (opcodeH2 >> 13) & 1;
        var j2 = (opcodeH2 >> 11) & 1;

        var imm10 = opcodeH1 & 0x3FF;
        var imm11 = opcodeH2 & 0x7FF;

        var i1 = ~(j1 ^ s) & 1;
        var i2 = ~(j2 ^ s) & 1;

        var offset = (s << 24) | (i1 << 23) | (i2 << 22) | (imm10 << 12) | (imm11 << 1);

        offset = (offset << 7) >> 7;

        cpu.Registers.LR = nextPc | 1;
        pc = (uint)(nextPc + offset);

        // rp2040js BL: base 1 (loop) + 2 (total 3 cycles).
        cpu.Cycles += 2;
        // BLTaken Action is Missing
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Blx(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;
        ref var pc = ref cpu.Registers.PC;

        cpu.Registers.LR = pc | 0x1;

        var targetAddress = cpu.Registers[rm];
        pc = targetAddress & 0xFFFFFFFE;

        // rp2040js BLX: base 1 (loop) + 1.
        cpu.Cycles++;
        // BLTaken Action is Missing
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Branch(ushort opcode, CortexM0Plus cpu)
    {
        var offset = (opcode << 21) >> 20;

        ref var pc = ref cpu.Registers.PC;

        pc += (uint)(offset + 2);

        // rp2040js B (unconditional): base 1 (loop) + 1.
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Beq(ushort opcode, CortexM0Plus cpu) // Z == 1
    {
        if (cpu.Registers.Z)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bne(ushort opcode, CortexM0Plus cpu) // Z == 0
    {
        if (!cpu.Registers.Z)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bcs(ushort opcode, CortexM0Plus cpu) // C == 1
    {
        if (cpu.Registers.C)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bcc(ushort opcode, CortexM0Plus cpu) // C == 0
    {
        if (!cpu.Registers.C)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bmi(ushort opcode, CortexM0Plus cpu) // N == 1
    {
        if (cpu.Registers.N)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bpl(ushort opcode, CortexM0Plus cpu) // N == 0
    {
        if (!cpu.Registers.N)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bvs(ushort opcode, CortexM0Plus cpu) // V == 1
    {
        if (cpu.Registers.V)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bvc(ushort opcode, CortexM0Plus cpu) // V == 0
    {
        if (!cpu.Registers.V)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bhi(ushort opcode, CortexM0Plus cpu) // C==1 && Z==0
    {
        if (cpu.Registers.C && !cpu.Registers.Z)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bls(ushort opcode, CortexM0Plus cpu) // C==0 || Z==1
    {
        if (!cpu.Registers.C || cpu.Registers.Z)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bge(ushort opcode, CortexM0Plus cpu) // N == V
    {
        if (cpu.Registers.N == cpu.Registers.V)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Blt(ushort opcode, CortexM0Plus cpu) // N != V
    {
        if (cpu.Registers.N != cpu.Registers.V)
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bgt(ushort opcode, CortexM0Plus cpu) // Z==0 && N==V
    {
        if (!cpu.Registers.Z && (cpu.Registers.N == cpu.Registers.V))
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ble(ushort opcode, CortexM0Plus cpu) // Z==1 || N!=V
    {
        if (cpu.Registers.Z || (cpu.Registers.N != cpu.Registers.V))
            TakeBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bx(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xf;
        var target = cpu.Registers[rm];
        // rp2040js BX: base 1 (loop) + 1, charged whether the target is a normal address or an
        // EXC_RETURN (BXWritePC → exceptionReturn adds 0 under the accurate model).
        cpu.Cycles++;
        if (target >= 0xFFFFFFF0)
        {
            cpu.ExceptionReturn(target);
            return;
        }
        cpu.Registers.PC = target & 0xFFFFFFFE;
    }

    // ================================================================
    // CBZ / CBNZ  (Compare and Branch if Zero/Non-Zero)
    // mask=0xF500, CBZ=0xB100, CBNZ=0xB900
    // Encoding: bit11=0(CBZ)/1(CBNZ), bit9=imm5[5], bits[7:3]=imm5[4:0]
    // offset = imm5:0 (zero-extended, already bit1=0 so effective *2)
    // Branch target = PC_after_fetch + offset  (PC was already +2 at dispatch)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cbz(ushort opcode, CortexM0Plus cpu)
    {
        if (cpu.Registers[opcode & 0x7] == 0)
            TakeCbBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cbnz(ushort opcode, CortexM0Plus cpu)
    {
        if (cpu.Registers[opcode & 0x7] != 0)
            TakeCbBranch(opcode, cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TakeCbBranch(ushort opcode, CortexM0Plus cpu)
    {
        // imm32 = ZeroExtend(i:imm5:0, 32)
        // i = bit[10], imm5 = bits[7:3]
        // combined: (imm5 | (i << 5)) << 1
        var imm32 = (uint)((((opcode >> 3) & 0x1F) | ((opcode >> 5) & 0x20)) << 1);
        // PC was already advanced by 2 (speculative fetch); add imm32 + 2 more
        cpu.Registers.PC += imm32 + 2;
        // Taken branch: base 1 (loop) + 1 (rp2040js has no CBZ/CBNZ on ARMv6-M, but a taken branch
        // costs the same +1 there).
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TakeBranch(ushort opcode, CortexM0Plus cpu)
    {
        var offset = (sbyte)(opcode & 0xFF) << 1;

        ref var pc = ref cpu.Registers.PC;
        pc += (uint)(offset + 2);

        // rp2040js B<cond> taken: base 1 (loop) + 1 (added only when the branch is taken).
        cpu.Cycles++;
    }
}

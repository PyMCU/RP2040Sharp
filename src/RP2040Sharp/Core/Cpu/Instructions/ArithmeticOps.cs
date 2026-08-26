using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Instructions;

public static class ArithmeticOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddsImmediate3(ushort opcode, CortexM0Plus cpu)
    {
        // ADDS Rd, Rn, #imm3
        var rd = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm3 = (uint)((opcode >> 6) & 0x7);

        ref var ptrRd = ref cpu.Registers[rd];
        var valRn = cpu.Registers[rn];

        ptrRd = AddWithFlags(cpu, valRn, imm3, carryIn: 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddsImmediate8(ushort opcode, CortexM0Plus cpu)
    {
        // ADDS Rd, #imm8 (Rd es Source y Destino)
        var rd = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF);

        ref var ptrRd = ref cpu.Registers[rd];

        ptrRd = AddWithFlags(cpu, ptrRd, imm8, carryIn: 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddsRegister(ushort opcode, CortexM0Plus cpu)
    {
        // ADDS Rd, Rn, Rm
        var rd = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rm = (opcode >> 6) & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRn = cpu.Registers[rn];
        var valRm = cpu.Registers[rm];

        ptrRd = AddWithFlags(cpu, valRn, valRm, carryIn: 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Adcs(ushort opcode, CortexM0Plus cpu)
    {
        // ADCS Rd, Rm (Rd es Source y Destino)
        var rd = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        var carryIn = cpu.Registers.GetC();

        ptrRd = AddWithFlags(cpu, ptrRd, valRm, carryIn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSpImmediate7(ushort opcode, CortexM0Plus cpu)
    {
        // ADD SP, SP, #imm7
        var imm7 = (uint)((opcode & 0x7F) << 2);
        cpu.Registers.SP += imm7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSpImmediate8(ushort opcode, CortexM0Plus cpu)
    {
        // ADD Rd, SP, #imm8
        var rd = (opcode >> 8) & 0x7;
        var imm8 = (uint)((opcode & 0xFF) << 2);

        cpu.Registers[rd] = cpu.Registers.SP + imm8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddHighToPc(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;
        var valRm = cpu.Registers[rm];

        ref var pc = ref cpu.Registers.PC;

        var valPcSource = pc + 2;
        var result = (valPcSource + valRm) & 0xFFFFFFFE;

        pc = result;
        // rp2040js ADD (register) with Rdn == PC: base 1 (loop) + 1.
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddHighToSp(ushort opcode, CortexM0Plus cpu)
    {
        // ARMv6-M §A6.7.2: ADD (SP plus register) — writes the raw sum to SP.
        // The architecture does NOT mandate alignment for this encoding; only
        // explicit stack operations (MSR SP, PUSH, POP) must be word-aligned.
        var rm = (opcode >> 3) & 0xF;
        var valRm = cpu.Registers[rm];

        cpu.Registers.SP += valRm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddHighToReg(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;
        var dn = ((opcode >> 4) & 0x8) | (opcode & 0x7);

        ref var ptrDn = ref cpu.Registers[dn];

        var valRm = cpu.Registers[rm];

        ptrDn += valRm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Adr(ushort opcode, CortexM0Plus cpu)
    {
        // ADR Rd, label
        var rd = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF);

        var basePc = (cpu.Registers.PC + 2) & 0xFFFFFFFC;

        cpu.Registers[rd] = basePc + (imm8 << 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cmn(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rn = opcode & 0x7;

        var valRm = cpu.Registers[rm];
        var valRn = cpu.Registers[rn];

        AddWithFlags(cpu, valRn, valRm, carryIn: 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CmpImmediate(ushort opcode, CortexM0Plus cpu)
    {
        var rn = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF);

        var val1 = cpu.Registers[rn];

        SubWithFlags(cpu, val1, imm8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CmpRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rn = opcode & 0x7;

        var val1 = cpu.Registers[rn];
        var val2 = cpu.Registers[rm];

        SubWithFlags(cpu, val1, val2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CmpHighRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;
        var rn = ((opcode >> 4) & 0x8) | (opcode & 0x7);

        var valRn = cpu.Registers[rn];
        var valRm = cpu.Registers[rm];

        valRn += (uint)((rn + 1) >> 4) << 1;
        valRm += (uint)((rm + 1) >> 4) << 1;

        SubWithFlags(cpu, valRn, valRm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Muls(ushort opcode, CortexM0Plus cpu)
    {
        var rn = (opcode >> 3) & 0x7;
        var rdm = opcode & 0x7;

        ref var ptrRdm = ref cpu.Registers[rdm];
        var valRn = cpu.Registers[rn];

        ptrRdm *= valRn;

        cpu.Registers.N = (int)ptrRdm < 0;
        cpu.Registers.Z = (ptrRdm == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Rsbs(ushort opcode, CortexM0Plus cpu)
    {
        var rn = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRn = cpu.Registers[rn];

        ptrRd = SubWithFlags(cpu, 0, valRn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sbcs(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rdn = opcode & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRm = cpu.Registers[rm];
        var carry = cpu.Registers.C ? 1u : 0u;

        ptrRdn = AddWithFlags(cpu, ptrRdn, ~valRm, carry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubsImmediate3(ushort opcode, CortexM0Plus cpu)
    {
        // SUBS Rd, Rn, #imm3 (Encoding T1)
        var rd = opcode & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var imm3 = (uint)((opcode >> 6) & 0x7);

        ref var ptrRd = ref cpu.Registers[rd];
        var valRn = cpu.Registers[rn];

        ptrRd = SubWithFlags(cpu, valRn, imm3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubsImmediate8(ushort opcode, CortexM0Plus cpu)
    {
        // SUBS Rd, #imm8 (Encoding T2)
        var rd = (opcode >> 8) & 0x7;
        var imm8 = (uint)(opcode & 0xFF);

        ref var ptrRd = ref cpu.Registers[rd];

        ptrRd = SubWithFlags(cpu, ptrRd, imm8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubSp(ushort opcode, CortexM0Plus cpu)
    {
        // SUB (SP minus immediate)
        var imm32 = (opcode & 0x7f) << 2;
        cpu.Registers.SP -= (ushort)imm32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubsRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 6) & 0x7;
        var rn = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRn = cpu.Registers[rn];
        var valRm = cpu.Registers[rm];

        ptrRd = SubWithFlags(cpu, valRn, valRm);
    }

    // =============================================================
    // MATH HELPERS
    // =============================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddWithFlags(CortexM0Plus cpu, uint op1, uint op2, uint carryIn)
    {
        var result64 = (ulong)op1 + op2 + carryIn;
        var result32 = (uint)result64;

        cpu.Registers.N = (int)result32 < 0;
        cpu.Registers.Z = (result32 == 0);
        cpu.Registers.C = result64 > uint.MaxValue;
        cpu.Registers.V = ((~(op1 ^ op2) & (op1 ^ result32)) & 0x80000000) != 0;

        return result32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SubWithFlags(CortexM0Plus cpu, uint op1, uint op2)
    {
        var result = op1 - op2;
        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = op1 >= op2;
        cpu.Registers.V = (((op1 ^ op2) & (op1 ^ result)) & 0x80000000) != 0;

        return result;
    }
}

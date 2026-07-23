using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Instructions;

public static class BitOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ands(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRm = cpu.Registers[rm];

        var result = ptrRdn & valRm;
        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AsrsImm5(ushort opcode, CortexM0Plus cpu)
    {
        var rd = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;
        var imm5 = (opcode >> 6) & 0x1F;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        var shiftVal = imm5 == 0 ? 31 : imm5;
        var shiftCarry = (imm5 - 1) & 0x1F;

        var result = (uint)((int)valRm >> shiftVal);
        var carry = ((valRm >> shiftCarry) & 1) != 0;

        ptrRd = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AsrsRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];

        var valRdn = ptrRdn;
        var valRm = cpu.Registers[rm];

        var shift = (int)(valRm & 0xFF);

        if (shift == 0)
        {
            cpu.Registers.N = (int)valRdn < 0;
            cpu.Registers.Z = (valRdn == 0);
            return;
        }

        var effShift = shift < 32 ? shift : 31;
        var effCarryShift = shift < 32 ? (shift - 1) : 31;

        var result = (uint)((int)valRdn >> effShift);
        var carry = ((valRdn >> effCarryShift) & 1) != 0;

        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bics(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRm = cpu.Registers[rm];

        var result = ptrRdn & ~valRm;
        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Eors(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rdn = opcode & 0x7;
        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRm = cpu.Registers[rm];

        var result = ptrRdn ^ valRm;
        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LslsImm5(ushort opcode, CortexM0Plus cpu)
    {
        var imm5 = (opcode >> 6) & 0x1F;
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        var extended = (ulong)valRm << imm5;
        var result = (uint)extended;
        var carry = (extended & 0x1_0000_0000) != 0;

        ptrRd = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LslsRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];

        var valRdn = ptrRdn;
        var shift = (int)(cpu.Registers[rm] & 0xFF);

        if (shift == 0)
        {
            cpu.Registers.N = (int)valRdn < 0;
            cpu.Registers.Z = (valRdn == 0);
            return;
        }

        // ARMv6-M §A2.2.1: for LSL, if shift ≥ 33 the result and carry are both 0.
        // C# ulong shifts are masked to mod-64, so shifts of 33–63 behave correctly
        // via the (ulong)valRdn << shift expression. However shifts ≥ 64 would wrap
        // back and produce wrong results; guard against that explicitly.
        uint result;
        bool calcCarry;
        if (shift >= 33)
        {
            result = 0;
            calcCarry = false;
        }
        else
        {
            var extended = (ulong)valRdn << shift;
            result = (uint)extended;
            calcCarry = (extended & 0x1_0000_0000UL) != 0;
        }

        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = calcCarry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LslsZero(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        ptrRd = valRm;

        cpu.Registers.N = (int)valRm < 0;
        cpu.Registers.Z = (valRm == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LsrsImm5(ushort opcode, CortexM0Plus cpu)
    {
        // LSRS Rd, Rm, #imm5
        var imm5 = (opcode >> 6) & 0x1F;
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        var result = valRm >> imm5;
        var carry = ((valRm >> (imm5 - 1)) & 1) != 0;

        ptrRd = result;

        cpu.Registers.N = false; // (result >> 31) will be always 0 if shift >= 1
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LsrsImm32(ushort opcode, CortexM0Plus cpu)
    {
        // LSRS Rd, Rm, #0
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        ptrRd = 0;

        cpu.Registers.N = false;
        cpu.Registers.Z = true;
        cpu.Registers.C = (int)valRm < 0; // Bit 31
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LsrsRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRdn = ptrRdn;
        var shift = (int)(cpu.Registers[rm] & 0xFF);

        // Fast Guard
        if (shift == 0)
        {
            cpu.Registers.N = (int)valRdn < 0;
            cpu.Registers.Z = (valRdn == 0);
            return;
        }

        var result = shift >= 32 ? 0 : valRdn >> shift;

        var rawCarryBit = (valRdn >> ((shift - 1) & 0x1F)) & 1;
        var limitMask = (32 - shift) >> 31;
        var carry = (rawCarryBit & ~limitMask) != 0;

        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
        cpu.Registers.C = carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovRegister(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;
        var rd = ((opcode >> 4) & 0x8) | (opcode & 0x7);

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        valRm += (uint)((rm + 1) >> 4) << 1;
        ptrRd = valRm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Movs(ushort opcode, CortexM0Plus cpu)
    {
        var value = (uint)(opcode & 0xFF);
        var rd = (opcode >> 8) & 7;

        cpu.Registers[rd] = value;

        cpu.Registers.N = (int)value < 0;
        cpu.Registers.Z = value == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovToPc(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0xF;

        ref var pc = ref cpu.Registers.PC;
        var valRm = cpu.Registers[rm];

        valRm += (uint)((rm + 1) >> 4) << 1;
        pc = valRm & 0xFFFFFFFE;
        // rp2040js MOV (register) with Rd == PC: base 1 (loop) + 1.
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovToSp(ushort opcode, CortexM0Plus cpu)
    {
        // ARMv6-M §A6.7.75: MOV (register) to SP — writes the raw register value.
        // The architecture does NOT force word-alignment on this write; only the
        // processor behavior on subsequent stack accesses is affected by misalignment.
        var rm = (opcode >> 3) & 0xF;
        ref var sp = ref cpu.Registers.SP;

        var valRm = cpu.Registers[rm];
        valRm += (uint)((rm + 1) >> 4) << 1;

        sp = valRm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mvns(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 7;
        var rd = opcode & 7;

        ref var ptrRd = ref cpu.Registers[rd];
        var valRm = cpu.Registers[rm];

        var result = ~valRm;
        ptrRd = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Orrs(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 7;
        var rdn = opcode & 7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var valRm = cpu.Registers[rm];

        var result = ptrRdn | valRm;
        ptrRdn = result;

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = (result == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Rev(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        var val = cpu.Registers[rm];

        cpu.Registers[rd] = BinaryPrimitives.ReverseEndianness(val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Rev16(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        var val = cpu.Registers[rm];

        cpu.Registers[rd] = ((val & 0xFF00FF00u) >> 8) | ((val & 0x00FF00FFu) << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Revsh(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;

        var val = (ushort)cpu.Registers[rm];
        var reversed = BinaryPrimitives.ReverseEndianness(val);
        cpu.Registers[rd] = (uint)(short)reversed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Tst(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rn = opcode & 0x7;

        var result = cpu.Registers[rn] & cpu.Registers[rm];

        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = result == 0;
    }

    // ================================================================
    // ROR (Rotate Right, register)  mask=0xFFC0 pattern=0x41C0
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ror(ushort opcode, CortexM0Plus cpu)
    {
        var rdn = opcode & 0x7;
        var rm = (opcode >> 3) & 0x7;

        ref var ptrRdn = ref cpu.Registers[rdn];
        var val = ptrRdn;
        var shift = cpu.Registers[rm] & 0xFF;

        if (shift == 0)
        {
            cpu.Registers.N = (int)val < 0;
            cpu.Registers.Z = val == 0;
            return;
        }

        var shiftMod = (int)(shift & 0x1F);
        uint result;
        bool carry;

        if (shiftMod == 0)
        {
            // shift is a multiple of 32: result = val, C = bit31
            result = val;
            carry = (val >> 31) != 0;
        }
        else
        {
            result = (val >> shiftMod) | (val << (32 - shiftMod));
            carry = ((val >> (shiftMod - 1)) & 1) != 0;
        }

        ptrRdn = result;
        cpu.Registers.N = (int)result < 0;
        cpu.Registers.Z = result == 0;
        cpu.Registers.C = carry;
    }

    // ================================================================
    // Sign/Zero extend  (mask=0xFFC0, 16-bit Thumb)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sxth(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;
        cpu.Registers[rd] = (uint)(short)(ushort)cpu.Registers[rm];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sxtb(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;
        cpu.Registers[rd] = (uint)(sbyte)(byte)cpu.Registers[rm];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Uxth(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;
        cpu.Registers[rd] = cpu.Registers[rm] & 0xFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Uxtb(ushort opcode, CortexM0Plus cpu)
    {
        var rm = (opcode >> 3) & 0x7;
        var rd = opcode & 0x7;
        cpu.Registers[rd] = cpu.Registers[rm] & 0xFFu;
    }

    // ================================================================
    // CLZ (Count Leading Zeros) — Thumb-2 32-bit, mask=0xFFF0 pattern=0xFAB0
    // First halfword: 0xFABx (Rm in bits 3:0)
    // Second halfword: 0xF08x (Rd in bits 11:8, Rm in bits 3:0)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clz(ushort opcode, CortexM0Plus cpu)
    {
        var rm = opcode & 0xF;
        var second = cpu.Bus.ReadHalfWord(cpu.Registers.PC);
        cpu.Registers.PC += 2;

        var rd = (second >> 8) & 0xF;
        cpu.Registers[rd] = (uint)BitOperations.LeadingZeroCount(cpu.Registers[rm]);
    }
}

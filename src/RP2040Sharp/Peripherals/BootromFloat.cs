// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
//
// Clean-room implementation of the RP2040 bootrom floating-point ROM API: the
// 'SF' (single precision) and 'SD' (double precision) tables.
//
// Why this exists: the bootrom's own float library (mufplib, © 2020 Mark Owen)
// is not covered by the bootrom's BSD-3-Clause licence. It is licensed for use
// "solely on a Raspberry Pi RP2040 device" (or GPLv2 from its author), so it
// cannot be redistributed inside this package. tools/strip_mufplib.py removes it
// from the shipped ROM images; this class stands in for it. The ROM tables still
// hold the original function addresses, so firmware resolves and calls them
// exactly as on silicon — the emulator intercepts each entry and computes the
// result natively. See NOTICE.txt.
//
// Sources for the API — table layout, argument order, return registers and
// rounding behaviour — are the RP2040 datasheet §2.8.3.2 and pico-sdk's
// BSD-3-Clause headers (pico/bootrom/sf_table.h, pico/float.h). mufplib itself
// was never consulted.
//
// Known deviations from silicon:
//   * Timing. A hook costs a single cycle, whereas the real routines take tens to
//     hundreds. Firmware that measures how long ROM float calls take will read low.
//   * Last-bit accuracy. The results come from the .NET math library rather than
//     mufplib's approximations, so the transcendentals can differ in the final ulp
//     (ours are the more accurate ones). Only matters to code comparing exact bits.

using RP2040.Core.Cpu;

namespace RP2040.Peripherals;

/// <summary>
/// Installs native implementations of the bootrom 'SF'/'SD' float tables.
/// </summary>
internal static unsafe class BootromFloat
{
    // Entry indices, shared by both tables (pico/bootrom/sf_table.h). The double
    // table mirrors the float table's layout with double-precision equivalents.
    private const int ADD = 0, SUB = 1, MUL = 2, DIV = 3, CMP_FAST = 4, CMP_FAST_FLAGS = 5,
                      SQRT = 6, TO_INT = 7, TO_FIX = 8, TO_UINT = 9, TO_UFIX = 10,
                      INT_TO = 11, FIX_TO = 12, UINT_TO = 13, UFIX_TO = 14,
                      COS = 15, SIN = 16, TAN = 17, SINCOS = 18, EXP = 19, LN = 20,
                      CMP_BASIC = 21, ATAN2 = 22,
                      INT64_TO = 23, FIX64_TO = 24, UINT64_TO = 25, UFIX64_TO = 26,
                      TO_INT64 = 27, TO_FIX64 = 28, TO_UINT64 = 29, TO_UFIX64 = 30,
                      CONVERT = 31; // SF: float2double, SD: double2float

    private const int TableEntries = 32; // SF_TABLE_V2_SIZE / 4

    /// <summary>
    /// Resolves the 'SF' and 'SD' tables in a loaded bootrom image and registers a
    /// native hook on every entry. Safe to call for any ROM revision; entries that
    /// the revision does not populate are skipped.
    /// </summary>
    public static void Install(CortexM0Plus cpu, byte* rom)
    {
        // The ROM's own markers delimit the code strip_mufplib.py removed. Hooking is scoped to
        // that window on purpose: it is exactly the code we took out, and nothing else. Entries
        // outside it must be left alone — see InstallTable.
        var start = RomDataLookup(rom, 'F', 'S');
        var end = RomDataLookup(rom, 'D', 'E');
        if (start == 0 || end <= start) return;

        InstallTable(cpu, rom, RomDataLookup(rom, 'S', 'F'), start, end, single: true);
        InstallTable(cpu, rom, RomDataLookup(rom, 'S', 'D'), start, end, single: false);
    }

    private static void InstallTable(CortexM0Plus cpu, byte* rom, int table, int start, int end, bool single)
    {
        if (table == 0) return;

        for (var i = 0; i < TableEntries; i++)
        {
            var addr = U32(rom, table + i * 4) & ~1u; // entries are Thumb pointers

            // Only stand in for addresses inside the stripped region. An entry pointing elsewhere
            // is not ours to serve: bootrom v2 (RP2040 B1) leaves the sincos slot unimplemented,
            // pointing at unrelated ROM, and pico-sdk substitutes its own shim for it. Hooking
            // that address would hijack real bootrom code.
            if (addr < start || addr >= end) continue;

            var index = i;
            cpu.RegisterNativeHook(addr, single
                ? c => SingleEntry(c, index)
                : c => DoubleEntry(c, index));
        }
    }

    /// <summary>
    /// Reads the ROM data table (pointer at offset 0x16) looking for a two-character code,
    /// mirroring the bootrom's own rom_data_lookup. Returns 0 when absent.
    /// </summary>
    private static int RomDataLookup(byte* rom, char a, char b)
    {
        var code = (ushort)(a | (b << 8));
        for (int p = U16(rom, 0x16); p < 16384 - 4; p += 4)
        {
            var entry = U16(rom, p);
            if (entry == 0) break;
            if (entry == code) return U16(rom, p + 2);
        }
        return 0;
    }

    private static ushort U16(byte* p, int o) => (ushort)(p[o] | (p[o + 1] << 8));
    private static uint U32(byte* p, int o) => (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));

    // ── Register plumbing (AAPCS, softfp: floats in core registers) ──────────────

    private static float F(uint r) => BitConverter.UInt32BitsToSingle(r);
    private static uint U(float f) => BitConverter.SingleToUInt32Bits(f);
    private static double D(uint lo, uint hi) => BitConverter.UInt64BitsToDouble(lo | ((ulong)hi << 32));

    private static void SetD(CortexM0Plus c, double v)
    {
        var bits = BitConverter.DoubleToUInt64Bits(v);
        c.Registers.R0 = (uint)bits;
        c.Registers.R1 = (uint)(bits >> 32);
    }

    private static void SetD23(CortexM0Plus c, double v)
    {
        var bits = BitConverter.DoubleToUInt64Bits(v);
        c.Registers.R2 = (uint)bits;
        c.Registers.R3 = (uint)(bits >> 32);
    }

    private static void SetI64(CortexM0Plus c, long v)
    {
        c.Registers.R0 = (uint)v;
        c.Registers.R1 = (uint)((ulong)v >> 32);
    }

    private static long I64(uint lo, uint hi) => (long)(lo | ((ulong)hi << 32));

    // Saturating conversions. The ROM returns the clamped extreme for out-of-range
    // inputs rather than trapping; C# casts are undefined-ish there, so clamp first.
    private static int ToI32(double v) => v >= int.MaxValue ? int.MaxValue
                                        : v <= int.MinValue ? int.MinValue
                                        : double.IsNaN(v) ? 0 : (int)v;

    private static uint ToU32(double v) => v >= uint.MaxValue ? uint.MaxValue
                                         : v <= 0 ? 0
                                         : double.IsNaN(v) ? 0 : (uint)v;

    private static long ToI64(double v) => v >= long.MaxValue ? long.MaxValue
                                         : v <= long.MinValue ? long.MinValue
                                         : double.IsNaN(v) ? 0 : (long)v;

    private static ulong ToU64(double v) => v >= ulong.MaxValue ? ulong.MaxValue
                                          : v <= 0 ? 0
                                          : double.IsNaN(v) ? 0 : (ulong)v;

    // Scaling for the fixed-point conversions: value * 2^e, without overflowing the
    // exponent for extreme e (Math.Pow keeps it in double range).
    private static double Scale(double v, int e) => v * Math.Pow(2.0, e);

    /// <summary>
    /// Three-way compare. The ROM reports the ordering of a and b as a negative, zero or
    /// positive integer.
    /// </summary>
    private static int Compare(double a, double b) => a < b ? -1 : a > b ? 1 : 0;

    // ── Single precision ('SF') ─────────────────────────────────────────────────

    private static void SingleEntry(CortexM0Plus c, int index)
    {
        ref var r = ref c.Registers;
        var a = F(r.R0);
        var b = F(r.R1);

        switch (index)
        {
            case ADD: r.R0 = U(a + b); break;
            case SUB: r.R0 = U(a - b); break;
            case MUL: r.R0 = U(a * b); break;
            case DIV: r.R0 = U(a / b); break;

            case CMP_FAST:
            case CMP_BASIC: r.R0 = (uint)Compare(a, b); break;
            // Returns the ordering the same way; the caller derives condition flags from it.
            case CMP_FAST_FLAGS: r.R0 = (uint)Compare(a, b); break;

            case SQRT: r.R0 = U(MathF.Sqrt(a)); break;

            // float -> integer conversions round towards -Infinity (pico/float.h is explicit
            // that this is NOT C truncation). The _z (towards zero) variants are not ROM
            // functions; pico-sdk implements those itself.
            case TO_INT:  r.R0 = (uint)ToI32(Math.Floor((double)a)); break;
            case TO_UINT: r.R0 = ToU32(Math.Floor((double)a)); break;
            case TO_FIX:  r.R0 = (uint)ToI32(Math.Floor(Scale(a, (int)r.R1))); break;
            case TO_UFIX: r.R0 = ToU32(Math.Floor(Scale(a, (int)r.R1))); break;

            case INT_TO:  r.R0 = U((int)r.R0); break;
            case UINT_TO: r.R0 = U(r.R0); break;
            case FIX_TO:  r.R0 = U((float)Scale((int)r.R0, -(int)r.R1)); break;
            case UFIX_TO: r.R0 = U((float)Scale(r.R0, -(int)r.R1)); break;

            // The ROM's fsin computes both sine and cosine: sin in R0, cos in R1.
            // pico-sdk's sincosf relies on this (it calls FSIN and reads both).
            case SIN:
            case SINCOS:
                r.R0 = U(MathF.Sin(a));
                r.R1 = U(MathF.Cos(a));
                break;
            case COS:
                r.R0 = U(MathF.Cos(a));
                r.R1 = U(MathF.Sin(a));
                break;

            case TAN:   r.R0 = U(MathF.Tan(a)); break;
            case EXP:   r.R0 = U(MathF.Exp(a)); break;
            case LN:    r.R0 = U(MathF.Log(a)); break;
            case ATAN2: r.R0 = U(MathF.Atan2(a, b)); break;

            case INT64_TO:   r.R0 = U(I64(r.R0, r.R1)); break;
            case UINT64_TO:  r.R0 = U(r.R0 | ((ulong)r.R1 << 32)); break;
            case FIX64_TO:   r.R0 = U((float)Scale(I64(r.R0, r.R1), -(int)r.R2)); break;
            case UFIX64_TO:  r.R0 = U((float)Scale(r.R0 | ((ulong)r.R1 << 32), -(int)r.R2)); break;

            case TO_INT64:   SetI64(c, ToI64(Math.Floor((double)a))); break;
            case TO_UINT64:  SetI64(c, (long)ToU64(Math.Floor((double)a))); break;
            case TO_FIX64:   SetI64(c, ToI64(Math.Floor(Scale(a, (int)r.R1)))); break;
            case TO_UFIX64:  SetI64(c, (long)ToU64(Math.Floor(Scale(a, (int)r.R1)))); break;

            case CONVERT: SetD(c, a); break; // float2double
        }
    }

    // ── Double precision ('SD') ─────────────────────────────────────────────────

    private static void DoubleEntry(CortexM0Plus c, int index)
    {
        ref var r = ref c.Registers;
        var a = D(r.R0, r.R1);
        var b = D(r.R2, r.R3);

        switch (index)
        {
            case ADD: SetD(c, a + b); break;
            case SUB: SetD(c, a - b); break;
            case MUL: SetD(c, a * b); break;
            case DIV: SetD(c, a / b); break;

            case CMP_FAST:
            case CMP_BASIC:
            case CMP_FAST_FLAGS: r.R0 = (uint)Compare(a, b); break;

            case SQRT: SetD(c, Math.Sqrt(a)); break;

            case TO_INT:  r.R0 = (uint)ToI32(Math.Floor(a)); break;
            case TO_UINT: r.R0 = ToU32(Math.Floor(a)); break;
            case TO_FIX:  r.R0 = (uint)ToI32(Math.Floor(Scale(a, (int)r.R2))); break;
            case TO_UFIX: r.R0 = ToU32(Math.Floor(Scale(a, (int)r.R2))); break;

            case INT_TO:  SetD(c, (int)r.R0); break;
            case UINT_TO: SetD(c, r.R0); break;
            case FIX_TO:  SetD(c, Scale((int)r.R0, -(int)r.R1)); break;
            case UFIX_TO: SetD(c, Scale(r.R0, -(int)r.R1)); break;

            // pico-sdk's double sincos calls the SINCOS entry and reads sin from R0:R1
            // and cos from R2:R3. Plain dsin returns only the sine.
            case SIN: SetD(c, Math.Sin(a)); break;
            case COS: SetD(c, Math.Cos(a)); break;
            case SINCOS:
                SetD23(c, Math.Cos(a));
                SetD(c, Math.Sin(a));
                break;

            case TAN:   SetD(c, Math.Tan(a)); break;
            case EXP:   SetD(c, Math.Exp(a)); break;
            case LN:    SetD(c, Math.Log(a)); break;
            case ATAN2: SetD(c, Math.Atan2(a, b)); break;

            case INT64_TO:  SetD(c, I64(r.R0, r.R1)); break;
            case UINT64_TO: SetD(c, r.R0 | ((ulong)r.R1 << 32)); break;
            case FIX64_TO:  SetD(c, Scale(I64(r.R0, r.R1), -(int)r.R2)); break;
            case UFIX64_TO: SetD(c, Scale(r.R0 | ((ulong)r.R1 << 32), -(int)r.R2)); break;

            case TO_INT64:  SetI64(c, ToI64(Math.Floor(a))); break;
            case TO_UINT64: SetI64(c, (long)ToU64(Math.Floor(a))); break;
            case TO_FIX64:  SetI64(c, ToI64(Math.Floor(Scale(a, (int)r.R2)))); break;
            case TO_UFIX64: SetI64(c, (long)ToU64(Math.Floor(Scale(a, (int)r.R2)))); break;

            case CONVERT: r.R0 = U((float)a); break; // double2float
        }
    }
}

using RP2040.Peripherals;
using Xunit;

namespace RP2040Sharp.Tests.Peripherals;

/// <summary>
/// Verifies the native stand-in for the bootrom float library ('SF'/'SD' tables). The shipped ROM
/// images have mufplib stripped (it is not redistributable — see NOTICE.txt), so these entries are
/// served by BootromFloat. Each test resolves a table entry the way firmware does — via the ROM data
/// table — and calls it through the CPU, so it exercises the real hook dispatch path.
/// </summary>
public sealed class BootromFloatTests
{
    // Entry offsets from pico-sdk's pico/bootrom/sf_table.h; the 'SD' table mirrors the layout.
    private const int FADD = 0x00, FSUB = 0x04, FMUL = 0x08, FDIV = 0x0c, FSQRT = 0x18,
                      FLOAT2INT = 0x1c, FLOAT2FIX = 0x20, INT2FLOAT = 0x2c, FIX2FLOAT = 0x30,
                      FCOS = 0x3c, FSIN = 0x40, FEXP = 0x4c, FLN = 0x50, FLOAT2DOUBLE = 0x7c;

    private static byte[] MinimalImage()
    {
        var img = new byte[512];
        BitConverter.GetBytes(0x20040000u).CopyTo(img, 0);
        BitConverter.GetBytes(0x10000101u).CopyTo(img, 4);
        return img;
    }

    private static RP2040Machine Booted(RP2040BootromRevision rev = RP2040BootromRevision.B1)
    {
        var m = new RP2040Machine(bootrom: rev);
        m.LoadFlash(MinimalImage());
        return m;
    }

    /// <summary>Resolves a two-character code in the ROM data table, as rom_data_lookup does.</summary>
    private static uint DataLookup(RP2040Machine m, char a, char b)
    {
        var code = (ushort)(a | (b << 8));
        for (uint p = m.Bus.ReadHalfWord(0x16); p < 16384; p += 4)
        {
            var entry = m.Bus.ReadHalfWord(p);
            if (entry == 0) break;
            if (entry == code) return m.Bus.ReadHalfWord(p + 2);
        }
        return 0;
    }

    private static uint Entry(RP2040Machine m, char a, char b, int offset) =>
        m.Bus.ReadWord(DataLookup(m, a, b) + (uint)offset);

    /// <summary>
    /// Calls a ROM function with the given core registers. Must go through Run(), which is where
    /// native hooks are dispatched — Step() decodes the opcode directly and would miss them.
    /// The hook returns via BX LR to a sentinel that parks the CPU in a self-branch.
    /// </summary>
    private static void Call(RP2040Machine m, uint fn, uint r0 = 0, uint r1 = 0, uint r2 = 0, uint r3 = 0)
    {
        const uint returnTo = 0x10000100;
        m.Cpu.Registers.R0 = r0;
        m.Cpu.Registers.R1 = r1;
        m.Cpu.Registers.R2 = r2;
        m.Cpu.Registers.R3 = r3;
        m.Cpu.Registers.LR = returnTo | 1;
        m.Cpu.Registers.PC = fn & ~1u;
        m.Cpu.Run(1); // one hook dispatch, then BX LR
        m.Cpu.Registers.PC.Should().Be(returnTo, "the ROM entry should have returned to its caller");
    }

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static uint U(float f) => BitConverter.SingleToUInt32Bits(f);

    [Theory]
    [InlineData(RP2040BootromRevision.B1)]
    [InlineData(RP2040BootromRevision.B2)]
    public void Sf_table_arithmetic_is_served_natively(RP2040BootromRevision rev)
    {
        using var m = Booted(rev);

        Call(m, Entry(m, 'S', 'F', FADD), U(1.5f), U(2.25f));
        F(m.Cpu.Registers.R0).Should().Be(3.75f);

        Call(m, Entry(m, 'S', 'F', FSUB), U(1.5f), U(2.25f));
        F(m.Cpu.Registers.R0).Should().Be(-0.75f);

        Call(m, Entry(m, 'S', 'F', FMUL), U(2.5f), U(4f));
        F(m.Cpu.Registers.R0).Should().Be(10f);

        Call(m, Entry(m, 'S', 'F', FDIV), U(7f), U(2f));
        F(m.Cpu.Registers.R0).Should().Be(3.5f);

        Call(m, Entry(m, 'S', 'F', FSQRT), U(16f));
        F(m.Cpu.Registers.R0).Should().Be(4f);
    }

    [Fact]
    public void Sf_transcendentals_are_accurate()
    {
        using var m = Booted();

        Call(m, Entry(m, 'S', 'F', FEXP), U(1f));
        F(m.Cpu.Registers.R0).Should().BeApproximately(MathF.E, 1e-5f);

        Call(m, Entry(m, 'S', 'F', FLN), U(MathF.E));
        F(m.Cpu.Registers.R0).Should().BeApproximately(1f, 1e-5f);

        Call(m, Entry(m, 'S', 'F', FCOS), U(0f));
        F(m.Cpu.Registers.R0).Should().BeApproximately(1f, 1e-6f);
    }

    /// <summary>
    /// The ROM's fsin returns the sine in r0 *and* the cosine in r1 — pico-sdk's sincosf calls
    /// FSIN and reads both, so getting this wrong silently breaks sincosf.
    /// </summary>
    [Fact]
    public void Fsin_returns_sine_in_r0_and_cosine_in_r1()
    {
        using var m = Booted();

        Call(m, Entry(m, 'S', 'F', FSIN), U(0.5f));
        F(m.Cpu.Registers.R0).Should().BeApproximately(MathF.Sin(0.5f), 1e-6f);
        F(m.Cpu.Registers.R1).Should().BeApproximately(MathF.Cos(0.5f), 1e-6f);
    }

    /// <summary>
    /// float2int rounds towards -Infinity, not towards zero (pico/float.h is explicit that this
    /// is not the C behaviour; the round-towards-zero variants are not ROM functions).
    /// </summary>
    [Fact]
    public void Float2int_rounds_towards_negative_infinity()
    {
        using var m = Booted();
        var fn = Entry(m, 'S', 'F', FLOAT2INT);

        Call(m, fn, U(2.75f));
        ((int)m.Cpu.Registers.R0).Should().Be(2);

        Call(m, fn, U(-2.25f));
        ((int)m.Cpu.Registers.R0).Should().Be(-3, "rounding is towards -Infinity, so -2.25 floors to -3");
    }

    [Fact]
    public void Fixed_point_conversions_round_trip()
    {
        using var m = Booted();

        // float2fix(f, e) = floor(f * 2^e)
        Call(m, Entry(m, 'S', 'F', FLOAT2FIX), U(1.5f), 8);
        ((int)m.Cpu.Registers.R0).Should().Be(384);

        // fix2float(m, e) = m * 2^-e
        Call(m, Entry(m, 'S', 'F', FIX2FLOAT), unchecked((uint)384), 8);
        F(m.Cpu.Registers.R0).Should().Be(1.5f);

        Call(m, Entry(m, 'S', 'F', INT2FLOAT), unchecked((uint)-7));
        F(m.Cpu.Registers.R0).Should().Be(-7f);
    }

    [Fact]
    public void Float2double_returns_a_double_in_r0_r1()
    {
        using var m = Booted();

        Call(m, Entry(m, 'S', 'F', FLOAT2DOUBLE), U(0.5f));
        var bits = m.Cpu.Registers.R0 | ((ulong)m.Cpu.Registers.R1 << 32);
        BitConverter.UInt64BitsToDouble(bits).Should().Be(0.5);
    }

    [Theory]
    [InlineData(RP2040BootromRevision.B1)]
    [InlineData(RP2040BootromRevision.B2)]
    public void Sd_table_arithmetic_is_served_natively(RP2040BootromRevision rev)
    {
        using var m = Booted(rev);

        static (uint lo, uint hi) Split(double d)
        {
            var b = BitConverter.DoubleToUInt64Bits(d);
            return ((uint)b, (uint)(b >> 32));
        }
        double Result(RP2040Machine mm) =>
            BitConverter.UInt64BitsToDouble(mm.Cpu.Registers.R0 | ((ulong)mm.Cpu.Registers.R1 << 32));

        var (alo, ahi) = Split(1.5);
        var (blo, bhi) = Split(2.25);

        Call(m, Entry(m, 'S', 'D', FADD), alo, ahi, blo, bhi);
        Result(m).Should().Be(3.75);

        Call(m, Entry(m, 'S', 'D', FMUL), alo, ahi, blo, bhi);
        Result(m).Should().Be(3.375);

        var (slo, shi) = Split(16.0);
        Call(m, Entry(m, 'S', 'D', FSQRT), slo, shi);
        Result(m).Should().Be(4.0);
    }

    /// <summary>
    /// Every table entry that points into the stripped region must be hooked: the region is filled
    /// with BKPT, so any gap would trap instead of returning.
    /// </summary>
    [Theory]
    [InlineData(RP2040BootromRevision.B1)]
    [InlineData(RP2040BootromRevision.B2)]
    public void Every_stripped_table_entry_is_hooked(RP2040BootromRevision rev)
    {
        using var m = Booted(rev);
        uint start = DataLookup(m, 'F', 'S'), end = DataLookup(m, 'D', 'E');
        var served = 0;

        foreach (var (a, b) in new[] { ('S', 'F'), ('S', 'D') })
        {
            for (var i = 0; i < 32; i++)
            {
                var fn = Entry(m, a, b, i * 4) & ~1u;
                if (fn < start || fn >= end) continue; // not ours to serve — see below

                m.Bus.ReadHalfWord(fn).Should().Be(0xBE00, "mufplib is stripped from the image");
                Call(m, fn); // would trap on the BKPT if the entry were not hooked
                served++;
            }
        }

        served.Should().BeGreaterThan(50, "both tables should be served natively");
    }

    /// <summary>
    /// Bootrom v2 (B1 silicon) never implemented the sincos slot: it points at unrelated ROM rather
    /// than at the float library, and pico-sdk substitutes its own shim. That address must be left
    /// alone — hooking it would hijack real bootrom code. v3 (B2) implements it for real.
    /// </summary>
    [Fact]
    public void B1_leaves_the_unimplemented_sincos_slot_untouched()
    {
        using var b1 = Booted(RP2040BootromRevision.B1);
        const int SINCOS = 0x48;
        uint start = DataLookup(b1, 'F', 'S'), end = DataLookup(b1, 'D', 'E');

        var slot = Entry(b1, 'S', 'F', SINCOS) & ~1u;
        (slot < start || slot >= end).Should().BeTrue("v2 leaves sincos outside the float library");
        b1.Bus.ReadHalfWord(slot).Should().NotBe(0xBE00, "that ROM code must survive untouched");

        using var b2 = Booted(RP2040BootromRevision.B2);
        var b2Slot = Entry(b2, 'S', 'F', SINCOS) & ~1u;
        (b2Slot >= DataLookup(b2, 'F', 'S') && b2Slot < DataLookup(b2, 'D', 'E')).Should()
            .BeTrue("v3 implements sincos inside the float library");
        Call(b2, b2Slot | 1, U(0.5f));
        F(b2.Cpu.Registers.R0).Should().BeApproximately(MathF.Sin(0.5f), 1e-6f);
        F(b2.Cpu.Registers.R1).Should().BeApproximately(MathF.Cos(0.5f), 1e-6f);
    }
}

using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Sio;

/// <summary>
/// Tests for the SIO hardware divider (§2.3.1.6 of RP2040 TRM).
/// </summary>
public abstract class SioTests
{
    private sealed class Fixture : IDisposable
    {
        public BusInterconnect Bus { get; }
        public CortexM0Plus Cpu { get; }
        public SioPeripheral Sio { get; }

        public Fixture()
        {
            Bus = new BusInterconnect();
            Cpu = new CortexM0Plus(Bus);
            Sio = new SioPeripheral(Cpu);
        }

        public void Dispose() => Bus.Dispose();
    }

    // Unsigned divide register offsets (local from SIO base 0xD0000000)
    private const uint DIV_UDIVIDEND = 0x060;
    private const uint DIV_UDIVISOR  = 0x064;
    private const uint DIV_SDIVIDEND = 0x068;
    private const uint DIV_SDIVISOR  = 0x06C;
    private const uint DIV_QUOTIENT  = 0x070;
    private const uint DIV_REMAINDER = 0x074;
    private const uint DIV_CSR       = 0x078;

    private const uint SPINLOCK_BASE = 0x100;

    public class UnsignedDivide
    {
        [Fact]
        public void Divide_100_by_7_returns_quotient_14_remainder_2()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_UDIVIDEND, 100);
            f.Sio.WriteWord(DIV_UDIVISOR, 7);

            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(14u);
            f.Sio.ReadWord(DIV_REMAINDER).Should().Be(2u);
        }

        [Fact]
        public void Divide_sets_CSR_READY_bit()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_UDIVIDEND, 50);
            f.Sio.WriteWord(DIV_UDIVISOR, 5);

            var csr = f.Sio.ReadWord(DIV_CSR);
            (csr & 0x1).Should().Be(1u, "READY bit must be set after divide");
        }

        [Fact]
        public void Divide_by_zero_returns_0xFFFFFFFF_quotient()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_UDIVIDEND, 42);
            f.Sio.WriteWord(DIV_UDIVISOR, 0);

            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(0xFFFFFFFFu);
            f.Sio.ReadWord(DIV_REMAINDER).Should().Be(42u);
        }

        [Fact]
        public void Divide_large_value_rounds_down()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_UDIVIDEND, 0xFFFFFFFF);
            f.Sio.WriteWord(DIV_UDIVISOR, 0x10000);

            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(0xFFFFu);
        }
    }

    public class SignedDivide
    {
        [Fact]
        public void Signed_divide_negative_dividend()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_SDIVIDEND, unchecked((uint)-100));
            f.Sio.WriteWord(DIV_SDIVISOR, 7);

            // -100 / 7 = -14 remainder -2 (truncation towards zero)
            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(unchecked((uint)-14));
            f.Sio.ReadWord(DIV_REMAINDER).Should().Be(unchecked((uint)-2));
        }

        [Fact]
        public void Signed_divide_both_negative()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_SDIVIDEND, unchecked((uint)-48));
            f.Sio.WriteWord(DIV_SDIVISOR, unchecked((uint)-6));

            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(8u);
            f.Sio.ReadWord(DIV_REMAINDER).Should().Be(0u);
        }

        [Fact]
        public void Signed_divide_by_zero_positive_dividend()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_SDIVIDEND, 10);
            f.Sio.WriteWord(DIV_SDIVISOR, 0);

            // positive dividend / 0 → quotient = 1 per RP2040 spec
            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(1u);
        }

        [Fact]
        public void Signed_divide_by_zero_negative_dividend()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(DIV_SDIVIDEND, unchecked((uint)-10));
            f.Sio.WriteWord(DIV_SDIVISOR, 0);

            // negative dividend / 0 → quotient = -1 = 0xFFFFFFFF per RP2040 spec
            f.Sio.ReadWord(DIV_QUOTIENT).Should().Be(0xFFFFFFFFu);
        }
    }

    public class DividerSave
    {
        [Fact]
        public void Writing_quotient_sets_DIRTY_bit()
        {
            using var f = new Fixture();
            // Normal divide first
            f.Sio.WriteWord(DIV_UDIVIDEND, 10);
            f.Sio.WriteWord(DIV_UDIVISOR, 2);
            // Verify READY is set
            (f.Sio.ReadWord(DIV_CSR) & 0x1).Should().Be(1u);
            f.Sio.ReadWord(DIV_QUOTIENT);
            (f.Sio.ReadWord(DIV_CSR) & 0x2).Should().Be(0u, "reading the quotient clears DIRTY");

            // Save quotient (simulating context save)
            f.Sio.WriteWord(DIV_QUOTIENT, 99);
            // DIRTY bit should now be set (bit 1)
            (f.Sio.ReadWord(DIV_CSR) & 0x2).Should().Be(2u, "writing quotient sets DIRTY");
        }
    }

    public class Spinlocks
    {
        [Fact]
        public void Claim_unclaimed_spinlock_returns_nonzero()
        {
            using var f = new Fixture();
            var result = f.Sio.ReadWord(SPINLOCK_BASE);  // spinlock 0
            result.Should().NotBe(0u, "claiming a free spinlock returns its bit");
        }

        [Fact]
        public void Claim_already_taken_spinlock_returns_zero()
        {
            using var f = new Fixture();
            f.Sio.ReadWord(SPINLOCK_BASE);  // claim spinlock 0
            var second = f.Sio.ReadWord(SPINLOCK_BASE);
            second.Should().Be(0u, "spinlock already held returns 0");
        }

        [Fact]
        public void Release_spinlock_allows_reclaim()
        {
            using var f = new Fixture();
            f.Sio.ReadWord(SPINLOCK_BASE);          // claim
            f.Sio.WriteWord(SPINLOCK_BASE, 0);      // release (any write)
            var reclaim = f.Sio.ReadWord(SPINLOCK_BASE);
            reclaim.Should().NotBe(0u, "reclaiming after release must succeed");
        }

        [Fact]
        public void SPINLOCK_ST_reflects_claimed_locks()
        {
            using var f = new Fixture();
            const uint SPINLOCK_ST = 0x05C;
            f.Sio.ReadWord(SPINLOCK_BASE);       // claim spinlock 0
            f.Sio.ReadWord(SPINLOCK_BASE + 4);   // claim spinlock 1
            var st = f.Sio.ReadWord(SPINLOCK_ST);
            (st & 0x3).Should().Be(0x3u, "SPINLOCK_ST bits 0-1 reflect claimed locks");
        }
    }

    /// <summary>
    /// Tests for SIO Interpolators (INTERP0 at 0x080, INTERP1 at 0x0C0).
    /// Each interpolator has ACCUM0/1, BASE0/1/2, CTRL_LANE0/1, POP_LANE0/1/FULL,
    /// PEEK_LANE0/1/FULL, ACCUM0_ADD / ACCUM1_ADD, BASE_1AND0.
    /// </summary>
    public class Interpolators
    {
        // INTERP0 register base (relative to SIO)
        private const uint INTERP0    = 0x080;
        private const uint INTERP_ACCUM0     = 0x00;
        private const uint INTERP_ACCUM1     = 0x04;
        private const uint INTERP_BASE0      = 0x08;
        private const uint INTERP_BASE1      = 0x0C;
        private const uint INTERP_BASE2      = 0x10;
        private const uint INTERP_POP_LANE0  = 0x14;
        private const uint INTERP_POP_LANE1  = 0x18;
        private const uint INTERP_POP_FULL   = 0x1C;
        private const uint INTERP_PEEK_LANE0 = 0x20;
        private const uint INTERP_PEEK_LANE1 = 0x24;
        private const uint INTERP_PEEK_FULL  = 0x28;
        private const uint INTERP_CTRL0      = 0x2C;
        private const uint INTERP_CTRL1      = 0x30;
        private const uint INTERP_ACCUM0_ADD = 0x34;
        private const uint INTERP_ACCUM1_ADD = 0x38;
        private const uint INTERP_BASE_1AND0 = 0x3C;

        private static uint R0(uint reg) => INTERP0 + reg;         // INTERP0 register
        private static uint R1(uint reg) => 0x0C0 + reg;           // INTERP1 register

        // CTRL_LANE bits
        private const uint CTRL_SHIFT_MASK = 0x1F;                 // bits [4:0]
        private const uint CTRL_MASK_LSB_SHIFT = 5;
        private const uint CTRL_MASK_MSB_SHIFT = 10;
        private const uint CTRL_SIGNED = 1u << 15;
        private const uint CTRL_CROSS_INPUT = 1u << 16;

        [Fact]
        public void Accum0_and_Accum1_are_read_write()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0xDEAD_BEEFu);
            f.Sio.WriteWord(R0(INTERP_ACCUM1), 0xCAFE_0000u);

            f.Sio.ReadWord(R0(INTERP_ACCUM0)).Should().Be(0xDEAD_BEEFu);
            f.Sio.ReadWord(R0(INTERP_ACCUM1)).Should().Be(0xCAFE_0000u);
        }

        [Fact]
        public void Base0_Base1_Base2_are_read_write()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(R0(INTERP_BASE0), 0x11111111u);
            f.Sio.WriteWord(R0(INTERP_BASE1), 0x22222222u);
            f.Sio.WriteWord(R0(INTERP_BASE2), 0x33333333u);

            f.Sio.ReadWord(R0(INTERP_BASE0)).Should().Be(0x11111111u);
            f.Sio.ReadWord(R0(INTERP_BASE1)).Should().Be(0x22222222u);
            f.Sio.ReadWord(R0(INTERP_BASE2)).Should().Be(0x33333333u);
        }

        [Fact]
        public void BASE_1AND0_write_splits_into_base0_and_base1()
        {
            using var f = new Fixture();
            // BASE_1AND0: low 16 bits → BASE0, high 16 bits → BASE1
            f.Sio.WriteWord(R0(INTERP_BASE_1AND0), 0xBBBB_AAAAu);

            f.Sio.ReadWord(R0(INTERP_BASE0)).Should().Be(0x0000_AAAAu, "BASE0 = lower 16 bits");
            f.Sio.ReadWord(R0(INTERP_BASE1)).Should().Be(0x0000_BBBBu, "BASE1 = upper 16 bits");
        }

        [Fact]
        public void Lane0_peek_returns_shifted_masked_accum_plus_base()
        {
            using var f = new Fixture();
            // CTRL_LANE0: SHIFT=4, MASK_LSB=0, MASK_MSB=7 → field = bits[7:0] of (ACCUM0 >> 4)
            uint ctrl = (4 << 0)    |   // SHIFT = 4 (bits [4:0])
                        (0 << 5)    |   // MASK_LSB = 0 (bits [9:5])
                        (7 << 10);      // MASK_MSB = 7 (bits [14:10])
            f.Sio.WriteWord(R0(INTERP_CTRL0), ctrl);
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0x0000_00F0u); // ACCUM0 = 0xF0
            f.Sio.WriteWord(R0(INTERP_BASE0),  0x0000_0001u); // BASE0 = 1

            // RESULT0 = ((ACCUM0 >> SHIFT) & MASK) + BASE0
            // = ((0xF0 >> 4) & 0xFF) + 1 = 0x0F + 1 = 0x10
            f.Sio.ReadWord(R0(INTERP_PEEK_LANE0)).Should().Be(0x10u);
        }

        [Fact]
        public void POP_LANE0_returns_same_as_PEEK_then_advances_ACCUM()
        {
            using var f = new Fixture();
            // CTRL_LANE0: SHIFT=0, MASK_LSB=0, MASK_MSB=31 (passthrough), no BASE
            uint ctrl = (0u << 0) | (0u << 5) | (31u << 10); // full 32-bit passthrough
            f.Sio.WriteWord(R0(INTERP_CTRL0), ctrl);
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0x100u);
            f.Sio.WriteWord(R0(INTERP_ACCUM1), 0x000u);
            f.Sio.WriteWord(R0(INTERP_BASE0),  0x10u);   // step = 16
            f.Sio.WriteWord(R0(INTERP_BASE1),  0x00u);

            var peekBefore = f.Sio.ReadWord(R0(INTERP_PEEK_LANE0));
            var popVal     = f.Sio.ReadWord(R0(INTERP_POP_LANE0));  // advances ACCUM0 by BASE0
            var peekAfter  = f.Sio.ReadWord(R0(INTERP_PEEK_LANE0));

            popVal.Should().Be(peekBefore, "POP returns the same value as PEEK before the pop");
            peekAfter.Should().Be(peekBefore + 0x10u, "ACCUM0 advanced by BASE0 after POP_LANE0");
        }

        [Fact]
        public void Signed_mode_sign_extends_shifted_result()
        {
            using var f = new Fixture();
            // CTRL_LANE0: SHIFT=0, MASK_MSB=7 (byte), SIGNED=1
            uint ctrl = (0u << 0) | (0u << 5) | (7u << 10) | CTRL_SIGNED;
            f.Sio.WriteWord(R0(INTERP_CTRL0), ctrl);
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0x000000FFu); // 0xFF = -1 as signed byte
            f.Sio.WriteWord(R0(INTERP_BASE0),  0x0u);

            // Signed extension: bits[7:0] = 0xFF → sign-extend to 32 bits = 0xFFFFFFFF
            f.Sio.ReadWord(R0(INTERP_PEEK_LANE0)).Should().Be(0xFFFFFFFFu,
                "signed mode sign-extends the extracted field");
        }

        [Fact]
        public void ACCUM0_ADD_atomically_adds_to_ACCUM0()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0x100u);
            f.Sio.WriteWord(R0(INTERP_ACCUM0_ADD), 0x050u); // add 0x50 to ACCUM0

            f.Sio.ReadWord(R0(INTERP_ACCUM0)).Should().Be(0x150u, "ACCUM0_ADD adds to ACCUM0");
        }

        [Fact]
        public void ACCUM1_ADD_atomically_adds_to_ACCUM1()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(R1(INTERP_ACCUM1), 0x200u);
            f.Sio.WriteWord(R1(INTERP_ACCUM1_ADD), 0x100u);

            f.Sio.ReadWord(R1(INTERP_ACCUM1)).Should().Be(0x300u);
        }

        [Fact]
        public void Interp1_independent_from_interp0()
        {
            using var f = new Fixture();
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0xAAAAAAAAu);
            f.Sio.WriteWord(R1(INTERP_ACCUM0), 0x55555555u);

            f.Sio.ReadWord(R0(INTERP_ACCUM0)).Should().Be(0xAAAAAAAAu);
            f.Sio.ReadWord(R1(INTERP_ACCUM0)).Should().Be(0x55555555u);
        }

        [Fact]
        public void CROSS_INPUT_lane0_uses_accum1_as_input()
        {
            using var f = new Fixture();
            // CTRL_LANE0: SHIFT=0, MASK full 32-bit, CROSS_INPUT=1
            uint ctrl = (0u << 0) | (0u << 5) | (31u << 10) | CTRL_CROSS_INPUT;
            f.Sio.WriteWord(R0(INTERP_CTRL0), ctrl);
            f.Sio.WriteWord(R0(INTERP_ACCUM0), 0xAAAAAAAAu); // ACCUM0 = source normally
            f.Sio.WriteWord(R0(INTERP_ACCUM1), 0x12345678u); // ACCUM1 = cross source
            f.Sio.WriteWord(R0(INTERP_BASE0),  0x0u);

            // CROSS_INPUT=1: lane0 uses ACCUM1 instead of ACCUM0
            f.Sio.ReadWord(R0(INTERP_PEEK_LANE0)).Should().Be(0x12345678u,
                "CROSS_INPUT makes lane0 use ACCUM1 as input");
        }
    }
}

using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pwm;

namespace RP2040.Peripherals.Tests.Pwm;

/// <summary>
/// Tests for the RP2040 PWM peripheral (§4.5 of RP2040 TRM).
/// </summary>
public abstract class PwmTests
{
    private static uint SliceBase(int s) => (uint)(s * 0x14);
    private static uint CSR(int s)  => SliceBase(s) + 0x00;
    private static uint DIV(int s)  => SliceBase(s) + 0x04;
    private static uint CTR(int s)  => SliceBase(s) + 0x08;
    private static uint CC(int s)   => SliceBase(s) + 0x0C;
    private static uint TOP(int s)  => SliceBase(s) + 0x10;

    private const uint REG_EN   = 0xA0;
    private const uint REG_INTR = 0xA4;
    private const uint REG_INTE = 0xA8;
    private const uint REG_INTS = 0xB0;

    private const uint CSR_EN         = 1u << 0;
    private const uint CSR_PH_CORRECT = 1u << 1;
    private const uint CSR_A_INV      = 1u << 2;
    private const uint CSR_B_INV      = 1u << 3;
    private const uint CSR_PH_ADV     = 1u << 7;
    private const uint CSR_PH_RET     = 1u << 6;

    private sealed class Fixture : IDisposable
    {
        public BusInterconnect Bus { get; }
        public CortexM0Plus Cpu { get; }
        public PwmPeripheral Pwm { get; }

        public Fixture()
        {
            Bus = new BusInterconnect();
            Cpu = new CortexM0Plus(Bus);
            Pwm = new PwmPeripheral(Cpu);
        }

        public void Dispose() => Bus.Dispose();
    }

    public class Counter
    {
        [Fact]
        public void Counter_advances_when_slice_enabled()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_EN);
            f.Pwm.WriteWord(DIV(0), 1 << 4);  // integer=1, frac=0

            f.Pwm.Tick(10);

            f.Pwm.ReadWord(CTR(0)).Should().BeGreaterThan(0u, "counter should advance");
        }

        [Fact]
        public void Counter_does_not_advance_when_slice_disabled()
        {
            using var f = new Fixture();
            // CSR_EN = 0 (default)
            f.Pwm.Tick(100);
            f.Pwm.ReadWord(CTR(0)).Should().Be(0u);
        }

        [Fact]
        public void Counter_wraps_at_TOP()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(TOP(0), 9u);       // wrap at 9
            f.Pwm.WriteWord(DIV(0), 1 << 4);
            f.Pwm.WriteWord(CTR(0), 8u);       // start near top
            f.Pwm.WriteWord(CSR(0), CSR_EN);

            f.Pwm.Tick(3);  // should wrap

            f.Pwm.ReadWord(CTR(0)).Should().BeLessThanOrEqualTo(9u, "counter should stay within TOP");
        }

        [Fact]
        public void Writing_CTR_sets_counter()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CTR(0), 42u);
            f.Pwm.ReadWord(CTR(0)).Should().Be(42u);
        }
    }

    public class Interrupts
    {
        [Fact]
        public void INTR_bit_set_after_wrap()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(TOP(0), 4u);
            f.Pwm.WriteWord(DIV(0), 1 << 4);
            f.Pwm.WriteWord(CTR(0), 3u);
            f.Pwm.WriteWord(CSR(0), CSR_EN);

            f.Pwm.Tick(5);  // enough to wrap

            (f.Pwm.ReadWord(REG_INTR) & 1u).Should().Be(1u, "INTR bit 0 should be set after wrap");
        }

        [Fact]
        public void INTR_cleared_by_writing_1()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(TOP(0), 2u);
            f.Pwm.WriteWord(DIV(0), 1 << 4);
            f.Pwm.WriteWord(CSR(0), CSR_EN);
            f.Pwm.Tick(5);

            f.Pwm.WriteWord(REG_INTR, 1u);
            (f.Pwm.ReadWord(REG_INTR) & 1u).Should().Be(0u);
        }

        [Fact]
        public void INTS_reflects_INTR_when_INTE_enabled()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(INTE(0), 1u);
            f.Pwm.WriteWord(TOP(0), 2u);
            f.Pwm.WriteWord(DIV(0), 1 << 4);
            f.Pwm.WriteWord(CSR(0), CSR_EN);
            f.Pwm.Tick(5);

            (f.Pwm.ReadWord(REG_INTS) & 1u).Should().Be(1u);
        }

        private static uint INTE(int s) => REG_INTE;
    }

    public class DutyReadback
    {
        [Fact]
        public void GetDutyA_returns_channel_A_compare_value()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CC(0), 0x00000064u);  // A=100 (bits 15:0)
            f.Pwm.GetDutyA(0).Should().Be(100);
        }

        [Fact]
        public void GetDutyB_returns_channel_B_compare_value()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CC(0), 0x01900000u);  // B=400 (bits 31:16)
            f.Pwm.GetDutyB(0).Should().Be(400);
        }

        [Fact]
        public void GetDutyA_inverted_when_A_INV_set()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CC(0), 0x0000FFFFu);  // A = 0xFFFF
            f.Pwm.WriteWord(CSR(0), CSR_A_INV);   // set A_INV

            f.Pwm.GetDutyA(0).Should().Be(0, "~0xFFFF = 0x0000 (ushort)");
        }
    }

    public class PhaseControl
    {
        [Fact]
        public void PH_ADV_increments_counter()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CTR(0), 5u);

            // Write CSR with PH_ADV strobe
            f.Pwm.WriteWord(CSR(0), CSR_PH_ADV | CSR_EN);
            f.Pwm.ReadWord(CTR(0)).Should().Be(6u, "PH_ADV should increment counter by 1");
        }

        [Fact]
        public void PH_RET_decrements_counter()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CTR(0), 10u);

            f.Pwm.WriteWord(CSR(0), CSR_PH_RET | CSR_EN);
            f.Pwm.ReadWord(CTR(0)).Should().Be(9u, "PH_RET should decrement counter by 1");
        }

        [Fact]
        public void PH_ADV_not_stored_in_CSR()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_PH_ADV | CSR_EN);
            var csr = f.Pwm.ReadWord(CSR(0));
            (csr & CSR_PH_ADV).Should().Be(0u, "PH_ADV is a strobe and must not be stored in CSR");
        }
    }

    public class DivMode
    {
        private const uint DIVMODE_GATED = 1u << 4;
        private const uint DIVMODE_RISE  = 2u << 4;
        private const uint DIVMODE_FALL  = 3u << 4;

        [Fact]
        public void EdgeRise_counts_rising_B_edges_only()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_EN | DIVMODE_RISE);

            for (var i = 0; i < 5; i++)
            {
                f.Pwm.SetBInput(0, true);
                f.Pwm.Tick(10_000);
                f.Pwm.SetBInput(0, false);
                f.Pwm.Tick(10_000);
            }

            f.Pwm.ReadWord(CTR(0)).Should().Be(5u, "each rising edge is one count; clock cycles must not advance the slice");
        }

        [Fact]
        public void EdgeFall_counts_falling_B_edges_only()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_EN | DIVMODE_FALL);

            for (var i = 0; i < 3; i++)
            {
                f.Pwm.SetBInput(0, true);
                f.Pwm.SetBInput(0, false);
            }
            f.Pwm.SetBInput(0, true);

            f.Pwm.ReadWord(CTR(0)).Should().Be(3u);
        }

        [Fact]
        public void Gated_counts_cycles_only_while_B_high()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_EN | DIVMODE_GATED);

            f.Pwm.Tick(100);
            f.Pwm.ReadWord(CTR(0)).Should().Be(0u, "B low: the slice clock is gated off");

            f.Pwm.SetBInput(0, true);
            f.Pwm.Tick(100);
            f.Pwm.ReadWord(CTR(0)).Should().Be(100u, "B high: counts at clk/div");

            f.Pwm.SetBInput(0, false);
            f.Pwm.Tick(100);
            f.Pwm.ReadWord(CTR(0)).Should().Be(100u);
        }

        [Fact]
        public void EdgeRise_applies_fractional_divider()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(DIV(0), 4u << 4);
            f.Pwm.WriteWord(CSR(0), CSR_EN | DIVMODE_RISE);

            for (var i = 0; i < 8; i++)
            {
                f.Pwm.SetBInput(0, true);
                f.Pwm.SetBInput(0, false);
            }

            f.Pwm.ReadWord(CTR(0)).Should().Be(2u, "8 edges / div 4 = 2 counts");
        }

        [Fact]
        public void FreeRun_ignores_B_pin()
        {
            using var f = new Fixture();
            f.Pwm.WriteWord(CSR(0), CSR_EN);

            f.Pwm.SetBInput(0, true);
            f.Pwm.SetBInput(0, false);
            f.Pwm.Tick(50);

            f.Pwm.ReadWord(CTR(0)).Should().Be(50u, "edges must not add counts in free-running mode");
        }
    }
}

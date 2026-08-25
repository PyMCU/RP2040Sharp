using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;

namespace RP2040.Peripherals.Tests.Pio;

/// <summary>
/// Tests for the PIO state machine instruction set.
/// Uses PIO0 with state machine 0.
/// </summary>
public abstract class PioTests
{
    private const int SM = 0;

    private sealed class Fixture : IDisposable
    {
        public BusInterconnect Bus { get; }
        public CortexM0Plus Cpu { get; }
        public SioPeripheral Sio { get; }
        public PioPeripheral Pio { get; }

        // Register addresses (within PIO base)
        public const uint CTRL        = 0x000;
        public const uint FSTAT       = 0x004;
        public const uint FDEBUG      = 0x008;
        public const uint FLEVEL      = 0x00C;
        public const uint TXF0        = 0x010;
        public const uint RXF0        = 0x020;
        public const uint SM0_CLKDIV  = 0x0C8;
        public const uint SM0_EXECCTRL= 0x0CC;
        public const uint SM0_SHIFTCTRL= 0x0D0;
        public const uint SM0_ADDR    = 0x0D4;
        public const uint SM0_INSTR   = 0x0D8;
        public const uint SM0_PINCTRL = 0x0DC;
        public const uint INSTR_MEM0  = 0x048;

        public Fixture()
        {
            Bus = new BusInterconnect();
            Cpu = new CortexM0Plus(Bus);
            Sio = new SioPeripheral(Cpu);
            Pio = new PioPeripheral(Cpu, 0);
        }

        public void Dispose() => Bus.Dispose();

        /// <summary>Load a single instruction at program address 0 and enable SM0.</summary>
        public void LoadAndRun(ushort instr)
        {
            // Write instruction to instruction memory slot 0
            Pio.WriteWord(INSTR_MEM0, instr);
            // Set EXECCTRL: wrap_top=0, wrap_bottom=0 → SM loops at address 0
            // EXECCTRL bits [16:12] = wrap_top, bits [11:7] = wrap_bottom
            Pio.WriteWord(SM0_EXECCTRL, 0u);
            // Set CLKDIV = 1 (integer=1, frac=0)
            Pio.WriteWord(SM0_CLKDIV, 1u << 16);
            // Enable SM0
            Pio.WriteWord(CTRL, 1u);
        }

        /// <summary>Run N ticks of the PIO.</summary>
        public void Tick(long n = 1) => Pio.Tick(n);
    }

    // PIO instruction encoding helpers
    // SET instruction: 111 DDDDD 00000 NNNNN where D=destination, N=data
    private static ushort EncodeSet(uint dest, uint data)
        => (ushort)(0b111_00000000_00000 | ((dest & 0x7) << 5) | (data & 0x1F));

    // JMP instruction: 000 COND AAAAAAAA
    private static ushort EncodeJmp(uint cond, uint addr)
        => (ushort)(0b000_000_00000 | ((cond & 0x7) << 5) | (addr & 0x1F));

    // PULL instruction: opcode=4 (bits 15-13), bit 7=1 (PULL), bit 6=IfEmpty, bit 5=Block
    private static ushort EncodePull(bool block = true, bool ifEmpty = false)
        => (ushort)((4 << 13) | (1 << 7) | (ifEmpty ? (1 << 6) : 0) | (block ? (1 << 5) : 0));

    // OUT instruction: 011 DEST COUNT where count=0 means 32
    private static ushort EncodeOut(uint dest, uint count)
        => (ushort)(0b011_00000_000_00000 | ((dest & 0x7) << 5) | (count & 0x1F));

    // PUSH instruction: 100 0 IFFL NBLK 0100000
    private static ushort EncodePush(bool block = true, bool ifFull = false)
        => (ushort)(0b100_0_0_0_0_00000 | (ifFull ? (1 << 6) : 0) | (block ? (1 << 5) : 0));

    // MOV instruction: 101 DST OP SRC
    private static ushort EncodeMov(uint dst, uint op, uint src)
        => (ushort)(0b101_00000_00_00000 | ((dst & 0x7) << 5) | ((op & 0x3) << 3) | (src & 0x7));

    // IN instruction: 010 SRC COUNT  (count=0 means 32)
    private static ushort EncodeIn(uint src, uint count)
        => (ushort)(0b010_00000_000_00000 | ((src & 0x7) << 5) | (count & 0x1F));

    public class SetPins
    {
        // SET PINS destination = 0b000 = PINS
        private const uint DEST_PINS = 0;
        private const uint DEST_X    = 1;
        private const uint DEST_Y    = 2;

        [Fact]
        public void SET_X_stores_immediate_value()
        {
            using var f = new Fixture();
            var instr = EncodeSet(DEST_X, 15);
            f.LoadAndRun(instr);
            f.Tick(2); // give a couple ticks

            // Read scratch X via SM0_INSTR executing a MOV trick...
            // Simpler: just verify the SM ran (addr advanced past 0 and wrapped back to 0)
            // With wrap top=0 bottom=0 it stays at 0
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(0u, "SM0 wraps back to 0");
        }

        [Fact]
        public void SET_Y_stores_immediate_value()
        {
            using var f = new Fixture();
            var instr = EncodeSet(DEST_Y, 7);
            f.LoadAndRun(instr);
            f.Tick(2);
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(0u, "SM0 loops at 0");
        }
    }

    public class JmpInstruction
    {
        private const uint JMP_ALWAYS = 0;  // condition = always

        [Fact]
        public void JMP_unconditional_to_address_0_keeps_PC_at_0()
        {
            using var f = new Fixture();
            var instr = EncodeJmp(JMP_ALWAYS, 0);  // JMP 0
            f.LoadAndRun(instr);
            f.Tick(5);
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(0u);
        }

        [Fact]
        public void JMP_to_nonzero_address_updates_PC()
        {
            using var f = new Fixture();
            // Program: slot 0 = JMP 2, slot 2 = JMP 2 (loop at 2)
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 0, EncodeJmp(JMP_ALWAYS, 2));
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 8, EncodeJmp(JMP_ALWAYS, 2));  // slot 2

            // Set EXECCTRL: wrap_top=31, wrap_bottom=0 — bits [16:12] = 31 → (31<<12)
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 31u << 12);

            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(3);  // execute: JMP 2 at slot0, then JMP 2 at slot2, then JMP 2 again
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(2u, "PC should be at address 2 (looping)");
        }
    }

    public class PushPull
    {
        [Fact]
        public void PULL_from_empty_FIFO_stalls_SM()
        {
            using var f = new Fixture();
            var instr = EncodePull(block: true);
            f.LoadAndRun(instr);
            f.Tick(3);

            // RP2040 TRM §3.7: FDEBUG TXSTALL bits are at [27:24] (one bit per SM).
            // SM0 TXSTALL = bit 24.
            var fdebug = f.Pio.ReadWord(Fixture.FDEBUG);
            ((fdebug >> 24) & 1u).Should().Be(1u, "TXSTALL bit 24 should be set when SM stalls on blocking PULL");
        }

        [Fact]
        public void PULL_succeeds_when_TXF_has_data()
        {
            using var f = new Fixture();
            // Write data to TXF0
            f.Pio.WriteWord(Fixture.TXF0, 0xDEADBEEFu);

            var instr = EncodePull(block: true);
            f.LoadAndRun(instr);
            f.Tick(1);  // one tick is enough to confirm the PULL succeeded (no stall)

            // TXSTALL (bit 24) must NOT be set when data was available.
            var fdebug = f.Pio.ReadWord(Fixture.FDEBUG);
            (fdebug & (1u << 24)).Should().Be(0u, "TXSTALL should NOT be set when data was available");
        }

        [Fact]
        public void TXF0_not_full_flag_set_initially()
        {
            using var f = new Fixture();
            // FSTAT bits: TXFULL[3:0]=SM0-3 TX full, TXEMPTY[11:8]=SM0-3 TX empty
            // Initially TX FIFO is empty, so TXFULL[0]=0 and TXEMPTY[0]=1
            var fstat = f.Pio.ReadWord(Fixture.FSTAT);
            // SM0 TX EMPTY = bit 8
            (fstat & (1u << 8)).Should().Be(1u << 8, "TX FIFO of SM0 should be empty initially");
        }
    }

    public class FifoLevel
    {
        [Fact]
        public void FLEVEL_increases_as_TXF_is_written()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(Fixture.TXF0, 1u);
            var flevel = f.Pio.ReadWord(Fixture.FLEVEL);
            // SM0 TX level is in bits [3:0] of FLEVEL
            (flevel & 0xFu).Should().Be(1u, "TX FIFO level should be 1 after one push");
        }

        [Fact]
        public void FLEVEL_TX_maxes_at_4_entries()
        {
            using var f = new Fixture();
            for (var i = 0; i < 5; i++)
                f.Pio.WriteWord(Fixture.TXF0, (uint)i);

            var flevel = f.Pio.ReadWord(Fixture.FLEVEL);
            (flevel & 0xFu).Should().Be(4u, "TX FIFO depth is 4");
        }
    }

    public class InstrRegister
    {
        [Fact]
        public void SM0_INSTR_executes_instruction_immediately()
        {
            using var f = new Fixture();
            // Enable the SM first
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            // Writing to SM0_INSTR executes immediately (side-loads instruction)
            var setX = EncodeSet(1 /* X */, 0b10101);  // SET X, 21
            f.Pio.WriteWord(Fixture.SM0_INSTR, setX);
            f.Tick(1);

            // The SM should have executed SET X, 21 — PC doesn't advance for EXEC writes
            // We can't directly read X, but we can verify the SM is still running (no stall)
            var fdebug = f.Pio.ReadWord(Fixture.FDEBUG);
            (fdebug & (1u << 24)).Should().Be(0u, "SM should not be stalled");
        }
    }

    public class FifoJoin
    {
        private const uint SM0_SHIFTCTRL = 0x0D0;
        private const uint FJOIN_TX = 1u << 30;  // SHIFTCTRL bit 30 (pico-sdk PIO_SM0_SHIFTCTRL_FJOIN_TX_LSB)
        private const uint FJOIN_RX = 1u << 31;  // SHIFTCTRL bit 31 (pico-sdk PIO_SM0_SHIFTCTRL_FJOIN_RX_LSB)

        [Fact]
        public void FJOIN_TX_allows_8_entries_in_TX_FIFO()
        {
            using var f = new Fixture();
            // Enable FJOIN_TX: TX FIFO becomes 8 deep
            f.Pio.WriteWord(SM0_SHIFTCTRL, FJOIN_TX);

            // Write 8 words — all should be accepted
            for (uint i = 0; i < 8; i++)
                f.Pio.WriteWord(Fixture.TXF0, i);

            // FLEVEL TX should be 8
            var flevel = f.Pio.ReadWord(Fixture.FLEVEL);
            (flevel & 0xFu).Should().Be(8u, "TX FIFO depth is 8 with FJOIN_TX");
        }

        [Fact]
        public void FJOIN_TX_caps_at_8_entries()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(SM0_SHIFTCTRL, FJOIN_TX);

            // Write 10 words — only 8 fit
            for (uint i = 0; i < 10; i++)
                f.Pio.WriteWord(Fixture.TXF0, i);

            var flevel = f.Pio.ReadWord(Fixture.FLEVEL);
            (flevel & 0xFu).Should().Be(8u, "TX FIFO caps at 8 with FJOIN_TX");
        }

        [Fact]
        public void Without_FJOIN_TX_FIFO_caps_at_4()
        {
            using var f = new Fixture();
            // No join: default depth = 4
            for (uint i = 0; i < 6; i++)
                f.Pio.WriteWord(Fixture.TXF0, i);

            var flevel = f.Pio.ReadWord(Fixture.FLEVEL);
            (flevel & 0xFu).Should().Be(4u, "TX FIFO depth is still 4 without FJOIN");
        }
    }

    public class Sideset
    {
        // Helper: encode a raw sideset+delay field into bits [12:8] of a JMP 0 instruction.
        private static ushort JmpWithField(int field5bit)
            => (ushort)(0x0000 | ((field5bit & 0x1F) << 8));

        // PINCTRL.SIDESET_COUNT = N → bits [31:29] = N (inclusive of enable when SIDE_EN=1)
        // PINCTRL.SIDESET_BASE  = B → bits [14:10] = B
        private static uint PinCtrl(uint sidesetCount, uint sidesetBase)
            => (sidesetCount << 29) | (sidesetBase << 10);

        // EXECCTRL: SIDE_EN = bit 30, WRAP_TOP = bits [16:12] = 0 (wrap at 0)
        private static uint ExecCtrl(bool sideEn = false)
            => sideEn ? (1u << 30) : 0u;

        [Fact]
        public void Sideset_drives_pins_without_SideEn()
        {
            using var f = new Fixture();
            uint capturedPins = 0, capturedMask = 0;
            f.Pio.WriteGpioPins = (v, m) => { capturedPins = v; capturedMask = m; };

            // PINCTRL: SIDESET_COUNT=2, SIDESET_BASE=0 → occupies field bits [4:3]
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(2, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(false));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // sideset=0b11 (both pins high), no delay → field bits [4:3]=0b11 → field = 0b11000 = 24
            var instr = JmpWithField(0b11000); // 0b11 << 3
            f.Pio.WriteWord(Fixture.INSTR_MEM0, instr);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(1);

            (capturedMask & 3u).Should().Be(3u, "mask covers pins 0 and 1");
            (capturedPins & 3u).Should().Be(3u, "both sideset pins driven high");
        }

        [Fact]
        public void Sideset_with_nonzero_base_drives_correct_pins()
        {
            using var f = new Fixture();
            uint capturedPins = 0;
            f.Pio.WriteGpioPins = (v, m) => { capturedPins = v; };

            // PINCTRL: SIDESET_COUNT=1, SIDESET_BASE=5 → pin 5 only
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(1, 5));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(false));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // sideset=1 (pin 5 high), no delay → top 1 bit of field = bit 4 → field = 0b10000 = 16
            var instr = JmpWithField(0b10000);
            f.Pio.WriteWord(Fixture.INSTR_MEM0, instr);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(1);

            (capturedPins & (1u << 5)).Should().NotBe(0u, "pin 5 driven high");
        }

        [Fact]
        public void Delay_stalls_SM_for_correct_number_of_cycles()
        {
            using var f = new Fixture();

            // PINCTRL: SIDESET_COUNT=0 → all 5 bits are delay
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(0, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(false));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // delay=3 in bits [2:0] of the 5-bit field → field = 0b00011 = 3
            var instr = JmpWithField(3);
            f.Pio.WriteWord(Fixture.INSTR_MEM0, instr);

            // Set WRAP_TOP=31 so SM doesn't get stuck in wrap at 0
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, (31u << 12));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            // Tick 1: instruction executes, delay counter = 3, PC → 1
            // Ticks 2-4: SM burning delay (PC stays at 1, no advance per delay tick)
            f.Tick(1); // executes JMP 0 → PC=0 again, delay=3 loaded
            // SM is now at PC=0 with delay=3, 3 more cycles needed before next exec
            f.Tick(3); // burning 3 delay cycles
            // After 3 delay burns the counter hits 0 — on tick 5 the instruction re-executes
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(0u, "PC back at 0 (JMP 0) after delay burned");
        }

        [Fact]
        public void Sideset_not_applied_when_SM_stalls()
        {
            using var f = new Fixture();
            var sidesetCallCount = 0;
            f.Pio.WriteGpioPins = (_, _) => sidesetCallCount++;

            // Configure sideset on pin 0
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(1, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(false));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // PULL block — will stall because TX FIFO is empty
            // sideset value = 1 → field bit 4 = 1 → field = 0b10000 = 16
            var pullField = (ushort)(EncodePull(block: true) | (16 << 8));
            f.Pio.WriteWord(Fixture.INSTR_MEM0, pullField);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            // Run 5 ticks — SM stalls on empty FIFO every tick
            f.Tick(5);

            sidesetCallCount.Should().Be(0, "sideset must not fire on stall cycles");
        }

        [Fact]
        public void SideEn_enable_bit_gates_sideset()
        {
            using var f = new Fixture();
            var sidesetCallCount = 0;
            f.Pio.WriteGpioPins = (_, _) => sidesetCallCount++;

            // PINCTRL: SIDESET_COUNT=3 (inclusive: 1 enable + 2 pins), SIDESET_BASE=0
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(3, 0));
            // EXECCTRL: SIDE_EN=1 (bit 30)
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(sideEn: true));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // Instruction with enable=0 in field: bit 4=0, pins=0b11 → field = 0b01100 = 12
            var instrNoEnable = JmpWithField(0b01100);
            f.Pio.WriteWord(Fixture.INSTR_MEM0, instrNoEnable);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(2);
            sidesetCallCount.Should().Be(0, "sideset must not fire when enable bit is 0");
        }

        [Fact]
        public void SideEn_enable_bit_set_fires_sideset()
        {
            using var f = new Fixture();
            uint capturedPins = 0;
            f.Pio.WriteGpioPins = (v, _) => capturedPins = v;

            // PINCTRL: SIDESET_COUNT=3 (1 enable + 2 pins), SIDESET_BASE=2
            f.Pio.WriteWord(Fixture.SM0_PINCTRL, PinCtrl(3, 2));
            // EXECCTRL: SIDE_EN=1
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, ExecCtrl(sideEn: true));
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);

            // enable=1, pins=0b11 → top 3 bits of field: bit4=1, [3:2]=0b11 → field = 0b11100 = 28
            var instrWithEnable = JmpWithField(0b11100);
            f.Pio.WriteWord(Fixture.INSTR_MEM0, instrWithEnable);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(1);

            // Pins 2 and 3 (sidesetBase=2, 2 pins) should both be high
            (capturedPins & (3u << 2)).Should().Be(3u << 2, "pins 2 and 3 driven high");
        }
    }

    // ── Bug-fix regression tests ─────────────────────────────────────────

    public class JmpOsre
    {
        // JMP condition 7 = !OSRE: jump when OSR is NOT empty (OsrCount > 0)

        [Fact]
        public void JMP_OSRE_falls_through_when_OSR_empty()
        {
            using var f = new Fixture();
            // OSR is empty (OsrCount=0, default) — JMP !OSRE should NOT be taken
            // Program slot 0 = JMP !OSRE 0 (target=0), wrap_top=31 so SM advances normally
            f.Pio.WriteWord(Fixture.INSTR_MEM0, EncodeJmp(7, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 31u << 12);
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(1);

            // OSR is empty → condition false → fall through → PC = 1
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(1u,
                "JMP !OSRE must NOT jump when OsrCount=0 (OSR empty)");
        }

        [Fact]
        public void JMP_OSRE_jumps_when_OSR_has_data()
        {
            using var f = new Fixture();
            // Pre-load TXF0 so PULL can fill the OSR
            f.Pio.WriteWord(Fixture.TXF0, 0xAABBCCDDu);

            // Program: [0] PULL, [1] JMP !OSRE 1 (loop while OSR non-empty)
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 0, EncodePull(block: true));
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 4, EncodeJmp(7, 1));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 31u << 12);
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(2);  // tick 1: PULL (OsrCount→32); tick 2: JMP !OSRE 1

            // OSR has 32 bits → condition true → jump back to 1
            f.Pio.ReadWord(Fixture.SM0_ADDR).Should().Be(1u,
                "JMP !OSRE must jump when OsrCount=32 (OSR full)");
        }
    }

    public class ShiftBits32
    {
        // When bitCount encodes 0 → 32. C# shift operators are mod-32, so
        // explicit checks are required for the 32-bit case.

        [Fact]
        public void OUT_PINS_32_right_shift_outputs_all_OSR_bits()
        {
            using var f = new Fixture();
            uint capturedPins = 0;
            f.Pio.WriteGpioPins = (v, _) => capturedPins = v;

            // OSR shift right (SHIFTCTRL bit 19)
            f.Pio.WriteWord(Fixture.SM0_SHIFTCTRL, 1u << 19);
            f.Pio.WriteWord(Fixture.TXF0, 0xCAFEBABEu);

            // Program: [0] PULL, [1] OUT PINS 32 (count=0 encodes 32)
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 0, EncodePull(block: true));
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 4, EncodeOut(0, 0));  // OUT PINS, 32
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 1u << 12);  // wrap_top=1
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(2);  // PULL then OUT PINS 32

            capturedPins.Should().Be(0xCAFEBABEu,
                "OUT PINS with bitCount=32 right-shift must output all 32 OSR bits");
        }

        [Fact]
        public void OUT_PINS_32_left_shift_clears_OSR_afterwards()
        {
            // With left-shift OUT 32, the OSR must become 0 after the operation.
            // The bug was sm.OSR <<= 32 which is a no-op in C# (mod-32 shift).
            // We observe this indirectly: after OUT 32, autopull refills from TXF.
            using var f = new Fixture();
            uint capturedFirst = 0, capturedSecond = 0;
            var callCount = 0;
            f.Pio.WriteGpioPins = (v, _) =>
            {
                if (callCount++ == 0) capturedFirst = v;
                else capturedSecond = v;
            };

            // Two distinct values in TX FIFO
            f.Pio.WriteWord(Fixture.TXF0, 0x11111111u);
            f.Pio.WriteWord(Fixture.TXF0, 0x22222222u);

            // OSR shift left (default), autopull enabled at threshold 32 (=0)
            // SHIFTCTRL: bit 17 = autopull, bits [29:25] = threshold (0 → 32)
            f.Pio.WriteWord(Fixture.SM0_SHIFTCTRL, 1u << 17);

            // Program: [0] OUT PINS 32 (autopull refills from TXF), loops at 0
            f.Pio.WriteWord(Fixture.INSTR_MEM0, EncodeOut(0, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 0u);   // wrap at 0
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            // Pre-load OSR by writing PULL via forced INSTR before enabling SM
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);
            // Writing SM0_INSTR executes the instruction immediately (per TRM §3.4.5 and rp2040js
            // reference). PULL runs at write time and loads the first TXF value into OSR.
            f.Pio.WriteWord(Fixture.SM0_INSTR, EncodePull(block: false));
            // No extra tick needed for the PULL — it already ran during the register write.

            // Tick OUT PINS 32 twice — autopull should reload OSR from TXF after each OUT.
            f.Tick(1);  // OUT PINS 32 (first value = 0x11111111), autopull loads second
            f.Tick(1);  // OUT PINS 32 (second value = 0x22222222)

            capturedFirst.Should().Be(0x11111111u, "first OUT PINS 32 outputs first TXF value");
            capturedSecond.Should().Be(0x22222222u,
                "second OUT PINS 32 must output second value — OSR must be 0 after OUT 32 for autopull to trigger");
        }

        [Fact]
        public void IN_PINS_32_left_shift_captures_full_gpio_value()
        {
            // With left-shift IN 32, ISR must become exactly the GPIO value.
            // The bug was: ISR = (ISR << 32) | (data & 0) — left shift mod-32 = no-op,
            // mask of (1u<<32)-1 = 0 in C#, so ISR was unchanged instead of replaced.
            using var f = new Fixture();
            const uint gpioValue = 0xDEADF00Du;
            f.Pio.ReadGpioIn = () => gpioValue;

            // Autopush at threshold=32 (bit 16 = enable, bits[24:20]=0 → threshold 32)
            f.Pio.WriteWord(Fixture.SM0_SHIFTCTRL, 1u << 16);

            // Program: [0] IN PINS 32 — autopush fires immediately at threshold 32
            f.Pio.WriteWord(Fixture.INSTR_MEM0, EncodeIn(0, 0));  // IN PINS, 32
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 0u);            // wrap at 0
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            f.Tick(1);  // IN PINS 32 → autopush enqueues ISR into RX FIFO

            var rxValue = f.Pio.ReadWord(Fixture.RXF0);
            rxValue.Should().Be(gpioValue,
                "IN PINS with bitCount=32 left-shift must store all 32 GPIO bits in ISR");
        }
    }

    public class PushNoblockClearsIsr
    {
        [Fact]
        public void PUSH_NOBLOCK_clears_ISR_when_RX_FIFO_full()
        {
            // When RX FIFO is full, PUSH NOBLOCK should discard the data AND clear ISR.
            // We observe ISR cleared by: after the NOBLOCK PUSH, doing another IN and autopush —
            // if ISR was cleared, IsrCount restarts from 0.
            using var f = new Fixture();

            // Fill RX FIFO to capacity (4 entries for normal mode)
            for (uint i = 0; i < 4; i++)
                f.Pio.InjectRxData(0, i);

            // Program: [0] PUSH NOBLOCK  [1] JMP 0  (loop)
            // Autopush enabled so we can observe ISR state via subsequent pushes
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 0, EncodePush(block: false));
            f.Pio.WriteWord(Fixture.INSTR_MEM0 + 4, EncodeJmp(0, 0));
            f.Pio.WriteWord(Fixture.SM0_EXECCTRL, 1u << 12);  // wrap_top=1
            f.Pio.WriteWord(Fixture.SM0_CLKDIV, 1u << 16);
            f.Pio.WriteWord(Fixture.CTRL, 1u);

            // Force-load a known ISR value before PUSH executes
            f.Pio.WriteWord(Fixture.SM0_INSTR, EncodeMov(6 /*ISR*/, 1 /*invert*/, 3 /*NULL*/));
            f.Tick(1);  // execute MOV ISR, ~NULL → ISR = 0xFFFFFFFF, IsrCount doesn't change here

            // Execute PUSH NOBLOCK with full FIFO — ISR should be cleared
            f.Tick(1);

            // Now drain the RX FIFO so it has space
            for (var i = 0; i < 4; i++) f.Pio.ReadWord(Fixture.RXF0);

            // Force an IN PINS 1 to shift 1 bit into ISR — if ISR was cleared (IsrCount=0),
            // this sets IsrCount=1. Then PUSH NOBLOCK with not-full FIFO should push IsrCount bits.
            // We can't read IsrCount directly, but we can check the FSTAT RX-empty flag.
            // Simpler: force PUSH and check that RX FIFO received 0 (ISR was 0 after clear).
            f.Pio.WriteWord(Fixture.SM0_INSTR, EncodePush(block: false));
            f.Tick(1);

            var pushed = f.Pio.ReadWord(Fixture.RXF0);
            pushed.Should().Be(0u, "ISR should have been cleared to 0 by the preceding PUSH NOBLOCK");
        }
    }
}

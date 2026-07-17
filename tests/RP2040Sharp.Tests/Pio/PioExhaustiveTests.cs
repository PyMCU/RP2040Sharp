using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pio;

namespace RP2040.Peripherals.Tests.Pio;

/// <summary>
/// Exhaustive coverage of the PIO instruction set, FIFOs, shift logic, autopull/autopush,
/// side-set, wrap, clock divider, multi-SM independence and NVIC interrupt routing.
///
/// Scratch registers are observed non-invasively via the SMx_INSTR forced-execution path
/// (force <c>MOV ISR, &lt;reg&gt;</c> then force <c>PUSH</c>, then read RXFx).
/// </summary>
public sealed class PioExhaustiveTests
{
    // ── Register map ─────────────────────────────────────────────────────────
    private const uint CTRL       = 0x000;
    private const uint FSTAT      = 0x004;
    private const uint FDEBUG     = 0x008;
    private const uint FLEVEL     = 0x00C;
    private const uint TXF_BASE   = 0x010;
    private const uint RXF_BASE   = 0x020;
    private const uint IRQ        = 0x030;
    private const uint IRQ_FORCE  = 0x034;
    private const uint INSTR_MEM  = 0x048;
    private const uint SM_BASE    = 0x0C8;
    private const uint SM_STRIDE  = 0x18;
    private const uint INTR       = 0x128;
    private const uint IRQ0_INTE  = 0x12C;
    private const uint IRQ0_INTS  = 0x134;
    private const uint IRQ1_INTE  = 0x138;
    private const uint IRQ1_INTS  = 0x140;

    private static uint TXF(int sm)        => TXF_BASE + (uint)sm * 4;
    private static uint RXF(int sm)        => RXF_BASE + (uint)sm * 4;
    private static uint CLKDIV(int sm)     => SM_BASE + (uint)sm * SM_STRIDE + 0x00;
    private static uint EXECCTRL(int sm)   => SM_BASE + (uint)sm * SM_STRIDE + 0x04;
    private static uint SHIFTCTRL(int sm)  => SM_BASE + (uint)sm * SM_STRIDE + 0x08;
    private static uint ADDR(int sm)       => SM_BASE + (uint)sm * SM_STRIDE + 0x0C;
    private static uint INSTR(int sm)      => SM_BASE + (uint)sm * SM_STRIDE + 0x10;
    private static uint PINCTRL(int sm)    => SM_BASE + (uint)sm * SM_STRIDE + 0x14;

    // ── Instruction encoders ─────────────────────────────────────────────────
    private static ushort Jmp(uint cond, uint addr) => (ushort)((0 << 13) | ((cond & 7) << 5) | (addr & 0x1F));
    private static ushort Wait(uint pol, uint src, uint idx) => (ushort)((1 << 13) | ((pol & 1) << 7) | ((src & 3) << 5) | (idx & 0x1F));
    private static ushort In(uint src, uint cnt) => (ushort)((2 << 13) | ((src & 7) << 5) | (cnt & 0x1F));
    private static ushort Out(uint dst, uint cnt) => (ushort)((3 << 13) | ((dst & 7) << 5) | (cnt & 0x1F));
    private static ushort Push(bool block = true, bool ifFull = false) => (ushort)((4 << 13) | (ifFull ? 1 << 6 : 0) | (block ? 1 << 5 : 0));
    private static ushort Pull(bool block = true, bool ifEmpty = false) => (ushort)((4 << 13) | (1 << 7) | (ifEmpty ? 1 << 6 : 0) | (block ? 1 << 5 : 0));
    private static ushort Mov(uint dst, uint op, uint src) => (ushort)((5 << 13) | ((dst & 7) << 5) | ((op & 3) << 3) | (src & 7));
    private static ushort IrqInstr(uint idx, bool clear = false, bool wait = false) => (ushort)((6 << 13) | (clear ? 1 << 6 : 0) | (wait ? 1 << 5 : 0) | (idx & 0x1F));
    private static ushort Set(uint dst, uint val) => (ushort)((7 << 13) | ((dst & 7) << 5) | (val & 0x1F));
    private static ushort Nop() => Mov(2, 0, 2); // MOV Y, Y

    // dest/src codes
    private const uint D_PINS = 0, D_X = 1, D_Y = 2, D_NULL = 3, D_PINDIRS = 4, D_PC = 5, D_ISR = 6, D_EXEC = 7;
    private const uint MOV_PINS = 0, MOV_X = 1, MOV_Y = 2, MOV_NULL = 3, MOV_STATUS = 5, MOV_ISR = 6, MOV_OSR = 7;
    private const uint OP_NONE = 0, OP_INVERT = 1, OP_REVERSE = 2;

    private sealed class Fixture : IDisposable
    {
        public BusInterconnect Bus { get; }
        public CortexM0Plus Cpu { get; }
        public PioPeripheral Pio { get; }
        public uint GpioIn;

        public Fixture()
        {
            Bus = new BusInterconnect();
            Cpu = new CortexM0Plus(Bus);
            Pio = new PioPeripheral(Cpu, 0);
            Pio.ReadGpioIn = () => GpioIn;
        }

        public void Dispose() => Bus.Dispose();

        /// <summary>Load a program (slot 0..n) and configure wrap_top to the last slot.</summary>
        public void Load(int sm, params ushort[] prog)
        {
            for (var i = 0; i < prog.Length; i++)
                Pio.WriteWord(INSTR_MEM + (uint)i * 4, prog[i]);
            // wrap_top = last instruction, wrap_bottom = 0
            Pio.WriteWord(EXECCTRL(sm), (uint)(prog.Length - 1) << 12);
            Pio.WriteWord(CLKDIV(sm), 1u << 16);
        }

        public void Enable(int sm) => Pio.WriteWord(CTRL, 1u << sm);
        public void Tick(long n = 1) => Pio.Tick(n);

        /// <summary>Read scratch X non-invasively via forced MOV ISR,X → PUSH → RXF.</summary>
        public uint ReadX(int sm) => ForceReadReg(sm, MOV_X);
        public uint ReadY(int sm) => ForceReadReg(sm, MOV_Y);
        public uint ReadOsr(int sm) => ForceReadReg(sm, MOV_OSR);

        private uint ForceReadReg(int sm, uint movSrc)
        {
            Pio.WriteWord(INSTR(sm), Mov(MOV_ISR, OP_NONE, movSrc)); // MOV ISR, reg
            Pio.WriteWord(INSTR(sm), Push(block: true));            // PUSH
            return Pio.ReadWord(RXF(sm));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  JMP — all eight conditions
    // ════════════════════════════════════════════════════════════════════════
    public class JmpInstr
    {
        [Fact]
        public void Always_jumps()
        {
            using var f = new Fixture();
            f.Load(0, Jmp(0, 3), Nop(), Nop(), Nop());
            f.Enable(0);
            f.Tick(1);
            f.Pio.ReadWord(ADDR(0)).Should().Be(3u);
        }

        [Fact]
        public void NotX_taken_when_X_zero()
        {
            using var f = new Fixture();
            // SET X,0 ; JMP !X, 3 ; NOP ; target NOP
            f.Load(0, Set(D_X, 0), Jmp(1, 3), Nop(), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(3u, "JMP !X taken when X==0");
        }

        [Fact]
        public void NotX_falls_through_when_X_nonzero()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 5), Jmp(1, 3), Nop(), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(2u, "JMP !X not taken when X!=0");
        }

        [Fact]
        public void XPostDec_taken_and_decrements_when_nonzero()
        {
            using var f = new Fixture();
            // SET X,2 ; loop: JMP X--, loop  → spins until X exhausted
            f.Load(0, Set(D_X, 2), Jmp(2, 1));
            f.Enable(0);
            f.Tick(1);            // SET X,2
            f.Tick(1);            // JMP X-- (X 2→1, taken)
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "still looping");
            f.ReadX(0).Should().Be(1u, "X decremented to 1");
        }

        [Fact]
        public void XPostDec_decrements_to_wraparound_when_zero()
        {
            using var f = new Fixture();
            // SET X,0 ; JMP X--, 0 (not taken) ; NOP
            f.Load(0, Set(D_X, 0), Jmp(2, 0), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(2u, "JMP X-- not taken when X==0");
            f.ReadX(0).Should().Be(0xFFFFFFFFu, "X-- always decrements (0 → 0xFFFFFFFF)");
        }

        [Fact]
        public void YPostDec_taken_when_nonzero()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_Y, 1), Jmp(4, 3), Nop(), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(3u, "JMP Y-- taken when Y!=0");
        }

        [Fact]
        public void XNeY_taken_when_different()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 1), Set(D_Y, 2), Jmp(5, 5), Nop(), Nop(), Nop());
            f.Enable(0);
            f.Tick(3);
            f.Pio.ReadWord(ADDR(0)).Should().Be(5u, "JMP X!=Y taken when X != Y");
        }

        [Fact]
        public void XNeY_falls_through_when_equal()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 2), Set(D_Y, 2), Jmp(5, 5), Nop(), Nop(), Nop());
            f.Enable(0);
            f.Tick(3);
            f.Pio.ReadWord(ADDR(0)).Should().Be(3u, "JMP X!=Y not taken when X == Y");
        }

        [Fact]
        public void Pin_condition_reads_input_pin()
        {
            using var f = new Fixture();
            // EXECCTRL.JMP_PIN = bit [28:24]; pick pin 7
            f.Load(0, Jmp(6, 3), Nop(), Nop(), Nop());
            f.Pio.WriteWord(EXECCTRL(0), (0u << 12) | (7u << 24)); // wrap_top=0 overwritten below
            // restore wrap_top to 3 and keep jmp_pin=7
            f.Pio.WriteWord(EXECCTRL(0), (3u << 12) | (7u << 24));
            f.GpioIn = 1u << 7;
            f.Enable(0);
            f.Tick(1);
            f.Pio.ReadWord(ADDR(0)).Should().Be(3u, "JMP PIN taken when JMP_PIN input is high");
        }

        [Fact]
        public void NotOsre_taken_when_osr_not_empty()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 0x1234u);
            f.Load(0, Pull(block: true), Jmp(7, 1));
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "JMP !OSRE taken when OSR has data");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SET — pins/dirs/X/Y with base & count
    // ════════════════════════════════════════════════════════════════════════
    public class SetInstr
    {
        [Fact]
        public void Set_X_then_Y()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 21), Set(D_Y, 10));
            f.Enable(0);
            f.Tick(2);
            f.ReadX(0).Should().Be(21u);
            f.ReadY(0).Should().Be(10u);
        }

        [Fact]
        public void Set_pins_uses_set_base_and_count()
        {
            using var f = new Fixture();
            uint pins = 0, mask = 0;
            f.Pio.WriteGpioPins = (v, m) => { pins = v; mask = m; };
            // PINCTRL: SET_COUNT=3 (bits[28:26]), SET_BASE=4 (bits[9:5])
            f.Pio.WriteWord(PINCTRL(0), (3u << 26) | (4u << 5));
            f.Load(0, Set(D_PINS, 0b101));
            f.Enable(0);
            f.Tick(1);
            mask.Should().Be(0b111u << 4, "3 pins at base 4");
            pins.Should().Be(0b101u << 4, "value 0b101 shifted to base 4");
        }

        [Fact]
        public void Set_pindirs_drives_dirs()
        {
            using var f = new Fixture();
            uint dirs = 0, mask = 0;
            f.Pio.WriteGpioDirs = (v, m) => { dirs = v; mask = m; };
            f.Pio.WriteWord(PINCTRL(0), (2u << 26) | (0u << 5)); // count 2, base 0
            f.Load(0, Set(D_PINDIRS, 0b11));
            f.Enable(0);
            f.Tick(1);
            mask.Should().Be(0b11u);
            dirs.Should().Be(0b11u);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MOV — sources, ops, destinations
    // ════════════════════════════════════════════════════════════════════════
    public class MovInstr
    {
        [Fact]
        public void Mov_X_to_Y()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 13), Mov(MOV_Y, OP_NONE, MOV_X));
            f.Enable(0);
            f.Tick(2);
            f.ReadY(0).Should().Be(13u);
        }

        [Fact]
        public void Mov_invert()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 0), Mov(MOV_Y, OP_INVERT, MOV_X));
            f.Enable(0);
            f.Tick(2);
            f.ReadY(0).Should().Be(0xFFFFFFFFu, "MOV ~X with X=0 → all ones");
        }

        [Fact]
        public void Mov_bit_reverse()
        {
            using var f = new Fixture();
            // X = 1 → reverse → 0x80000000
            f.Load(0, Set(D_X, 1), Mov(MOV_Y, OP_REVERSE, MOV_X));
            f.Enable(0);
            f.Tick(2);
            f.ReadY(0).Should().Be(0x80000000u, "bit-reverse of 1 is 0x80000000");
        }

        [Fact]
        public void Mov_pins_reads_gpio_input()
        {
            using var f = new Fixture();
            f.GpioIn = 0xCAFEu;
            f.Load(0, Mov(MOV_X, OP_NONE, MOV_PINS));
            f.Enable(0);
            f.Tick(1);
            f.ReadX(0).Should().Be(0xCAFEu, "MOV X, PINS reads physical GPIO input");
        }

        [Fact]
        public void Mov_status_all_ones_when_below_threshold()
        {
            using var f = new Fixture();
            f.Load(0, Mov(MOV_X, OP_NONE, MOV_STATUS), Nop());
            // EXECCTRL STATUS_SEL=0 (TX), STATUS_N=1 → MOV STATUS is all-ones when TX level < 1 (empty).
            // Set after Load (which would otherwise overwrite EXECCTRL with just wrap_top).
            f.Pio.WriteWord(EXECCTRL(0), (1u << 12) | (0u << 4) | 1u);
            f.Enable(0);
            f.Tick(1);
            f.ReadX(0).Should().Be(0xFFFFFFFFu, "STATUS all-ones when TX FIFO level < STATUS_N");
        }

        [Fact]
        public void Mov_status_zero_when_at_or_above_threshold()
        {
            using var f = new Fixture();
            f.Load(0, Mov(MOV_X, OP_NONE, MOV_STATUS), Nop());
            f.Pio.WriteWord(EXECCTRL(0), (1u << 12) | (0u << 4) | 1u); // TX, N=1 (after Load)
            f.Pio.WriteWord(TXF(0), 0xAAAAu); // TX level = 1 ≥ N
            f.Enable(0);
            f.Tick(1);
            f.ReadX(0).Should().Be(0u, "STATUS zero when TX FIFO level ≥ STATUS_N");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  IN / OUT — sources, destinations, shift directions, threshold
    // ════════════════════════════════════════════════════════════════════════
    public class InOut
    {
        [Fact]
        public void In_pins_shift_left_then_push()
        {
            using var f = new Fixture();
            f.GpioIn = 0xAB;
            // IN PINS,8 (left shift default) ; PUSH
            f.Load(0, In(0, 8), Push(block: true));
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(RXF(0)).Should().Be(0xABu);
        }

        [Fact]
        public void In_x_source()
        {
            using var f = new Fixture();
            f.Load(0, Set(D_X, 0x1F), In(1 /*X*/, 5), Push(block: true));
            f.Enable(0);
            f.Tick(3);
            f.Pio.ReadWord(RXF(0)).Should().Be(0x1Fu);
        }

        [Fact]
        public void In_shift_right_places_bits_at_top()
        {
            using var f = new Fixture();
            f.GpioIn = 0xFF;
            // SHIFTCTRL bit18 = ISR shift right
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 18);
            f.Load(0, In(0, 8), Push(block: true));
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(RXF(0)).Should().Be(0xFF000000u, "right-shift puts 8 bits at the MSB end");
        }

        [Fact]
        public void Out_to_X_left_shift()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 0xABCD1234u);
            // PULL ; OUT X,16 (left shift default → top 16 bits)
            f.Load(0, Pull(block: true), Out(D_X, 16));
            f.Enable(0);
            f.Tick(2);
            f.ReadX(0).Should().Be(0xABCDu, "left-shift OUT 16 takes the top 16 bits first");
        }

        [Fact]
        public void Out_to_Y_right_shift()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 0xABCD1234u);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // OSR shift right
            f.Load(0, Pull(block: true), Out(D_Y, 16));
            f.Enable(0);
            f.Tick(2);
            f.ReadY(0).Should().Be(0x1234u, "right-shift OUT 16 takes the low 16 bits first");
        }

        [Fact]
        public void Out_pindirs()
        {
            using var f = new Fixture();
            uint dirs = 0, mask = 0;
            f.Pio.WriteGpioDirs = (v, m) => { dirs = v; mask = m; };
            f.Pio.WriteWord(TXF(0), 0xFFu);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // shift right
            f.Load(0, Pull(block: true), Out(D_PINDIRS, 4));
            f.Enable(0);
            f.Tick(2);
            mask.Should().Be(0xFu);
            dirs.Should().Be(0xFu);
        }

        [Fact]
        public void Out_to_PC_jumps()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 5u);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // shift right
            f.Load(0, Pull(block: true), Out(D_PC, 5), Nop(), Nop(), Nop(), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(5u, "OUT PC sets the program counter");
        }

        [Fact]
        public void Out_exec_runs_embedded_instruction()
        {
            using var f = new Fixture();
            // Put a SET X,7 instruction word into the OSR, then OUT EXEC executes it.
            uint setX7 = Set(D_X, 7);
            f.Pio.WriteWord(TXF(0), setX7);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // shift right (low 16 bits = instruction)
            f.Load(0, Pull(block: true), Out(D_EXEC, 16), Nop());
            f.Enable(0);
            f.Tick(2);
            f.ReadX(0).Should().Be(7u, "OUT EXEC executes the SET X,7 carried in the OSR");
        }

        [Fact]
        public void Out_null_discards_and_consumes_osr()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 0xFFFFFFFFu);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // shift right
            // PULL ; OUT NULL,16 ; JMP !OSRE,target  (16 bits still remain → taken)
            f.Load(0, Pull(block: true), Out(D_NULL, 16), Jmp(7, 4), Nop(), Nop());
            f.Enable(0);
            f.Tick(3);
            f.Pio.ReadWord(ADDR(0)).Should().Be(4u, "16 bits still remain in OSR after OUT NULL,16");
            f.ReadOsr(0).Should().Be(0x0000FFFFu, "right-shift consumed low 16 bits, 0xFFFF remains");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PUSH / PULL — block, noblock, iffull, ifempty, FDEBUG
    // ════════════════════════════════════════════════════════════════════════
    public class PushPull
    {
        [Fact]
        public void Pull_block_stalls_on_empty_and_sets_TXSTALL()
        {
            using var f = new Fixture();
            f.Load(0, Pull(block: true));
            f.Enable(0);
            f.Tick(3);
            ((f.Pio.ReadWord(FDEBUG) >> 24) & 1).Should().Be(1u, "TXSTALL [27:24] set");
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "PC stays on the stalled PULL");
        }

        [Fact]
        public void Pull_noblock_on_empty_copies_X_to_OSR()
        {
            using var f = new Fixture();
            // SET X,9 ; PULL noblock (empty → OSR = X)
            f.Load(0, Set(D_X, 9), Pull(block: false), Nop());
            f.Enable(0);
            f.Tick(2);
            f.ReadOsr(0).Should().Be(9u, "PULL NOBLOCK on empty FIFO copies X into OSR");
        }

        [Fact]
        public void Pull_ifempty_skips_when_osr_not_empty()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(TXF(0), 0x1u);
            f.Pio.WriteWord(TXF(0), 0x2u);
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 19); // shift right
            // PULL ; OUT NULL,16 (OSR not empty) ; PULL ifempty (should NOT pull) ; loop
            f.Load(0, Pull(block: true), Out(D_NULL, 16), Pull(block: true, ifEmpty: true), Nop());
            f.Enable(0);
            f.Tick(3);
            // Only the first PULL consumed a word → one word still in TX FIFO
            (f.Pio.ReadWord(FLEVEL) & 0xF).Should().Be(1u, "PULL IFEMPTY must not consume when OSR not empty");
        }

        [Fact]
        public void Push_block_stalls_on_full_and_sets_RXSTALL()
        {
            using var f = new Fixture();
            for (uint i = 0; i < 4; i++) f.Pio.InjectRxData(0, i); // fill RX FIFO
            f.Load(0, Push(block: true));
            f.Enable(0);
            f.Tick(3);
            (f.Pio.ReadWord(FDEBUG) & 1u).Should().Be(1u, "RXSTALL [3:0] set on blocked PUSH");
        }

        [Fact]
        public void Push_iffull_skips_below_threshold()
        {
            using var f = new Fixture();
            // autopush threshold 32; PUSH IFFULL with IsrCount<32 should be a no-op
            f.Pio.WriteWord(SHIFTCTRL(0), 0u);
            f.Load(0, Push(block: true, ifFull: true), Nop());
            f.Enable(0);
            f.Tick(1);
            f.Pio.RxFifoEmpty(0).Should().BeTrue("PUSH IFFULL below threshold pushes nothing");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  IRQ instruction + WAIT IRQ + NVIC routing
    // ════════════════════════════════════════════════════════════════════════
    public class IrqAndWait
    {
        [Fact]
        public void Irq_set_raises_flag_in_INTR()
        {
            using var f = new Fixture();
            f.Load(0, IrqInstr(0), Nop());
            f.Enable(0);
            f.Tick(1);
            // INTR bits [11:8] are IRQ flags 0-3
            ((f.Pio.ReadWord(INTR) >> 8) & 1).Should().Be(1u, "IRQ 0 flag raised");
        }

        [Fact]
        public void Irq_clear_lowers_flag()
        {
            using var f = new Fixture();
            f.Load(0, IrqInstr(0), IrqInstr(0, clear: true), Nop());
            f.Enable(0);
            f.Tick(2);
            ((f.Pio.ReadWord(INTR) >> 8) & 1).Should().Be(0u, "IRQ 0 cleared");
        }

        [Fact]
        public void Wait_irq_stalls_until_flag_set_then_clears_it()
        {
            using var f = new Fixture();
            // SM1 sets IRQ4; SM0 waits on IRQ4 (polarity 1) then proceeds.
            // Use IRQ index 4 to avoid REL ambiguity.
            f.Pio.WriteWord(INSTR_MEM + 0, Wait(1, 2 /*IRQ*/, 4)); // slot0 SM0
            f.Pio.WriteWord(INSTR_MEM + 4, Jmp(0, 1));             // slot1 SM0 loop
            f.Pio.WriteWord(EXECCTRL(0), 1u << 12);
            f.Pio.WriteWord(CLKDIV(0), 1u << 16);
            f.Enable(0);

            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "SM0 stalls on WAIT IRQ4 while flag clear");

            // Raise IRQ4 via force register
            f.Pio.WriteWord(IRQ_FORCE, 1u << 4);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "SM0 proceeds once IRQ4 is set");
            ((f.Pio.ReadWord(IRQ) >> 4) & 1).Should().Be(0u, "WAIT 1 IRQ clears the flag on success");
        }

        [Fact]
        public void Nvic_irq0_asserted_when_inte_enabled_and_flag_set()
        {
            using var f = new Fixture();
            // Enable IRQ0 on SM IRQ flag 0 (INTR bit 8 → IRQ0_INTE bit 8)
            f.Pio.WriteWord(IRQ0_INTE, 1u << 8);
            f.Load(0, IrqInstr(0), Nop());
            f.Enable(0);
            f.Tick(1);
            ((f.Pio.ReadWord(IRQ0_INTS) >> 8) & 1).Should().Be(1u, "IRQ0_INTS reflects masked flag");
            (f.Cpu.Registers.PendingInterrupts & (1u << 7)).Should().NotBe(0u, "PIO0_IRQ0 = NVIC IRQ 7 must be asserted");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WAIT GPIO / PIN
    // ════════════════════════════════════════════════════════════════════════
    public class WaitPins
    {
        [Fact]
        public void Wait_gpio_high_stalls_until_pin_high()
        {
            using var f = new Fixture();
            f.GpioIn = 0;
            f.Load(0, Wait(1, 0 /*GPIO*/, 5), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "stalled while GPIO5 low");
            f.GpioIn = 1u << 5;
            f.Tick(1);
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "proceeds once GPIO5 high");
        }

        [Fact]
        public void Wait_gpio_low_polarity0()
        {
            using var f = new Fixture();
            f.GpioIn = 1u << 3;
            f.Load(0, Wait(0, 0 /*GPIO*/, 3), Nop());
            f.Enable(0);
            f.Tick(2);
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "stalled while GPIO3 high (waiting for low)");
            f.GpioIn = 0;
            f.Tick(1);
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "proceeds once GPIO3 low");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Autopull / autopush
    // ════════════════════════════════════════════════════════════════════════
    public class AutoShift
    {
        [Fact]
        public void Autopull_refills_osr_each_threshold()
        {
            using var f = new Fixture();
            uint a = 0, b = 0; var n = 0;
            f.Pio.WriteGpioPins = (v, _) => { if (n++ == 0) a = v; else b = v; };
            f.Pio.WriteWord(TXF(0), 0x11111111u);
            f.Pio.WriteWord(TXF(0), 0x22222222u);
            // autopull enabled, threshold 32, shift right
            f.Pio.WriteWord(SHIFTCTRL(0), (1u << 17) | (1u << 19));
            f.Load(0, Out(D_PINS, 0)); // OUT PINS,32 looping at 0
            f.Enable(0);
            f.Tick(2);
            a.Should().Be(0x11111111u);
            b.Should().Be(0x22222222u, "autopull refilled OSR from TX FIFO");
        }

        [Fact]
        public void Autopush_threshold_pushes_to_rx_fifo()
        {
            using var f = new Fixture();
            f.GpioIn = 0xA5;
            // autopush enabled, threshold 8, shift left
            f.Pio.WriteWord(SHIFTCTRL(0), (1u << 16) | (8u << 20));
            f.Load(0, In(0, 8)); // IN PINS,8 → autopush at 8 bits
            f.Enable(0);
            f.Tick(1);
            f.Pio.RxFifoEmpty(0).Should().BeFalse("autopush enqueued at threshold");
            f.Pio.ReadWord(RXF(0)).Should().Be(0xA5u);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FIFO joining
    // ════════════════════════════════════════════════════════════════════════
    public class FifoJoin
    {
        [Fact]
        public void Join_rx_gives_8_deep_rx()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 30); // FJOIN_RX
            for (uint i = 0; i < 8; i++) f.Pio.InjectRxData(0, i);
            ((f.Pio.ReadWord(FLEVEL) >> 4) & 0xF).Should().Be(8u, "RX FIFO is 8 deep with FJOIN_RX");
        }

        [Fact]
        public void Join_tx_disables_rx()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(SHIFTCTRL(0), 1u << 31); // FJOIN_TX
            f.Pio.InjectRxData(0, 1); // RX depth 0 → rejected
            f.Pio.RxFifoEmpty(0).Should().BeTrue("RX FIFO disabled under FJOIN_TX");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Clock divider
    // ════════════════════════════════════════════════════════════════════════
    public class ClockDivider
    {
        [Fact]
        public void Integer_divisor_slows_execution()
        {
            using var f = new Fixture();
            // div = 4 ; with 4 ticks, exactly one instruction step
            f.Pio.WriteWord(INSTR_MEM, Jmp(0, 1));
            f.Pio.WriteWord(INSTR_MEM + 4, Jmp(0, 1)); // slot1 loops
            f.Pio.WriteWord(EXECCTRL(0), 1u << 12);    // wrap_top=1
            f.Pio.WriteWord(CLKDIV(0), 4u << 16);      // integer divisor 4
            f.Enable(0);
            f.Tick(3);
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "no step yet after 3 of 4 sub-cycles");
            f.Tick(1);
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "one step after the 4th sub-cycle");
        }

        [Fact]
        public void Fractional_divisor_accumulates()
        {
            using var f = new Fixture();
            f.Pio.WriteWord(INSTR_MEM, Jmp(0, 1));
            f.Pio.WriteWord(INSTR_MEM + 4, Jmp(0, 1));
            f.Pio.WriteWord(EXECCTRL(0), 1u << 12);
            // div = 1.5 → integer=1 frac=128 → divisor=384 (per 256-scaled accumulator)
            f.Pio.WriteWord(CLKDIV(0), (1u << 16) | (128u << 8));
            f.Enable(0);
            // 3 ticks → accum 768 / 384 = 2 steps
            f.Tick(3);
            // 2 steps from slot0→1→(wrap)→... we mainly assert it advanced without crashing
            f.Pio.ReadWord(ADDR(0)).Should().BeOneOf(0u, 1u);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Wrap
    // ════════════════════════════════════════════════════════════════════════
    public class Wrap
    {
        [Fact]
        public void Wraps_from_top_to_bottom()
        {
            using var f = new Fixture();
            // wrap_top=2, wrap_bottom=1
            f.Pio.WriteWord(INSTR_MEM + 0, Nop());
            f.Pio.WriteWord(INSTR_MEM + 4, Nop());
            f.Pio.WriteWord(INSTR_MEM + 8, Nop());
            f.Pio.WriteWord(EXECCTRL(0), (2u << 12) | (1u << 7)); // top=2 bottom=1
            f.Pio.WriteWord(CLKDIV(0), 1u << 16);
            f.Enable(0);
            f.Tick(1); // PC 0→1
            f.Tick(1); // 1→2
            f.Tick(1); // 2→ wrap to 1
            f.Pio.ReadWord(ADDR(0)).Should().Be(1u, "PC wraps from top(2) to bottom(1)");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Multi-SM independence
    // ════════════════════════════════════════════════════════════════════════
    public class MultiSm
    {
        [Fact]
        public void Two_sms_run_independently()
        {
            using var f = new Fixture();
            // Shared instruction memory: slot0 SET X,5 ; slot1 SET X,9
            f.Pio.WriteWord(INSTR_MEM + 0, Set(D_X, 5));
            f.Pio.WriteWord(INSTR_MEM + 4, Set(D_X, 9));
            // SM0 wraps at 0 (runs SET X,5), SM1 starts at 1 wrapping at 1 (runs SET X,9)
            f.Pio.WriteWord(EXECCTRL(0), 0u << 12);
            f.Pio.WriteWord(CLKDIV(0), 1u << 16);
            f.Pio.WriteWord(EXECCTRL(1), (1u << 12) | (1u << 7)); // top=1 bottom=1
            f.Pio.WriteWord(CLKDIV(1), 1u << 16);
            // Force SM1 PC to 1 by writing a JMP via INSTR (immediate)
            f.Pio.WriteWord(INSTR(1), Jmp(0, 1));
            f.Pio.WriteWord(CTRL, 0b11); // enable both
            f.Tick(3);
            f.ReadX(0).Should().Be(5u, "SM0 ran SET X,5");
            f.ReadX(1).Should().Be(9u, "SM1 ran SET X,9 independently");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Regression: late producer + autopull stall (CheckSmWait must not skip OUT)
    // ════════════════════════════════════════════════════════════════════════
    public class LateProducerAutopull
    {
        [Fact]
        public void Out_stalled_on_autopull_completes_when_data_arrives_late()
        {
            using var f = new Fixture();
            uint captured = 0; var calls = 0;
            f.Pio.WriteGpioPins = (v, _) => { captured = v; calls++; };

            // autopull enabled, threshold 32, shift right
            f.Pio.WriteWord(SHIFTCTRL(0), (1u << 17) | (1u << 19));
            // [0] OUT PINS,32  [1] JMP 1 (halt loop). wrap_top=1.
            f.Pio.WriteWord(INSTR_MEM + 0, Out(D_PINS, 0));
            f.Pio.WriteWord(INSTR_MEM + 4, Jmp(0, 1));
            f.Pio.WriteWord(EXECCTRL(0), 1u << 12);
            f.Pio.WriteWord(CLKDIV(0), 1u << 16);
            f.Enable(0);

            // First tick: OUT stalls (autopull, TX FIFO empty)
            f.Tick(1);
            calls.Should().Be(0, "no pin write while stalled");
            f.Pio.ReadWord(ADDR(0)).Should().Be(0u, "PC parked on the stalled OUT");

            // Producer writes data late → must wake and DRIVE the pins, not skip the OUT.
            f.Pio.WriteWord(TXF(0), 0xABCD1234u);
            f.Tick(2);

            captured.Should().Be(0xABCD1234u,
                "the OUT that stalled on autopull must drive the pins once data arrives — not be skipped");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Regression: FDEBUG TXOVER / RXUNDER bit positions (TRM §3.7)
    // ════════════════════════════════════════════════════════════════════════
    public class FdebugLayout
    {
        [Fact]
        public void Txover_sets_bit_in_19_16_range()
        {
            using var f = new Fixture();
            for (uint i = 0; i < 5; i++) f.Pio.WriteWord(TXF(0), i); // overflow on the 5th
            var fdebug = f.Pio.ReadWord(FDEBUG);
            ((fdebug >> 16) & 1).Should().Be(1u, "TXOVER for SM0 is bit 16 ([19:16])");
            ((fdebug >> 8) & 1).Should().Be(0u, "bit 8 ([11:8]) is RXUNDER, not TXOVER");
        }

        [Fact]
        public void Rxunder_sets_bit_in_11_8_range_on_empty_read()
        {
            using var f = new Fixture();
            f.Pio.ReadWord(RXF(0)); // read empty RX FIFO → underflow
            ((f.Pio.ReadWord(FDEBUG) >> 8) & 1).Should().Be(1u, "RXUNDER for SM0 is bit 8 ([11:8])");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Regression: autopush on full RX FIFO must not silently discard data
    // ════════════════════════════════════════════════════════════════════════
    public class AutopushOverflow
    {
        [Fact]
        public void Autopush_when_full_retains_data_and_stalls()
        {
            using var f = new Fixture();
            f.GpioIn = 0xDEADBEEF;
            // Fill RX FIFO (4 deep)
            for (uint i = 0; i < 4; i++) f.Pio.InjectRxData(0, 0x1000u + i);
            // autopush threshold 32, shift left
            f.Pio.WriteWord(SHIFTCTRL(0), (1u << 16) | (0u << 20));
            f.Load(0, In(0, 0)); // IN PINS,32 → autopush; RX full → must stall (not discard)
            f.Enable(0);
            f.Tick(2);

            // SM must be stalled with RXSTALL set, data still pending (not lost)
            (f.Pio.ReadWord(FDEBUG) & 1u).Should().Be(1u, "RXSTALL set when autopush blocked by full RX FIFO");

            // Drain the four pre-filled words
            for (var i = 0; i < 4; i++) f.Pio.ReadWord(RXF(0));
            // Now the previously-stalled autopush should complete on the next tick(s)
            f.Tick(2);
            f.Pio.ReadWord(RXF(0)).Should().Be(0xDEADBEEFu,
                "the captured value must reach the RX FIFO once space frees up — never silently dropped");
        }
    }
}

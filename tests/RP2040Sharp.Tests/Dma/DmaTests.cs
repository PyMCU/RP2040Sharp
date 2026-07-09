using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Dma;

namespace RP2040.Peripherals.Tests.Dma;

/// <summary>
/// Tests for the RP2040 DMA peripheral.
/// </summary>
public abstract class DmaTests
{
    private const uint CHANNEL_COUNT = 12;

    // Per-channel register offsets: base = ch * 0x40
    private static uint ChBase(int ch) => (uint)(ch * 0x40);
    private static uint READ_ADDR(int ch)    => ChBase(ch) + 0x00;
    private static uint WRITE_ADDR(int ch)   => ChBase(ch) + 0x04;
    private static uint TRANS_COUNT(int ch)  => ChBase(ch) + 0x08;
    private static uint CTRL_TRIG(int ch)    => ChBase(ch) + 0x0C;

    // System registers
    private const uint INTR      = 0x400;
    private const uint INTE0     = 0x404;
    private const uint INTF0     = 0x408;
    private const uint INTS0     = 0x40C;

    // CTRL bits
    private const uint CTRL_EN            = 1u << 0;
    private const uint CTRL_DATA_SIZE_WORD = 2u << 2;   // SIZE=2 (word = 4 bytes)
    private const uint CTRL_INCR_READ     = 1u << 4;
    private const uint CTRL_INCR_WRITE    = 1u << 5;
    private const uint CTRL_TREQ_PERMANENT = 0x3Fu << 15;  // TREQ_SEL=63 = always ready

    private sealed class Fixture : IDisposable
    {
        public BusInterconnect Bus { get; }
        public CortexM0Plus Cpu { get; }
        public DmaPeripheral Dma { get; }

        public Fixture()
        {
            Bus = new BusInterconnect();
            Cpu = new CortexM0Plus(Bus);
            Dma = new DmaPeripheral(Bus, Cpu);
        }

        public void Dispose() => Bus.Dispose();

        /// <summary>Write a 32-bit word to SRAM at the given address.</summary>
        public void WriteToSram(uint addr, uint value) =>
            Bus.WriteWord(addr, value);

        /// <summary>Read a 32-bit word from SRAM.</summary>
        public uint ReadFromSram(uint addr) =>
            Bus.ReadWord(addr);
    }

    public class BasicTransfer
    {
        [Fact]
        public void Single_word_transfer_copies_data()
        {
            using var f = new Fixture();

            // Write source data to SRAM
            f.WriteToSram(0x20000000u, 0x12345678u);

            // Configure ch0: read from 0x20000000, write to 0x20001000, 1 word
            f.Dma.WriteWord(READ_ADDR(0),   0x20000000u);
            f.Dma.WriteWord(WRITE_ADDR(0),  0x20001000u);
            f.Dma.WriteWord(TRANS_COUNT(0), 1u);
            f.Dma.WriteWord(CTRL_TRIG(0),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT);

            f.ReadFromSram(0x20001000u).Should().Be(0x12345678u);
        }

        [Fact]
        public void Multi_word_transfer_copies_block()
        {
            using var f = new Fixture();

            // Write 4 words to SRAM
            for (uint i = 0; i < 4; i++)
                f.WriteToSram(0x20002000u + i * 4, 0xAABBCC00u | i);

            f.Dma.WriteWord(READ_ADDR(0),   0x20002000u);
            f.Dma.WriteWord(WRITE_ADDR(0),  0x20003000u);
            f.Dma.WriteWord(TRANS_COUNT(0), 4u);
            f.Dma.WriteWord(CTRL_TRIG(0),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT);

            for (uint i = 0; i < 4; i++)
                f.ReadFromSram(0x20003000u + i * 4).Should().Be(0xAABBCC00u | i);
        }

        [Fact]
        public void TRANS_COUNT_reads_back_before_trigger()
        {
            using var f = new Fixture();
            f.Dma.WriteWord(TRANS_COUNT(1), 64u);
            f.Dma.ReadWord(TRANS_COUNT(1)).Should().Be(64u);
        }
    }

    public class Chaining
    {
        [Fact]
        public void Channel_chaining_triggers_second_channel()
        {
            using var f = new Fixture();

            // Source for ch0 and ch1
            f.WriteToSram(0x20004000u, 0xCAFEBABEu);
            f.WriteToSram(0x20005000u, 0xDEAD1234u);

            // Pre-configure ch1 via AL1 alias (offset +0x10 within ch block) — NO trigger
            const uint CH1_BASE = 1 * 0x40;
            f.Dma.WriteWord(CH1_BASE + 0x00, 0x20005000u);  // READ_ADDR
            f.Dma.WriteWord(CH1_BASE + 0x04, 0x20007000u);  // WRITE_ADDR
            f.Dma.WriteWord(CH1_BASE + 0x08, 1u);           // TRANS_COUNT
            // Write CTRL via AL1 alias (no trigger): ch1 base + 0x10
            // CHAIN_TO = ch1 (itself = disable chaining) → bits [14:11] = 1
            f.Dma.WriteWord(CH1_BASE + 0x10,
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT
                | (1u << 11));  // CHAIN_TO = 1 (self = no chain)

            // Trigger ch0 with CHAIN_TO=1 (bits [14:11] = 1 → 1 << 11)
            f.Dma.WriteWord(READ_ADDR(0),   0x20004000u);
            f.Dma.WriteWord(WRITE_ADDR(0),  0x20006000u);
            f.Dma.WriteWord(TRANS_COUNT(0), 1u);
            f.Dma.WriteWord(CTRL_TRIG(0),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT
                | (1u << 11));  // CHAIN_TO = 1

            // ch0 should have run and chained to ch1
            f.ReadFromSram(0x20006000u).Should().Be(0xCAFEBABEu, "ch0 data at dest");
            f.ReadFromSram(0x20007000u).Should().Be(0xDEAD1234u, "ch1 data at dest after chain");
        }
    }

    public class Interrupts
    {
        [Fact]
        public void INTR_bit_set_after_channel_completes()
        {
            using var f = new Fixture();
            f.WriteToSram(0x20008000u, 0xABCDEF01u);

            f.Dma.WriteWord(READ_ADDR(2),   0x20008000u);
            f.Dma.WriteWord(WRITE_ADDR(2),  0x20009000u);
            f.Dma.WriteWord(TRANS_COUNT(2), 1u);
            f.Dma.WriteWord(CTRL_TRIG(2),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT);

            (f.Dma.ReadWord(INTR) & (1u << 2)).Should().Be(1u << 2, "INTR bit2 should be set after ch2 completes");
        }

        [Fact]
        public void INTR_cleared_by_writing_1()
        {
            using var f = new Fixture();
            f.WriteToSram(0x2000A000u, 0u);

            f.Dma.WriteWord(READ_ADDR(3),   0x2000A000u);
            f.Dma.WriteWord(WRITE_ADDR(3),  0x2000B000u);
            f.Dma.WriteWord(TRANS_COUNT(3), 1u);
            f.Dma.WriteWord(CTRL_TRIG(3),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_TREQ_PERMANENT);

            f.Dma.WriteWord(INTR, 1u << 3);  // clear ch3 interrupt
            (f.Dma.ReadWord(INTR) & (1u << 3)).Should().Be(0u, "INTR bit should be cleared");
        }
    }

    public class HalfWordTransfer
    {
        [Fact]
        public void Half_word_transfer_copies_2_bytes()
        {
            using var f = new Fixture();
            // Write halfword to SRAM
            f.Bus.WriteHalfWord(0x2000C000u, (ushort)0xABCD);

            f.Dma.WriteWord(READ_ADDR(4),   0x2000C000u);
            f.Dma.WriteWord(WRITE_ADDR(4),  0x2000D000u);
            f.Dma.WriteWord(TRANS_COUNT(4), 1u);
            f.Dma.WriteWord(CTRL_TRIG(4),
                CTRL_EN | (1u << 2) /* SIZE=1 halfword */ | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT);

            f.Bus.ReadHalfWord(0x2000D000u).Should().Be((ushort)0xABCD);
        }
    }

    public class DreqHandshake
    {
        private const uint CTRL_DATA_SIZE_BYTE = 0u;  // SIZE=0 (byte)

        [Fact]
        public void DREQ_gated_transfer_executes_only_ready_beats()
        {
            using var f = new Fixture();

            // Write 3 bytes to SRAM (source)
            f.Bus.WriteByte(0x20010000u, 0xAA);
            f.Bus.WriteByte(0x20010001u, 0xBB);
            f.Bus.WriteByte(0x20010002u, 0xCC);

            // Register DREQ index 1 that is ready only 2 times
            var readyCount = 2;
            f.Dma.RegisterDreq(1, () => readyCount-- > 0);

            var ctrl = CTRL_EN | CTRL_DATA_SIZE_BYTE | CTRL_INCR_READ | CTRL_INCR_WRITE
                       | (1u << 15);  // TREQ_SEL=1

            f.Dma.WriteWord(READ_ADDR(5),   0x20010000u);
            f.Dma.WriteWord(WRITE_ADDR(5),  0x20011000u);
            f.Dma.WriteWord(TRANS_COUNT(5), 3u);
            f.Dma.WriteWord(CTRL_TRIG(5),   ctrl);

            // Only 2 beats should have executed (DREQ was ready twice)
            f.Bus.ReadByte(0x20011000u).Should().Be(0xAA);
            f.Bus.ReadByte(0x20011001u).Should().Be(0xBB);
            // Third byte not transferred (DREQ was not ready)
            f.Bus.ReadByte(0x20011002u).Should().Be(0x00);
            // Channel should still be BUSY and TRANS_COUNT should be 1
            (f.Dma.ReadWord(CTRL_TRIG(5)) & (1u << 24)).Should().Be(1u << 24, "channel still BUSY");
            f.Dma.ReadWord(TRANS_COUNT(5)).Should().Be(1u, "1 transfer remaining");
        }

        [Fact]
        public void DREQ_not_ready_transfers_zero_beats()
        {
            using var f = new Fixture();
            f.Bus.WriteWord(0x20012000u, 0x55555555u);

            // DREQ always not ready
            f.Dma.RegisterDreq(2, () => false);

            var ctrl = CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE
                       | (2u << 15);  // TREQ_SEL=2

            f.Dma.WriteWord(READ_ADDR(6),   0x20012000u);
            f.Dma.WriteWord(WRITE_ADDR(6),  0x20013000u);
            f.Dma.WriteWord(TRANS_COUNT(6), 1u);
            f.Dma.WriteWord(CTRL_TRIG(6),   ctrl);

            // No transfer should have occurred
            f.Bus.ReadWord(0x20013000u).Should().Be(0u);
            f.Dma.ReadWord(TRANS_COUNT(6)).Should().Be(1u, "no beats consumed");
        }

        [Fact]
        public void TREQ_PERMANENT_ignores_dreq_registration()
        {
            using var f = new Fixture();
            f.Bus.WriteWord(0x20014000u, 0x12345678u);

            // Even if we tried to register DREQ 63, it can't be registered
            // (RegisterDreq throws on index 63). So PERMANENT always works.
            f.Dma.WriteWord(READ_ADDR(7),   0x20014000u);
            f.Dma.WriteWord(WRITE_ADDR(7),  0x20015000u);
            f.Dma.WriteWord(TRANS_COUNT(7), 1u);
            f.Dma.WriteWord(CTRL_TRIG(7),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_READ | CTRL_INCR_WRITE | CTRL_TREQ_PERMANENT);

            f.Bus.ReadWord(0x20015000u).Should().Be(0x12345678u);
        }
    }

    /// <summary>
    /// Regression for the PIO RX DREQ → DMA pacing fix: injecting a word into a state machine's RX FIFO
    /// must fire <c>OnRxPush</c> — the only trigger for <c>Dma.ResumeDreq</c> (wired in RP2040Machine).
    /// Before the fix InjectRxData enqueued silently, so a DREQ-paced DMA channel never woke and stalled
    /// BUSY forever. (Full end-to-end FIFO→DMA drain is exercised by the nanoFramework harness, which
    /// runs a real PIO program that configures the SM — a bare machine leaves the RX FIFO unconfigured.)
    /// </summary>
    public class PioRxDreqPacing
    {
        [Fact]
        public void InjectRxData_fires_OnRxPush_to_wake_a_paced_dma()
        {
            using var m = new RP2040Machine();
            var pushes = new System.Collections.Generic.List<(int sm, uint val)>();
            m.Pio0.OnRxPush += (sm, v) => pushes.Add((sm, v));

            m.Pio0.InjectRxData(0, 0xCAFEBABEu);

            pushes.Should().ContainSingle();
            pushes[0].sm.Should().Be(0);
            pushes[0].val.Should().Be(0xCAFEBABEu);
        }

        [Fact]
        public void Injected_rx_words_drive_a_dreq_paced_channel_to_completion()
        {
            using var m = new RP2040Machine();
            m.Pio0.InReset = false;   // firmware releases the PIO via RESETS; do the same so RXF reads work
            const int sm = 0;
            const uint pio0Rxf0 = 0x50200020u;   // PIO0 RXF0 — a read dequeues that SM's RX FIFO
            const uint dst = 0x20001000u;
            uint[] src = { 0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u, 0x55555555u };

            // ch0: read PIO0 RXF0 (fixed), write dst (incrementing), paced by PIO0 RX SM0 DREQ (index 4).
            m.Dma.WriteWord(READ_ADDR(0),   pio0Rxf0);
            m.Dma.WriteWord(WRITE_ADDR(0),  dst);
            m.Dma.WriteWord(TRANS_COUNT(0), (uint)src.Length);
            m.Dma.WriteWord(CTRL_TRIG(0),
                CTRL_EN | CTRL_DATA_SIZE_WORD | CTRL_INCR_WRITE | (4u << 15));   // TREQ_SEL = 4

            // Each inject fires OnRxPush → ResumeDreq → one synchronous DMA beat reads the word out.
            foreach (var w in src)
            {
                m.Pio0.InjectRxData(sm, w);
            }

            for (uint i = 0; i < src.Length; i++)
            {
                m.Bus.ReadWord(dst + i * 4).Should().Be(src[i]);
            }
        }
    }
}


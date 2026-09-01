using RP2040.Peripherals.Uart;

namespace RP2040.Peripherals.Tests.Uart;

/// <summary>
/// Tests for the PL011 UART peripheral.
/// </summary>
public abstract class UartTests
{
    private const uint UARTDR       = 0x000;
    private const uint UARTFR       = 0x018;
    private const uint UARTIBRD     = 0x024;
    private const uint UARTFBRD     = 0x028;
    private const uint UARTLCR_H    = 0x02C;
    private const uint UARTCR       = 0x030;
    private const uint UARTIFLS     = 0x034;
    private const uint UARTIMSC     = 0x038;
    private const uint UARTRIS      = 0x03C;
    private const uint UARTMIS      = 0x040;
    private const uint UARTICR      = 0x044;
    private const uint UARTDMACR    = 0x048;

    // PL011 ID registers
    private const uint UARTPERIPHID0 = 0xFE0;
    private const uint UARTPERIPHID1 = 0xFE4;
    private const uint UARTPERIPHID2 = 0xFE8;
    private const uint UARTPERIPHID3 = 0xFEC;
    private const uint UARTPCELLID0  = 0xFF0;
    private const uint UARTPCELLID1  = 0xFF4;
    private const uint UARTPCELLID2  = 0xFF8;
    private const uint UARTPCELLID3  = 0xFFC;

    // UARTFR bits
    private const uint FR_TXFE = 1u << 7;
    private const uint FR_RXFE = 1u << 4;

    public class BaudRate
    {
        [Fact]
        public void Write_IBRD_reads_back_correctly()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTIBRD, 67);
            uart.ReadWord(UARTIBRD).Should().Be(67u);
        }

        [Fact]
        public void Write_FBRD_reads_back_correctly()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTFBRD, 52);
            uart.ReadWord(UARTFBRD).Should().Be(52u);
        }

        [Fact]
        public void FBRD_masked_to_6_bits()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTFBRD, 0xFFFF);
            uart.ReadWord(UARTFBRD).Should().Be(0x3Fu, "FBRD is only 6 bits wide");
        }

        [Fact]
        public void IBRD_masked_to_16_bits()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTIBRD, 0x1FFFF);
            uart.ReadWord(UARTIBRD).Should().Be(0xFFFFu);
        }
    }

    public class LineControl
    {
        [Fact]
        public void UARTLCR_H_stores_word_length_bits()
        {
            var uart = new UartPeripheral();
            // WLEN = 0b11 (8-bit data) → bits [6:5] = 0b11 → value = 0b01100000 = 0x60
            uart.WriteWord(UARTLCR_H, 0x60);
            uart.ReadWord(UARTLCR_H).Should().Be(0x60u);
        }

        [Fact]
        public void UARTLCR_H_stores_FEN_bit()
        {
            var uart = new UartPeripheral();
            // FEN = bit 4 → enable FIFOs
            uart.WriteWord(UARTLCR_H, 0x70);  // FEN + WLEN=8bit
            uart.ReadWord(UARTLCR_H).Should().Be(0x70u);
        }

        [Fact]
        public void UARTLCR_H_masked_to_8_bits()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTLCR_H, 0x1FF);
            uart.ReadWord(UARTLCR_H).Should().Be(0xFFu);
        }
    }

    public class Transmit
    {
        [Fact]
        public void TX_invokes_OnByteTransmit_callback()
        {
            var uart = new UartPeripheral();
            byte? received = null;
            uart.OnByteTransmit = b => received = b;

            uart.WriteWord(UARTDR, 0x42);

            received.Should().Be(0x42);
        }

        [Fact]
        public void FR_TXFE_is_set_when_TX_is_idle()
        {
            var uart = new UartPeripheral();
            var fr = uart.ReadWord(UARTFR);
            (fr & FR_TXFE).Should().Be(FR_TXFE, "TX FIFO empty flag should be set");
        }

        [Fact]
        public void TX_sets_TXRIS_raw_interrupt()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTDR, 0x55);
            var ris = uart.ReadWord(UARTRIS);
            (ris & (1u << 5)).Should().Be(1u << 5, "TXRIS should be set after TX");
        }
    }

    public class Receive
    {
        [Fact]
        public void FR_RXFE_is_set_when_FIFO_empty()
        {
            var uart = new UartPeripheral();
            var fr = uart.ReadWord(UARTFR);
            (fr & FR_RXFE).Should().Be(FR_RXFE);
        }

        [Fact]
        public void InjectByte_populates_FIFO()
        {
            var uart = new UartPeripheral();
            uart.InjectByte(0xAB);

            var fr = uart.ReadWord(UARTFR);
            (fr & FR_RXFE).Should().Be(0u, "RXFE should be clear when FIFO has data");
        }

        [Fact]
        public void Reading_UARTDR_drains_RX_FIFO()
        {
            var uart = new UartPeripheral();
            uart.InjectByte(0xCD);
            uart.InjectByte(0xEF);

            uart.ReadWord(UARTDR).Should().Be(0xCDu);
            uart.ReadWord(UARTDR).Should().Be(0xEFu);
            (uart.ReadWord(UARTFR) & FR_RXFE).Should().Be(FR_RXFE, "FIFO empty after draining");
        }

        [Fact]
        public void RX_sets_RXRIS_raw_interrupt()
        {
            var uart = new UartPeripheral();
            uart.InjectByte(0x01);
            (uart.ReadWord(UARTRIS) & (1u << 4)).Should().Be(1u << 4, "RXRIS should be set");
        }
    }

    public class Interrupts
    {
        [Fact]
        public void MIS_is_RIS_AND_IMSC()
        {
            var uart = new UartPeripheral();
            uart.InjectByte(0x01);   // sets RXRIS
            uart.WriteWord(UARTIMSC, 0x10);  // unmask RXIM only

            var mis = uart.ReadWord(UARTMIS);
            (mis & 0x10).Should().Be(0x10u, "RXMIS should be set when RXRIS and RXIM both set");
        }

        [Fact]
        public void UARTICR_clears_selected_interrupts()
        {
            var uart = new UartPeripheral();
            uart.InjectByte(0x01);
            // Verify both RXRIS and assert TX is clear initially
            (uart.ReadWord(UARTRIS) & (1u << 4)).Should().Be(1u << 4, "RXRIS should be set");

            uart.WriteWord(UARTICR, 1u << 4);  // clear RXRIS
            (uart.ReadWord(UARTRIS) & (1u << 4)).Should().Be(0u, "RXRIS should be cleared");
        }
    }

    public class PeripheralId
    {
        [Fact]
        public void PL011_peripheral_id_registers_return_correct_values()
        {
            var uart = new UartPeripheral();
            uart.ReadWord(UARTPERIPHID0).Should().Be(0x11u);
            uart.ReadWord(UARTPERIPHID1).Should().Be(0x10u);
            uart.ReadWord(UARTPERIPHID2).Should().Be(0x34u);
            uart.ReadWord(UARTPERIPHID3).Should().Be(0x00u);
        }

        [Fact]
        public void PL011_cell_id_registers_return_correct_values()
        {
            var uart = new UartPeripheral();
            uart.ReadWord(UARTPCELLID0).Should().Be(0x0Du);
            uart.ReadWord(UARTPCELLID1).Should().Be(0xF0u);
            uart.ReadWord(UARTPCELLID2).Should().Be(0x05u);
            uart.ReadWord(UARTPCELLID3).Should().Be(0xB1u);
        }
    }

    public class DmaControl
    {
        [Fact]
        public void UARTDMACR_stores_and_reads_back()
        {
            var uart = new UartPeripheral();
            uart.WriteWord(UARTDMACR, 0x3);
            uart.ReadWord(UARTDMACR).Should().Be(0x3u);
        }
    }

    /// <summary>
    /// Regression for the sub-word write path: UARTDR is a FIFO whose read pops the RX side, so a
    /// read-modify-write would swallow a received byte on every 8-bit DMA beat.
    /// </summary>
    public class SubWordDataRegisterWrites
    {
        [Fact]
        public void Byte_write_to_the_data_register_does_not_consume_a_received_byte()
        {
            byte sent = 0;
            var uart = new UartPeripheral { OnByteTransmit = b => sent = b };
            uart.InjectByte(0xA5);

            uart.WriteByte(UARTDR, 0x11);   // the DMA_SIZE_8 beat a UART TX channel issues

            sent.Should().Be((byte)0x11, "the byte still goes out");
            uart.ReadWord(UARTDR).Should().Be(0xA5, "the received byte survives the sub-word write");
        }
    }
}

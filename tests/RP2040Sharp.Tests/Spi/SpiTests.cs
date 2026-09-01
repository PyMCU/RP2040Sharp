using RP2040.Peripherals.Spi;

namespace RP2040.Peripherals.Tests.Spi;

/// <summary>
/// Tests for the PL022 SPI peripheral's data-register access paths.
/// </summary>
public abstract class SpiTests
{
    private const uint SSPCR1 = 0x004, SSPDR = 0x008;
    private const uint CR1_SSE = 1u << 1;   // SSP enable

    /// <summary>
    /// Regression for the sub-word write path: SSPDR is a FIFO whose read pops the RX side, so a
    /// read-modify-write would eat the word received by the previous beat. Every DMA-driven SPI
    /// transfer uses 8-bit (or 16-bit) beats, so this is the normal path, not a corner case.
    /// </summary>
    public class SubWordDataRegisterWrites
    {
        [Theory]
        [InlineData(true)]      // 8-bit write — the DMA_SIZE_8 beat MicroPython's spi.write() uses
        [InlineData(false)]     // 16-bit write — the DMA_SIZE_16 beat a 16-bit DSS transfer uses
        public void Sub_word_write_to_the_data_register_does_not_consume_a_received_word(bool byteWide)
        {
            var spi = new SpiPeripheral();
            spi.WriteWord(SSPCR1, CR1_SSE);
            spi.InjectByte(0xA5);

            if (byteWide) spi.WriteByte(SSPDR, 0x11);
            else          spi.WriteHalfWord(SSPDR, 0x11);

            spi.ReadWord(SSPDR).Should().Be(0xA5, "the injected word survives a sub-word write to SSPDR");
        }
    }
}

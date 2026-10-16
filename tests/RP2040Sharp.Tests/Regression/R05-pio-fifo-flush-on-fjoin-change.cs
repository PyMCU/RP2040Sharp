using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R05-pio-fifo-flush-on-fjoin-change: found by running arduino-pico firmware on the emulator.</summary>
public class R05_pio_fifo_flush_on_fjoin_change
{
    [Fact]
    public void Pio_fifos_are_flushed_when_fjoin_changes()
    {
        var pio = new PioPeripheral(null!, 0);
        const uint FSTAT = 0x004, TXF0 = 0x010, SM0_SHIFTCTRL = 0x0C8 + 0x08;
        pio.WriteWord(TXF0, 1); pio.WriteWord(TXF0, 2); pio.WriteWord(TXF0, 3);
        (pio.ReadWord(FSTAT) & (1u << 24)).Should().Be(0, "TX FIFO 0 holds three words");
        pio.WriteWord(SM0_SHIFTCTRL, pio.ReadWord(SM0_SHIFTCTRL) ^ (1u << 31));     // toggle FJOIN_RX twice, as pio_sm_clear_fifos does
        pio.WriteWord(SM0_SHIFTCTRL, pio.ReadWord(SM0_SHIFTCTRL) ^ (1u << 31));
        (pio.ReadWord(FSTAT) & (1u << 24)).Should().NotBe(0, "toggling FJOIN flushes the FIFOs");
    }
}

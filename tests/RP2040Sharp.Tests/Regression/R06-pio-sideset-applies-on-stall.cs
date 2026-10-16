using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R06-pio-sideset-applies-on-stall: found by running arduino-pico firmware on the emulator.</summary>
public class R06_pio_sideset_applies_on_stall
{
    [Fact]
    public void Pio_sideset_is_applied_while_the_instruction_stalls()
    {
        // `pull block side 1` on an empty TX FIFO: the UART-TX idiom that holds the line high while idle
        // (pico-examples uart_tx.pio: "pull side 1 [7] ; Assert stop bit, or stall with line in idle state").
        var bus = new BusInterconnect();
        var pio = new PioPeripheral(new CortexM0Plus(bus), 0);
        uint lastValue = 0, lastMask = 0;
        pio.WriteGpioPins = (v, m) => { lastValue = v; lastMask = m; };
        const uint CTRL = 0x000, INSTR_MEM0 = 0x048, SM0_CLKDIV = 0x0C8, SM0_PINCTRL = 0x0C8 + 0x14;
        pio.WriteWord(0x0C8 + 0x04, 0u);                              // EXECCTRL: wrap 0..0, no SIDE_EN
        pio.WriteWord(SM0_CLKDIV, 1u << 16);
        pio.WriteWord(SM0_PINCTRL, (1u << 29) /* SIDESET_COUNT=1 */ | (0u << 10) /* SIDESET_BASE=0 */);
        pio.WriteWord(INSTR_MEM0, 0x80A0 | (1 << 12));                // pull block, side-set value 1
        pio.WriteWord(CTRL, 1);                                       // enable SM0
        pio.Tick(5);                                                  // the FIFO is empty: the SM stalls on the pull every cycle
        (lastMask & 1).Should().Be(1u, "side-set fires on the stalled instruction's first cycle");
        (lastValue & 1).Should().Be(1u);
    }
}

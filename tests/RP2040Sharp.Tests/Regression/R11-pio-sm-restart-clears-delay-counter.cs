using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R11: SM_RESTART must clear the delay counter.</summary>
public class R11_pio_sm_restart_clears_delay_counter
{
    [Fact]
    public void Sm_restart_clears_the_delay_counter()
    {
        var pio = new PioPeripheral(new CortexM0Plus(new BusInterconnect()), 0);
        const uint CTRL = 0x000, INSTR_MEM0 = 0x048, SM0_CLKDIV = 0x0C8, SM0_EXECCTRL = 0x0C8 + 0x04, SM0_ADDR = 0x0C8 + 0x0C;
        pio.WriteWord(INSTR_MEM0, 0xA042 | (31 << 8));                       // mov y, y [31]
        pio.WriteWord(INSTR_MEM0 + 4, 0x0000);                               // jmp 0
        pio.WriteWord(SM0_EXECCTRL, (1u << 12));                             // wrap 0..1
        pio.WriteWord(SM0_CLKDIV, 1u << 16);
        pio.WriteWord(CTRL, 1);
        pio.Tick(1);                                                         // executes instruction 0: 31 delay cycles pending
        pio.WriteWord(CTRL, 0);
        pio.WriteWord(CTRL, 1u << 4);                                        // SM_RESTART: clears the delay counter, PC back to the wrap bottom
        pio.WriteWord(CTRL, 1);
        pio.Tick(1);
        pio.ReadWord(SM0_ADDR).Should().Be(1u, "after a restart the first cycle executes the program, it does not burn the old delay");
    }
}

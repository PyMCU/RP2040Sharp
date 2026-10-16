using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R07-watchdog-reset-not-wired: found by running arduino-pico firmware on the emulator.</summary>
public class R07_watchdog_reset_not_wired
{
    // Park core 0 in a `b .` loop in SRAM so Machine.Run() ticks the peripherals with real cycle deltas.
    private static void ParkInLoop(RP2040Machine m)
    {
        m.Bus.WriteHalfWord(0x20000000, 0xE7FE);
        m.Cpu.Registers.PC = 0x20000000;
        m.Cpu.Registers.SP = 0x20001000;
    }

    [Fact]
    public void Watchdog_trigger_resets_the_cores_to_the_boot_vector()
    {
        using var m = new RP2040Machine();
        var bootPc = m.Bus.ReadWord(4) & ~1u;
        ParkInLoop(m);
        m.Run(10);
        m.Cpu.Registers.PC.Should().Be(0x20000000u);
        m.Bus.WriteWord(0x40058000, 1u << 31);                 // WATCHDOG.CTRL.TRIGGER
        m.Run(1);
        m.Cpu.Registers.PC.Should().BeInRange(bootPc, bootPc + 0x40, "the watchdog reset restarts core 0 from the BootROM");
    }
}

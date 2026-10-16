using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R01-nvic-irq-26-31: found by running arduino-pico firmware on the emulator.</summary>
public class R01_nvic_irq_26_31
{
    // Park core 0 in a `b .` loop in SRAM so Machine.Run() ticks the peripherals with real cycle deltas.
    private static void ParkInLoop(RP2040Machine m)
    {
        m.Bus.WriteHalfWord(0x20000000, 0xE7FE);
        m.Cpu.Registers.PC = 0x20000000;
        m.Cpu.Registers.SP = 0x20001000;
    }

    [Theory]
    [InlineData(26)] [InlineData(28)] [InlineData(31)]
    public void Nvic_user_irq_can_be_software_pended_and_is_taken(int irq)
    {
        using var m = new RP2040Machine();
        ParkInLoop(m);
        m.Bus.WriteHalfWord(0x20000200, 0xE7FE);                          // handler: b .
        m.Bus.WriteWord(0xE000ED08, 0x20000100);                          // VTOR
        m.Bus.WriteWord(0x20000100 + (uint)(16 + irq) * 4, 0x20000201);   // vector[16+irq] -> handler (thumb)
        m.Bus.WriteWord(0xE000E100, 1u << irq);                           // ISER
        m.Bus.WriteWord(0xE000E200, 1u << irq);                           // ISPR  (irq_set_pending)
        m.Run(20);
        m.Cpu.Registers.IPSR.Should().Be((uint)(16 + irq), "a pended+enabled NVIC line 26-31 must preempt thread mode");
    }
}

using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R03-dma-rx-dreq-resume: found by running arduino-pico firmware on the emulator.</summary>
public class R03_dma_rx_dreq_resume
{
    // Park core 0 in a `b .` loop in SRAM so Machine.Run() ticks the peripherals with real cycle deltas.
    private static void ParkInLoop(RP2040Machine m)
    {
        m.Bus.WriteHalfWord(0x20000000, 0xE7FE);
        m.Cpu.Registers.PC = 0x20000000;
        m.Cpu.Registers.SP = 0x20001000;
    }

    [Fact]
    public void Dma_paced_by_uart_rx_dreq_resumes_when_bytes_arrive()
    {
        using var m = new RP2040Machine();
        ParkInLoop(m);
        m.Bus.WriteWord(0x50000000 + 0x00, 0x40034000);        // CH0 READ_ADDR  = UART0.DR
        m.Bus.WriteWord(0x50000000 + 0x04, 0x20002000);        // CH0 WRITE_ADDR
        m.Bus.WriteWord(0x50000000 + 0x08, 4);                 // CH0 TRANS_COUNT
        m.Bus.WriteWord(0x50000000 + 0x0C, 1u | (0u << 2) | (1u << 5) | (21u << 15));   // EN, byte, incr write, TREQ=UART0_RX
        m.Bus.ReadWord(0x50000008).Should().Be(4u, "nothing received yet: the channel waits on the DREQ");
        foreach (var b in new byte[] { 1, 2, 3, 4 }) m.Uart0.InjectByte(b);
        m.Bus.ReadWord(0x50000008).Should().Be(0u, "each received byte must let the stalled channel move one more beat");
        m.Bus.ReadByte(0x20002003).Should().Be(4);
    }
}

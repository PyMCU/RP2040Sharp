using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R04-adc-not-ticked-and-sample-rate: found by running arduino-pico firmware on the emulator.</summary>
public class R04_adc_not_ticked_and_sample_rate
{
    // Park core 0 in a `b .` loop in SRAM so Machine.Run() ticks the peripherals with real cycle deltas.
    private static void ParkInLoop(RP2040Machine m)
    {
        m.Bus.WriteHalfWord(0x20000000, 0xE7FE);
        m.Cpu.Registers.PC = 0x20000000;
        m.Cpu.Registers.SP = 0x20001000;
    }

    private static long RunCycles(RP2040Machine m, long cycles)
    {
        long done = 0;
        while (done < cycles) { m.Run(200); done += Math.Max(m.LastElapsedCycles, 1); }
        return done;
    }

    [Fact]
    public void Adc_start_many_converts_at_one_sample_per_1_plus_div_adc_clocks()
    {
        using var m = new RP2040Machine();
        ParkInLoop(m);
        int conversions = 0;
        m.Adc.ReadChannel = _ => { conversions++; return 0; };
        m.Bus.WriteWord(0x4004C010, 5999u << 8);               // DIV: 48 MHz / 6000 = 8 kS/s
        m.Bus.WriteWord(0x4004C008, 1);                        // FCS.EN
        m.Bus.WriteWord(0x4004C000, 1 | (1u << 3));            // CS: EN | START_MANY
        RunCycles(m, 1_250_000);                               // 10 ms
        conversions.Should().BeInRange(78, 82, "8 kS/s for 10 ms is 80 samples (not 0.8, and not never ticked)");
    }
}

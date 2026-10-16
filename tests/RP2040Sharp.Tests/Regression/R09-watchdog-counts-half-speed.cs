using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R09-watchdog-counts-half-speed: found by running arduino-pico firmware on the emulator.</summary>
public class R09_watchdog_counts_half_speed
{
    [Fact]
    public void Watchdog_counts_down_two_ticks_per_microsecond_RP2040_E1()
    {
        // pico-sdk loads delay_ms * 2000 (RP2040-E1: the counter decrements twice per 1 MHz tick).
        int resets = 0;
        var wdg = new WatchdogPeripheral { OnReset = () => resets++ };
        wdg.WriteWord(0x04, 4000);                             // LOAD: 2 ms
        wdg.WriteWord(0x00, 1u << 30);                         // CTRL.ENABLE
        wdg.Tick(125_000);                                     // 1 ms
        resets.Should().Be(0);
        wdg.Tick(125_000 + 5_000);                             // 2.04 ms in total
        resets.Should().Be(1, "a 2 ms watchdog (load 4000) must fire after ~2 ms, not ~4 ms");
    }
}

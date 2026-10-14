using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R10-sio-per-core: found by running arduino-pico firmware on the emulator.</summary>
public class R10_sio_per_core
{
    [Fact]
    public void Sio_divider_state_is_per_core()
    {
        var bus = new BusInterconnect();
        var cpu = new CortexM0Plus(bus);
        var sio = new SioPeripheral(cpu);
        int core = 0;
        sio.GetActiveCoreId = () => core;
        const uint UDIVIDEND = 0x060, UDIVISOR = 0x064, QUOTIENT = 0x070, REMAINDER = 0x074;
        sio.WriteWord(UDIVIDEND, 100); sio.WriteWord(UDIVISOR, 7);      // core 0 starts 100 / 7
        core = 1;
        sio.WriteWord(UDIVIDEND, 9999); sio.WriteWord(UDIVISOR, 10);    // core 1's divider is a different instance
        sio.ReadWord(QUOTIENT).Should().Be(999u);
        core = 0;
        sio.ReadWord(QUOTIENT).Should().Be(14u, "core 0 must still read its own quotient");
        sio.ReadWord(REMAINDER).Should().Be(2u);
    }


    [Fact]
    public void Sio_interpolator_state_is_per_core()
    {
        var bus = new BusInterconnect();
        var sio = new SioPeripheral(new CortexM0Plus(bus));
        int core = 0;
        sio.GetActiveCoreId = () => core;
        const uint INTERP0_ACCUM0 = 0x080;
        sio.WriteWord(INTERP0_ACCUM0, 111);
        core = 1;
        sio.WriteWord(INTERP0_ACCUM0, 222);
        sio.ReadWord(INTERP0_ACCUM0).Should().Be(222u);
        core = 0;
        sio.ReadWord(INTERP0_ACCUM0).Should().Be(111u, "each core has its own INTERP0/INTERP1");
    }
}

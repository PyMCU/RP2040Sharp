using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Regression;

public sealed class R15_resets_survive_system_reset : MachineTestBase
{
    private const uint RESETS_RESET = 0x4000C000;

    [Fact]
    public void System_reset_holds_every_block_in_reset()
    {
        Machine.Bus.WriteWord(RESETS_RESET, 0);
        Machine.Bus.WriteWord(0x40034030, 0x301);
        Machine.Bus.WriteWord(0x40014004, 5);

        Machine.SystemReset();

        Machine.Bus.ReadWord(RESETS_RESET).Should().Be(0x01FFFFFFu, "RESETS is in the watchdog's PSM domain and comes back all-asserted");
        Machine.Bus.ReadWord(0x4000C008).Should().Be(0u, "RESET_DONE");
        Machine.Uart0.ReadWord(0x030).Should().Be(0u, "UART0 CR is back at its reset value");
        Machine.IoBank0.ReadWord(0x004).Should().Be(0x1Fu, "GPIO0_CTRL is back at its reset value");
    }
}

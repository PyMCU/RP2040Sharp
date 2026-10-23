using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Regression;

public sealed class R12_gpio_state_survives_reset : MachineTestBase
{
    private const uint RESETS_RESET = 0x4000C000;

    private void Pulse(int bit)
    {
        var r = Machine.Bus.ReadWord(RESETS_RESET);
        Machine.Bus.WriteWord(RESETS_RESET, r | (1u << bit));
        Machine.Bus.WriteWord(RESETS_RESET, r & ~(1u << bit));
    }

    [Fact]
    public void IoBank0_reset_restores_funcsel_overrides_and_interrupt_enables()
    {
        Machine.Bus.WriteWord(0xD0000024, 1u << 15);
        Machine.Bus.WriteWord(0xD0000014, 1u << 15);
        Machine.Bus.WriteWord(0x40014000 + 15 * 8 + 4, 5 | (3u << 8));
        Machine.Bus.WriteWord(0x40014104, 0xF0_0000);
        Machine.IoBank0.GetPadOutputEnable(15).Should().BeTrue();

        Pulse(5);

        Machine.Bus.ReadWord(0x40014000 + 15 * 8 + 4).Should().Be(0x1Fu);
        Machine.Bus.ReadWord(0x40014104).Should().Be(0u);
        (Machine.Bus.ReadWord(0x400140F4) & 0xCCCCCCCCu).Should().Be(0u, "no edge is latched by the reset itself");
        Machine.IoBank0.GetPadOutputEnable(15).Should().BeFalse();
    }

    [Fact]
    public void IoBank0_reset_keeps_host_inputs_and_pad_listeners()
    {
        var changes = new List<int>();
        Machine.IoBank0.PadChanged += changes.Add;
        Machine.IoBank0.SetExternalInput(24, true);
        Machine.Bus.WriteWord(0xD0000024, 1u << 23);
        Machine.Bus.WriteWord(0xD0000014, 1u << 23);
        Machine.Bus.WriteWord(0x40014000 + 23 * 8 + 4, 5);
        changes.Clear();

        Pulse(5);

        changes.Should().Contain(23, "listeners hear the pad the reset released");
        (Machine.Bus.ReadWord(0xD0000004) & (1u << 24)).Should().NotBe(0u, "the level the host drives stays on the pad");
        Machine.Bus.WriteWord(0x40014000 + 23 * 8 + 4, 5);
        Machine.IoBank0.GetPadOutputEnable(23).Should().BeTrue("the pad is usable again after the reset");
    }

    [Fact]
    public void PadsBank0_reset_restores_pad_controls()
    {
        Machine.Bus.WriteWord(0x4001C004 + 14 * 4, 0x4A);
        Machine.Bus.WriteWord(0x4001C000, 1);

        Pulse(8);

        Machine.Bus.ReadWord(0x4001C004 + 14 * 4).Should().Be(0x56u);
        Machine.Bus.ReadWord(0x4001C000).Should().Be(0u);
        Machine.Bus.ReadWord(0x4001C004 + 30 * 4).Should().Be(0xDAu);
        (Machine.PadsBank0.PullUpMask & (1u << 14)).Should().Be(0u);
    }

    [Fact]
    public void SystemReset_resets_sio()
    {
        Machine.Bus.WriteWord(0xD0000024, 1u << 15);
        Machine.Bus.WriteWord(0xD0000014, 1u << 15);
        Machine.Bus.WriteWord(0x40014000 + 15 * 8 + 4, 5);
        Machine.Bus.ReadWord(0xD0000100 + 31 * 4);
        Machine.Bus.WriteWord(0xD0000060, 100);
        Machine.Bus.WriteWord(0xD0000064, 7);
        Machine.Bus.WriteWord(0xD0000080, 0x1234);
        Machine.Sio.InjectFifoRx(42);

        Machine.SystemReset();

        Machine.Bus.ReadWord(0xD0000020).Should().Be(0u);
        Machine.Bus.ReadWord(0xD0000010).Should().Be(0u);
        Machine.Bus.ReadWord(0xD000005C).Should().Be(0u);
        Machine.Bus.ReadWord(0xD0000070).Should().Be(0u);
        Machine.Bus.ReadWord(0xD0000080).Should().Be(0u);
        (Machine.Bus.ReadWord(0xD0000050) & 1u).Should().Be(0u, "the FIFOs come back empty");
        Machine.IoBank0.GetPadOutputEnable(15).Should().BeFalse();
    }
}

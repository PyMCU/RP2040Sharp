using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Regression;

public sealed class R13_pio_runs_in_reset : MachineTestBase
{
    private const uint RESETS_RESET = 0x4000C000;
    private const uint PIO0 = 0x50200000;

    private void SetReset(bool asserted)
    {
        var r = Machine.Bus.ReadWord(RESETS_RESET);
        Machine.Bus.WriteWord(RESETS_RESET, asserted ? r | (1u << 10) : r & ~(1u << 10));
    }

    private void StartSquareWaveOnGpio0()
    {
        SetReset(false);
        Machine.Bus.WriteWord(PIO0 + 0x048, 0xFF01);
        Machine.Bus.WriteWord(PIO0 + 0x04C, 0xFF00);
        Machine.Bus.WriteWord(PIO0 + 0x0CC, 0x1000);
        Machine.Bus.WriteWord(PIO0 + 0x0DC, 1u << 26);
        Machine.Bus.WriteWord(PIO0 + 0x0D8, 0xE081);
        Machine.Bus.WriteWord(0x40014004, 6);
        Machine.Bus.WriteWord(PIO0 + 0x12C, 0xF00);
        Machine.Bus.WriteWord(PIO0 + 0x000, 1);
    }

    private int Edges()
    {
        var edges = 0;
        var last = Machine.IoBank0.GetPadOutputLevel(0);
        for (var i = 0; i < 200; i++)
        {
            Machine.Pio0.Tick(16);
            var now = Machine.IoBank0.GetPadOutputLevel(0);
            if (now != last) edges++;
            last = now;
        }
        return edges;
    }

    [Fact]
    public void State_machine_does_not_run_while_held_in_reset()
    {
        StartSquareWaveOnGpio0();
        Edges().Should().BeGreaterThan(0);

        SetReset(true);

        Edges().Should().Be(0);
        Machine.IoBank0.GetPadOutputEnable(0).Should().BeFalse("a PIO in reset drives none of its pins");
    }

    [Fact]
    public void Reset_restores_instruction_memory_state_machines_and_interrupts()
    {
        StartSquareWaveOnGpio0();
        Machine.Bus.WriteWord(PIO0 + 0x010, 0xABCD);
        Machine.Bus.WriteWord(PIO0 + 0x034, 0x5);

        SetReset(true);
        SetReset(false);

        Machine.Bus.ReadWord(PIO0 + 0x000).Should().Be(0u, "CTRL");
        Machine.Bus.ReadWord(PIO0 + 0x004).Should().Be(0x0F000F00u, "FSTAT: every FIFO empty");
        Machine.Bus.ReadWord(PIO0 + 0x030).Should().Be(0u, "IRQ");
        Machine.Bus.ReadWord(PIO0 + 0x12C).Should().Be(0u, "IRQ0_INTE");
        Machine.Bus.ReadWord(PIO0 + 0x048).Should().Be(0u, "INSTR_MEM0");
        Machine.Bus.ReadWord(PIO0 + 0x0C8).Should().Be(0x10000u, "SM0_CLKDIV");
        Machine.Bus.ReadWord(PIO0 + 0x0CC).Should().Be(0x1F000u, "SM0_EXECCTRL");
        Machine.Bus.ReadWord(PIO0 + 0x0D0).Should().Be(0xC0000u, "SM0_SHIFTCTRL");
        Machine.Bus.ReadWord(PIO0 + 0x0DC).Should().Be(0x14000000u, "SM0_PINCTRL");
        Machine.Bus.ReadWord(PIO0 + 0x03C).Should().Be(0u, "DBG_PADOUT");
        Machine.Bus.ReadWord(PIO0 + 0x040).Should().Be(0u, "DBG_PADOE");
    }
}

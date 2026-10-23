using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Regression;

public sealed class R14_core_state_survives_reset : MachineTestBase
{
    private static void Dirty(ref Core.Cpu.Registers r)
    {
        r.PRIMASK = 1;
        r.IPSR = 3;
        r.CONTROL = 2;
        r.VTOR = 0x2000_0000;
        r.EnabledInterrupts = 0xFFFF;
        r.PendingInterrupts = 1u << 16;
        r.PendingSystick = true;
    }

    private static void ShouldBeClean(Core.Cpu.Registers r)
    {
        r.PRIMASK.Should().Be(0u);
        r.IPSR.Should().Be(0u, "a core leaves reset in thread mode");
        r.CONTROL.Should().Be(0u);
        r.VTOR.Should().Be(0u);
        r.EnabledInterrupts.Should().Be(0u, "the NVIC is reset with the core");
        r.PendingInterrupts.Should().Be(0u);
        r.PendingSystick.Should().BeFalse();
    }

    [Fact]
    public void Machine_reset_clears_both_cores_exception_state_and_systick()
    {
        Dirty(ref Machine.Cpu.Registers);
        Dirty(ref Machine.Cpu1.Registers);
        Machine.Ppb.WriteWord(0xE000E014, 1000);
        Machine.Ppb.WriteWord(0xE000E010, 7);
        Machine.Ppb.ReadWord(0xE000E010).Should().NotBe(0u);

        Machine.Reset();

        ShouldBeClean(Machine.Cpu.Registers);
        ShouldBeClean(Machine.Cpu1.Registers);
        Machine.Ppb.ReadWord(0xE000E010).Should().Be(0u, "SYST_CSR resets to 0");
    }

    [Fact]
    public void Psm_core1_reset_clears_core1_exception_state_and_systick()
    {
        Dirty(ref Machine.Cpu1.Registers);
        Machine.Ppb1.WriteWord(0xE000E014, 1000);
        Machine.Ppb1.WriteWord(0xE000E010, 7);
        Machine.Ppb1.ReadWord(0xE000E010).Should().NotBe(0u);

        Machine.Bus.WriteWord(0x40012004, 1u << 16);
        Machine.Bus.WriteWord(0x40013004, 1u << 16);

        ShouldBeClean(Machine.Cpu1.Registers);
        Machine.Ppb1.ReadWord(0xE000E010).Should().Be(0u, "core 1's SYST_CSR resets with it");
    }
}

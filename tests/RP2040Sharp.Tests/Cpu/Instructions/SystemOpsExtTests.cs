using FluentAssertions;
using RP2040.Core.Helpers;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

public abstract class SystemOpsExtTests
{
    // ================================================================
    // CPSID / CPSIE
    // ================================================================
    public class Cps : CpuTestBase
    {
        [Fact]
        public void Cpsid_Should_Set_PRIMASK()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Cpsid);
            Cpu.Registers.PRIMASK = 0;

            Cpu.Step();

            Cpu.Registers.PRIMASK.Should().Be(1);
        }

        [Fact]
        public void Cpsie_Should_Clear_PRIMASK()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Cpsie);
            Cpu.Registers.PRIMASK = 1;

            Cpu.Step();

            Cpu.Registers.PRIMASK.Should().Be(0);
        }

        // ARMv6-M CPS: "if CurrentModeIsPrivileged() then ..." — a NOP from unprivileged Thread code.
        [Fact]
        public void Unprivileged_Cpsid_Is_Ignored()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Cpsid);
            Cpu.Registers.PRIMASK = 0;
            Cpu.Registers.IPSR = 0;
            Cpu.Registers.CONTROL = 1;   // nPRIV

            Cpu.Step();

            Cpu.Registers.PRIMASK.Should().Be(0);
        }

        [Fact]
        public void Unprivileged_Cpsie_Is_Ignored()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Cpsie);
            Cpu.Registers.PRIMASK = 1;
            Cpu.Registers.IPSR = 0;
            Cpu.Registers.CONTROL = 1;   // nPRIV

            Cpu.Step();

            Cpu.Registers.PRIMASK.Should().Be(1, "unprivileged code cannot unmask interrupts");
        }

        [Fact]
        public void Cpsie_Should_Set_InterruptsUpdated_Flag()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Cpsie);

            Cpu.Step();

            Cpu.Registers.InterruptsUpdated.Should().Be(true);
        }
    }

    // ================================================================
    // MRS / MSR special-register views (ARMv6-M B4.2.2 / B4.2.3)
    // ================================================================
    public class SpecialRegisters : CpuTestBase
    {
        private uint Mrs(uint sysm, uint rd = 5)
        {
            Cpu.Registers[(int)rd] = 0xA5A5A5A5;
            Bus.WriteHalfWord(0x20000000, 0xF3EF);
            Bus.WriteHalfWord(0x20000002, (ushort)(0x8000 | (rd << 8) | sysm));
            Cpu.Registers.PC = 0x20000000;
            Cpu.Step();
            return Cpu.Registers[(int)rd];
        }

        [Fact]
        public void Mrs_Xpsr_Views_Read_Epsr_As_Zero()
        {
            Cpu.Registers.N = true; Cpu.Registers.C = true;
            Cpu.Registers.IPSR = 0x11;

            Mrs(0).Should().Be(0xA0000000u, "APSR");
            Mrs(1).Should().Be(0xA0000011u, "IAPSR");
            Mrs(2).Should().Be(0xA0000000u, "EAPSR: the T bit reads as zero");
            Mrs(3).Should().Be(0xA0000011u, "xPSR: the T bit reads as zero");
            Mrs(5).Should().Be(0x11u, "IPSR");
            Mrs(6).Should().Be(0u, "EPSR reads as zero");
            Mrs(7).Should().Be(0x11u, "IEPSR");
        }

        [Fact]
        public void Unprivileged_Mrs_Reads_Stack_Pointers_And_Primask_As_Zero()
        {
            Cpu.Registers.PRIMASK = 1;
            Cpu.Registers.IPSR = 0;
            Cpu.Registers.CONTROL = 1;   // nPRIV

            Mrs(8).Should().Be(0u, "MSP");
            Mrs(9).Should().Be(0u, "PSP");
            Mrs(16).Should().Be(0u, "PRIMASK");
            Mrs(20).Should().Be(1u, "CONTROL is readable unprivileged");
        }

        [Fact]
        public void Unprivileged_Msr_Cannot_Clear_nPRIV_Or_Set_Primask()
        {
            Cpu.Registers.IPSR = 0;
            Cpu.Registers.CONTROL = 1;   // nPRIV
            Cpu.Registers.R4 = 0;
            Bus.WriteHalfWord(0x20000000, 0xF384); Bus.WriteHalfWord(0x20000002, 0x8814);   // msr CONTROL, r4
            Bus.WriteHalfWord(0x20000004, 0xF384); Bus.WriteHalfWord(0x20000006, 0x8810);   // msr PRIMASK, r4

            Cpu.Step();
            Cpu.Registers.CONTROL.Should().Be(1u, "Thread code cannot grant itself privilege");
            Cpu.Registers.R4 = 1;
            Cpu.Step();
            Cpu.Registers.PRIMASK.Should().Be(0u, "PRIMASK write ignored");
        }
    }

    // ================================================================
    // WFI
    // ================================================================
    public class Wfi : CpuTestBase
    {
        [Fact]
        public void Should_Set_Waiting_Flag()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Wfi);

            Cpu.Step();

            Cpu.Registers.Waiting.Should().Be(true);
        }
    }

    // ================================================================
    // SEV / WFE
    // ================================================================
    public class SevWfe : CpuTestBase
    {
        [Fact]
        public void Sev_Should_Set_EventRegistered()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Sev);

            Cpu.Step();

            Cpu.Registers.EventRegistered.Should().Be(true);
        }

        [Fact]
        public void Wfe_Should_Set_Waiting_When_No_Event()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Wfe);
            Cpu.Registers.EventRegistered = false;

            Cpu.Step();

            Cpu.Registers.Waiting.Should().Be(true);
        }

        [Fact]
        public void Wfe_Should_Clear_Event_And_Not_Wait_When_Event_Registered()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Wfe);
            Cpu.Registers.EventRegistered = true;

            Cpu.Step();

            Cpu.Registers.Waiting.Should().Be(false);
            Cpu.Registers.EventRegistered.Should().Be(false);
        }
    }

    // ================================================================
    // BKPT
    // ================================================================
    public class Bkpt : CpuTestBase
    {
        [Fact]
        public void Should_Invoke_OnBreakpoint_Callback_With_Imm8()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Bkpt(42));

            byte? received = null;
            Cpu.OnBreakpoint = imm => received = imm;

            Cpu.Step();

            received.Should().Be(42);
        }

        [Fact]
        public void Should_Not_Throw_When_No_Callback_Registered()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Bkpt(0));
            Cpu.OnBreakpoint = null;

            var act = () => Cpu.Step();
            act.Should().NotThrow();
        }
    }

    // ================================================================
    // SVC (triggers PendingSVCall)
    // ================================================================
    public class Svc : CpuTestBase
    {
        [Fact]
        public void Should_Set_PendingSVCall_Flag()
        {
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Svc(0));

            Cpu.Step();

            Cpu.Registers.PendingSVCall.Should().Be(true);
            Cpu.Registers.InterruptsUpdated.Should().Be(true);
        }
    }
}

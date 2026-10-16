using FluentAssertions;
using RP2040.Core.Helpers;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

// ====================================================================
// Tests for the EXC_RETURN handling lifted into BX Rm and POP {pc}.
//
// ARMv6-M B1.5.8: when an instruction loads a value of 0xFFFFFFFx into
// PC while the processor is in Handler mode, that value is interpreted
// as an exception return marker.  The unstacking pulls 8 words off the
// active stack (R0,R1,R2,R3,R12,LR,RetPC,xPSR) and resumes execution.
//
// The original implementation gated this on `IPSR != 0`, which broke
// the MicroPython boot path because some IRQ handlers leave IPSR
// effectively cleared by the time `BX LR` executes.  The current code
// fires ExceptionReturn purely on the EXC_RETURN range — these tests
// lock in that behaviour and the basic register-restore semantics.
// ====================================================================

public abstract class ExceptionReturnTests
{
    private const uint EXC_RETURN_THREAD_MSP = 0xFFFFFFF9;
    private const uint EXC_RETURN_HANDLER    = 0xFFFFFFF1;

    private const uint StackBase = 0x20004000;

    /// <summary>
    /// Lays out an 8-word exception frame at <paramref name="frameAddr"/>
    /// matching the ARMv6-M architectural stack: R0,R1,R2,R3,R12,LR,RetPC,xPSR.
    /// xPSR is written without bit 9 set (no stack alignment adjustment),
    /// so unstacking advances SP by exactly 0x20.
    /// </summary>
    private static void WriteFrame(
        RP2040.Core.Memory.BusInterconnect bus, uint frameAddr,
        uint r0, uint r1, uint r2, uint r3,
        uint r12, uint lr, uint retPc, uint xpsr)
    {
        bus.WriteWord(frameAddr + 0x00, r0);
        bus.WriteWord(frameAddr + 0x04, r1);
        bus.WriteWord(frameAddr + 0x08, r2);
        bus.WriteWord(frameAddr + 0x0C, r3);
        bus.WriteWord(frameAddr + 0x10, r12);
        bus.WriteWord(frameAddr + 0x14, lr);
        bus.WriteWord(frameAddr + 0x18, retPc);
        bus.WriteWord(frameAddr + 0x1C, xpsr);
    }

    public class BxLr : CpuTestBase
    {
        [Fact]
        public void BxLr_With_ThreadModeMsp_ExceptionReturn_UnstacksRegisters()
        {
            // Frame already on MSP starting at StackBase.
            WriteFrame(Bus, StackBase,
                r0:    0x11111111,
                r1:    0x22222222,
                r2:    0x33333333,
                r3:    0x44444444,
                r12:   0xCCCCCCCC,
                lr:    0xAAAAAAAA,
                retPc: 0x10000200u | 1u,  // Thumb bit
                xpsr:  0x01000000u);      // T bit only

            Cpu.Registers.SP  = StackBase;
            Cpu.Registers[LR] = EXC_RETURN_THREAD_MSP;
            // ARMv6-M A2.3.1 BXWritePC: an EXC_RETURN value is only honoured in Handler mode.
            Cpu.Registers.IPSR = 16;

            // BX LR — encoding 0x4770.
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Bx(LR));

            Cpu.Step();

            // ExceptionReturn restores the frame.
            Cpu.Registers[R0].Should().Be(0x11111111);
            Cpu.Registers[R1].Should().Be(0x22222222);
            Cpu.Registers[R2].Should().Be(0x33333333);
            Cpu.Registers[R3].Should().Be(0x44444444);
            Cpu.Registers[R12].Should().Be(0xCCCCCCCC);
            Cpu.Registers[LR].Should().Be(0xAAAAAAAA);

            // Resume PC = retPc with Thumb bit stripped.
            Cpu.Registers.PC.Should().Be(0x10000200);

            // SP advances past the 32-byte frame (xPSR bit 9 was clear).
            Cpu.Registers.SP.Should().Be(StackBase + 0x20);

            // Returning to Thread mode → IPSR == 0.
            Cpu.Registers.IPSR.Should().Be(0u);
        }

        [Fact]
        public void BxLr_With_HandlerModeMsp_ExceptionReturn_UnstacksRegisters()
        {
            WriteFrame(Bus, StackBase,
                r0:    0xDEADBEEF,
                r1:    0,
                r2:    0,
                r3:    0,
                r12:   0,
                lr:    0xFEEDFACEu,
                retPc: 0x10000400u | 1u,
                xpsr:  0x01000000u);

            Cpu.Registers.SP  = StackBase;
            Cpu.Registers[LR] = EXC_RETURN_HANDLER;
            // Pretend we were in handler #16 before the return.
            Cpu.Registers.IPSR = 16;

            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Bx(LR));

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0xDEADBEEF);
            Cpu.Registers[LR].Should().Be(0xFEEDFACEu);
            Cpu.Registers.PC.Should().Be(0x10000400);
            Cpu.Registers.SP.Should().Be(StackBase + 0x20);
        }

        [Fact]
        public void BxLr_With_NonExcReturn_DoesNotUnstack()
        {
            // Sentinels: any unstack would clobber these.
            Cpu.Registers[R0] = 0xCAFECAFE;
            Cpu.Registers[R1] = 0xBEEFBEEF;
            Cpu.Registers.SP  = StackBase;
            Cpu.Registers[LR] = 0x10000301;

            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Bx(LR));

            Cpu.Step();

            Cpu.Registers.PC.Should().Be(0x10000300);
            Cpu.Registers.SP.Should().Be(StackBase);          // untouched
            Cpu.Registers[R0].Should().Be(0xCAFECAFE);        // untouched
            Cpu.Registers[R1].Should().Be(0xBEEFBEEF);        // untouched
        }
    }

    public class PopPcExceptionReturn : CpuTestBase
    {
        [Fact]
        public void PopPc_OnlyPc_With_ExcReturn_OnTopOfStack_UnstacksFrame()
        {
            // Stack layout at SP:
            //   [SP+0x00]   = EXC_RETURN value (popped into PC)
            //   [SP+0x04..] = exception frame (8 words)
            //
            // After POP {pc}, SP = SP+4, then ExceptionReturn unstacks 0x20
            // bytes from the new SP, leaving SP = original + 0x24.
            const uint stackAtPop = StackBase;
            Bus.WriteWord(stackAtPop, EXC_RETURN_THREAD_MSP);
            WriteFrame(Bus, stackAtPop + 4,
                r0:    0x55555555,
                r1:    0x66666666,
                r2:    0,
                r3:    0,
                r12:   0,
                lr:    0x77777777,
                retPc: 0x10000800u | 1u,
                xpsr:  0x01000000u);

            Cpu.Registers.SP = stackAtPop;
            Cpu.Registers.IPSR = 16;   // LoadWritePC honours EXC_RETURN only in Handler mode (A2.3.1)

            // POP {pc} — encoding 0xBD00.
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Pop(true, 0));

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x55555555);
            Cpu.Registers[R1].Should().Be(0x66666666);
            Cpu.Registers[LR].Should().Be(0x77777777);
            Cpu.Registers.PC.Should().Be(0x10000800);
            Cpu.Registers.SP.Should().Be(stackAtPop + 4 + 0x20);
            Cpu.Registers.IPSR.Should().Be(0u);
        }

        [Fact]
        public void PopRegistersAndPc_With_ExcReturn_RestoresLowRegisterFirst()
        {
            // POP {r4, pc} — typical IRQ handler epilogue when r4 was saved.
            // [SP+0x00] = r4 value
            // [SP+0x04] = EXC_RETURN
            // [SP+0x08..] = exception frame
            const uint stackAtPop = StackBase;
            Bus.WriteWord(stackAtPop, 0xABCDEF00);             // r4
            Bus.WriteWord(stackAtPop + 4, EXC_RETURN_THREAD_MSP);
            WriteFrame(Bus, stackAtPop + 8,
                r0:    0x99999999,
                r1:    0,
                r2:    0,
                r3:    0,
                r12:   0,
                lr:    0,
                retPc: 0x10000C00u | 1u,
                xpsr:  0x01000000u);

            Cpu.Registers.SP = stackAtPop;
            Cpu.Registers.IPSR = 16;   // LoadWritePC honours EXC_RETURN only in Handler mode (A2.3.1)

            // POP {r4, pc} — register list bit 4 set, P bit set.
            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Pop(true, 1u << 4));

            Cpu.Step();

            Cpu.Registers[4].Should().Be(0xABCDEF00);
            Cpu.Registers[R0].Should().Be(0x99999999);
            Cpu.Registers.PC.Should().Be(0x10000C00);
            // POP advances SP by (regCount+1)*4 = 8, then ExceptionReturn adds 0x20.
            Cpu.Registers.SP.Should().Be(stackAtPop + 8 + 0x20);
        }

        [Fact]
        public void PopPc_With_NonExcReturn_BehavesAsRegularPop()
        {
            const uint stackAtPop = StackBase;
            Bus.WriteWord(stackAtPop, 0x10000F01);             // ordinary return
            // No frame after — verifies ExceptionReturn is NOT triggered
            // (because if it were, it would read garbage from beyond).
            Cpu.Registers[R0] = 0xCAFECAFE;
            Cpu.Registers.SP  = stackAtPop;

            Bus.WriteHalfWord(0x20000000, InstructionEmiter.Pop(true, 0));

            Cpu.Step();

            Cpu.Registers.PC.Should().Be(0x10000F00);
            Cpu.Registers.SP.Should().Be(stackAtPop + 4);
            Cpu.Registers[R0].Should().Be(0xCAFECAFE);          // untouched
        }
    }
}

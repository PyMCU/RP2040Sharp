using FluentAssertions;
using RP2040.Core.Cpu;
using RP2040.Core.Helpers;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

public abstract class FlowOpsExtTests
{
    // ================================================================
    // CBZ / CBNZ
    //
    // ARMv6-M (Cortex-M0/M0+) does NOT define CBZ/CBNZ: they are ARMv7-M (Thumb-2) instructions. ARMv6-M
    // A5.2.5 (Table A5-6) allocates only the 16-bit miscellaneous encodings ADD/SUB SP, SXT*/UXT*, PUSH,
    // CPS, REV*, POP, BKPT and the hints, and "other encodings in this space are UNDEFINED". So 0xB1xx,
    // 0xB3xx, 0xB7xx, 0xB9xx and 0xBBxx must HardFault, whatever the register holds. (These tests used to
    // pin the opposite: CBZ/CBNZ branching.)
    // ================================================================
    public class CbzCbnzAreUndefined : CpuTestBase
    {
        private void AssertHardFault(ushort opcode, uint r0)
        {
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R0] = r0;

            Cpu.Step();

            Cpu.Registers.IPSR.Should().Be(3u, $"0x{opcode:X4} is UNDEFINED on ARMv6-M");
        }

        [Theory]
        [InlineData(false, 0u)]
        [InlineData(false, 1u)]
        [InlineData(true, 0u)]
        [InlineData(true, 1u)]
        public void Emitted_Cbz_And_Cbnz_HardFault(bool nonZero, uint r0)
        {
            AssertHardFault(nonZero ? InstructionEmiter.Cbnz(R0, 4) : InstructionEmiter.Cbz(R0, 4), r0);
        }

        [Theory]
        [InlineData((ushort)0xB100)]   // CBZ  R0, i=0
        [InlineData((ushort)0xB13A)]   // CBZ  R2, imm5=7 (the opcode the old synthetic bootrom stub used)
        [InlineData((ushort)0xB300)]   // CBZ  R0, i=1
        [InlineData((ushort)0xB700)]   // not allocated either
        [InlineData((ushort)0xB900)]   // CBNZ R0, i=0
        [InlineData((ushort)0xBB00)]   // CBNZ R0, i=1
        public void Raw_Opcodes_HardFault(ushort opcode)
        {
            AssertHardFault(opcode, 0);
        }

        [Fact]
        public void Yield_Is_A_Hint_Not_Cbnz()
        {
            // 0xBF10 = YIELD (A5.2.5 Table A5-7). The old CBNZ mask 0xF900 swallowed it and branched when R0 != 0.
            Bus.WriteHalfWord(0x20000000, 0xBF10);
            Cpu.Registers[R0] = 5;

            Cpu.Step();

            Cpu.Registers.PC.Should().Be(0x20000002);
            Cpu.Registers.IPSR.Should().Be(0u);
        }
    }
}

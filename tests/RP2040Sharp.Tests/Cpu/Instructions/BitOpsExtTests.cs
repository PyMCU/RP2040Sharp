using FluentAssertions;
using RP2040.Core.Helpers;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

public abstract class BitOpsExtTests
{
    // ================================================================
    // ROR (Rotate Right, register)
    // ================================================================
    public class Ror : CpuTestBase
    {
        [Fact]
        public void Should_Rotate_Right_By_8()
        {
            var opcode = InstructionEmiter.Ror(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R0] = 0x12345678;
            Cpu.Registers[R1] = 8;

            Cpu.Step();

            // rotate 0x12345678 right 8: 0x78_123456
            Cpu.Registers[R0].Should().Be(0x78123456);
            // carry = bit(8-1)=bit7 of original = bit7 of 0x78 = 0 (0111_1000)
            Cpu.Registers.C.Should().Be(false);
        }

        [Fact]
        public void Should_Not_Change_Value_When_ShiftIs_Zero()
        {
            var opcode = InstructionEmiter.Ror(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R0] = 0xABCDEF01;
            Cpu.Registers[R1] = 0;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0xABCDEF01);
        }

        [Fact]
        public void Should_Rotate_By_32_Returning_Same_Value()
        {
            var opcode = InstructionEmiter.Ror(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R0] = 0x12345678;
            Cpu.Registers[R1] = 32;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x12345678);
            Cpu.Registers.C.Should().Be(false, "bit31 = 0");
        }

        [Fact]
        public void Should_Set_N_Flag_When_Result_Is_Negative()
        {
            var opcode = InstructionEmiter.Ror(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R0] = 0x01;   // shifting right will put bit0 to bit31
            Cpu.Registers[R1] = 1;

            Cpu.Step();

            Cpu.Registers.N.Should().Be(true, "bit0 rotated to bit31 sets N");
            Cpu.Registers.C.Should().Be(true, "bit0 was the carry");
        }
    }

    // ================================================================
    // SXTH (Sign-extend Halfword)
    // ================================================================
    public class Sxth : CpuTestBase
    {
        [Fact]
        public void Should_SignExtend_Negative_Halfword()
        {
            var opcode = InstructionEmiter.Sxth(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xFFFF8001;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0xFFFF8001);
        }

        [Fact]
        public void Should_SignExtend_Positive_Halfword()
        {
            var opcode = InstructionEmiter.Sxth(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xFFFF7FFF;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x00007FFF);
        }
    }

    // ================================================================
    // SXTB (Sign-extend Byte)
    // ================================================================
    public class Sxtb : CpuTestBase
    {
        [Fact]
        public void Should_SignExtend_Negative_Byte()
        {
            var opcode = InstructionEmiter.Sxtb(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xFFFFFF80;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0xFFFFFF80);
        }

        [Fact]
        public void Should_SignExtend_Positive_Byte()
        {
            var opcode = InstructionEmiter.Sxtb(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xABCDEF7F;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x0000007F);
        }
    }

    // ================================================================
    // UXTH (Zero-extend Halfword)
    // ================================================================
    public class Uxth : CpuTestBase
    {
        [Fact]
        public void Should_ZeroExtend_Halfword()
        {
            var opcode = InstructionEmiter.Uxth(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xABCDBEEF;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x0000BEEF);
        }
    }

    // ================================================================
    // UXTB (Zero-extend Byte)
    // ================================================================
    public class Uxtb : CpuTestBase
    {
        [Fact]
        public void Should_ZeroExtend_Byte()
        {
            var opcode = InstructionEmiter.Uxtb(R0, R1);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R1] = 0xABCDEF42;

            Cpu.Step();

            Cpu.Registers[R0].Should().Be(0x00000042);
        }
    }

    // ================================================================
    // CLZ (Count Leading Zeros, Thumb-2 32-bit)
    //
    // CLZ is ARMv7-M. ARMv6-M A5.3 (Table A5-9/A5-10) allocates only BL, MSR, MRS, DSB/DMB/ISB and UDF in
    // the 32-bit space; a first halfword of 0xFABx is UNDEFINED, so it must HardFault.
    // ================================================================
    public class Clz : CpuTestBase
    {
        [Fact]
        public void Is_Undefined_On_ArmV6M()
        {
            var (h1, h2) = InstructionEmiter.Clz(R0, R1);
            Bus.WriteHalfWord(0x20000000, h1);
            Bus.WriteHalfWord(0x20000002, h2);
            Cpu.Registers[R0] = 0xDEADBEEF;
            Cpu.Registers[R1] = 0x00080000;

            Cpu.Step();

            Cpu.Registers.IPSR.Should().Be(3u);
            Cpu.Registers[R0].Should().Be(0xDEADBEEF, "the instruction must not execute");
        }
    }
}

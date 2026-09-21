using FluentAssertions;
using RP2040.Core.Cpu;
using RP2040.Core.Helpers;
using RP2040.Core.Memory;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

public abstract class BitOpsTests
{
    public class Ands : CpuTestBase
    {
        [Fact]
        public void Should_CalculateBitwiseAnd_And_UpdateNegativeFlag()
        {
            // Arrange
            var opcode = InstructionEmiter.Ands(R5, R0);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = 0xffff0000;
            Cpu.Registers[R0] = 0xf00fffff;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be(0xf00f0000);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public abstract class Asrs
    {
        public class Immediate : CpuTestBase
        {
            [Fact]
            public void Should_SignExtend_When_ShiftingMaxNegative_By31()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsImm5(R3, R2, 31);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 0x80000000;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0xffffffff); // -1 (Sign extension)
                Cpu.Registers.PC.Should().Be(0x20000002);

                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
            }

            [Fact]
            public void Should_PerformArithmeticShiftBy32_When_ImmediateIsZero()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsImm5(R3, R2, 0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 0x80000000;
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0xffffffff);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_UseOnlyBottomByteOfShiftRegister()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x80000040;
                Cpu.Registers[R4] = 0xff500007;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0xff000000);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_ResultInZero_When_ShiftingPositiveNumber_ByMoreThan31()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x40000040;
                Cpu.Registers[R4] = 50;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeFalse();
            }

            [Fact]
            public void Should_ShiftBy31_And_UpdateCarry_WithBit30()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x40000040;
                Cpu.Registers[R4] = 31;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_ResultInMinusOne_When_ShiftingNegativeNumber_ByMoreThan31()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x80000040;
                Cpu.Registers[R4] = 50;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0xffffffff);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_PreserveValue_And_PreserveCarry_When_ShiftIsZero()
            {
                // Arrange
                var opcode = InstructionEmiter.AsrsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x80000040;
                Cpu.Registers[R4] = 0;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0x80000040);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
            }
        }
    }

    public class Bics : CpuTestBase
    {
        [Fact]
        public void Should_ClearBits_WhereMaskIsSet()
        {
            // Arrange
            var opcode = InstructionEmiter.Bics(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 0xff;
            Cpu.Registers[R3] = 0x0f;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(0xf0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
        }

        [Fact]
        public void Should_SetNegativeFlag_When_ResultHasSignBitSet()
        {
            // Arrange
            var opcode = InstructionEmiter.Bics(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 0xffffffff;
            Cpu.Registers[R3] = 0x0000ffff;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(0xffff0000);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public class Eors : CpuTestBase
    {
        [Fact]
        public void Should_CalculateExclusiveOr_And_UpdateNegativeFlag()
        {
            // Arrange
            var opcode = InstructionEmiter.Eors(R1, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R1] = 0xf0f0f0f0;
            Cpu.Registers[R3] = 0x08ff3007;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R1].Should().Be(0xf80fc0f7);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public class Mov : CpuTestBase
    {
        [Fact]
        public void Should_CopyValue_BetweenRegisters()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(R3, R8);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R8] = 55;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R3].Should().Be(55);
        }

        [Fact]
        public void Should_ReadProgramCounter_WithPipelineOffset()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(R3, PC);
            Bus.WriteHalfWord(0x20000000, opcode);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R3].Should().Be(0x20000004);
        }

        [Fact]
        public void Should_ReadProgramCounter_WithPipelineOffset_AtHalfwordAlignedAddress()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(R3, PC);
            Bus.WriteHalfWord(0x20000002, opcode);

            Cpu.Registers.PC = 0x20000002;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R3].Should().Be(0x20000006);
        }

        [Fact]
        public void Should_MoveRegisterToStackPointer_Without_ForcedAlignment()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(SP, R8);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R8] = 55;

            // Act
            Cpu.Step();

            // Assert: ARMv6-M §A6.7.75 — MOV SP, Rm writes the raw register value;
            // alignment is NOT forced by the instruction.
            Cpu.Registers[SP].Should().Be(55);
        }

        [Fact]
        public void Should_WriteRawValueToStackPointer()
        {
            // Arrange
            Cpu.Registers.PC = 0x20000000;
            var opcode = InstructionEmiter.Mov(SP, R5);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R5 = 0x53;

            // Act
            Cpu.Step();

            // Assert: ARMv6-M §A6.7.75 — MOV SP, Rm writes the raw value without masking.
            Cpu.Registers.SP.Should().Be(0x53);
        }

        [Fact]
        public void Should_ClearLeastSignificantBit_When_WritingToProgramCounter()
        {
            // Arrange
            Cpu.Registers.PC = 0x20000000;
            var opcode = InstructionEmiter.Mov(PC, R5);
            Bus.WriteHalfWord(0x20000000, opcode); // mov pc, r5
            Cpu.Registers.R5 = 0x53;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.PC.Should().Be(0x52);
        }
    }

    public class Movs : CpuTestBase
    {
        [Fact]
        public void Should_LoadImmediateValue_And_UpdateFlags()
        {
            // Arrange
            var opcode = InstructionEmiter.Movs(R5, 128);
            Bus.WriteHalfWord(0x20000000, opcode);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be(128);
            Cpu.Registers.PC.Should().Be(0x20000002);
        }

        [Fact]
        public void Should_WriteRawValueToStackPointer()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(SP, R5);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = 0x53;

            // Act
            Cpu.Step();

            // Assert: ARMv6-M §A6.7.75 — MOV SP, Rm writes the raw register value;
            // alignment is NOT forced.
            Cpu.Registers.SP.Should().Be(0x53);
        }

        [Fact]
        public void Should_ClearLeastSignificantBit_When_WritingToProgramCounter()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(PC, R5);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = 0x53;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.PC.Should().Be(0x52);
        }

        [Fact]
        public void Should_CopyValue_BetweenRegisters()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(R6, R5);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = 50;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R6].Should().Be(50);
        }

        [Fact]
        public void Should_CopyNegativeValue_BetweenRegisters()
        {
            // Arrange
            var opcode = InstructionEmiter.Mov(R6, R5);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = unchecked((uint)-50);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R6].Should().Be(unchecked((uint)-50));
        }
    }

    public class Mvns : CpuTestBase
    {
        [Fact]
        public void Should_BitwiseInvert_Value_And_UpdateNegativeFlag()
        {
            // Arrange
            var opcode = InstructionEmiter.Mvns(R4, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R3] = 0x11115555;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R4].Should().Be(0xeeeeaaaa);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public abstract class Lsls
    {
        public class Immediate : CpuTestBase
        {
            [Fact]
            public void Should_ShiftLeft_ByImmediateValue()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsImm5(R5, R5, 18);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 0b00000000000000000011; // 0b11
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R5].Should().Be(0b11000000000000000000);
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.PC.Should().Be(0x20000002);
            }

            [Fact]
            public void Should_SetCarryFlag_When_BitIsShiftedOut()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsImm5(R5, R5, 18);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 0x4001;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R5].Should().Be(0x40000);
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_PreserveCarry_When_ShiftIsZero()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsImm5(R5, R5, 0); // LSLS R5, R5, #0
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 0xFFFF;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R5].Should().Be(0xFFFF);
                Cpu.Registers.C.Should()
                    .BeTrue("LSL #0 should preserve Carry flag (MOVS behavior)");
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.N.Should().BeFalse();
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_UseOnlyBottomByte_Of_ShiftRegister()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsRegister(R5, R0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 0b00000000000000000011;
                Cpu.Registers[R0] = 0xFF003302;
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R5].Should().Be(0b00000000000000001100);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.C.Should().BeFalse();
            }

            [Fact]
            public void Should_ResultInZero_And_SetCarry_When_ShiftingBy32()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsRegister(R3, R4);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 1;
                Cpu.Registers[R4] = 0x20;
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeTrue(); // Result is 0
                Cpu.Registers.C.Should().BeTrue(); // Bit 0 shifted out
            }

            [Fact]
            public void Should_ResultInZero_And_ClearCarry_When_ShiftingLeftByMoreThan32()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsRegister(R0, R1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0xFFFFFFFF;
                Cpu.Registers[R1] = 33; // Shift 33
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(0);
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeFalse("LSL > 32 should set Carry to 0");
            }

            [Fact]
            public void Should_InterpretShiftRegister_AsUnsignedByte()
            {
                // Arrange
                var opcode = InstructionEmiter.LslsRegister(R0, R1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 1;
                Cpu.Registers[R1] = 0xFFFFFFFF; // Bottom byte is 0xFF (255)

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(0); // 1 << 255 is 0
                Cpu.Registers.C.Should().BeFalse(); // Carry from huge shift is 0
            }
        }
    }

    public abstract class Lsrs
    {
        public class Immediate : CpuTestBase
        {
            [Fact]
            public void Should_ShiftRight_ByImmediateValue()
            {
                // Port from JS: 'should execute a `lsrs r1, r1, #1` instruction'
                // Arrange
                var opcode = InstructionEmiter.LsrsImm5(R1, R1, 1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 0b10; // 2
                Cpu.Registers.C = true; // Dirty Carry

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(0b1);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.C.Should().BeFalse(); // Bit 0 (0) shifted out
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
            }

            [Fact]
            public void Should_PerformLogicalShiftRightBy32_When_ImmediateIsZero()
            {
                // Arrange
                var opcode = InstructionEmiter.LsrsImm5(R1, R1, 0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 0xFFFFFFFF;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(0u);
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_SetCarryFlag_When_OneIsShiftedOut()
            {
                // New Test: Explicitly checking carry behavior on standard shift
                // Arrange
                var opcode = InstructionEmiter.LsrsImm5(R0, R0, 1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0b11; // 3 -> Shift right 1 -> 1, Carry = 1
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(1);
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_ShiftRight_ByMaximumImmediate_31()
            {
                // Arrange
                var opcode = InstructionEmiter.LsrsImm5(R0, R0, 31);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0x80000000; // Bit 31 set
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(1); // 0x80000000 >> 31 = 1
                Cpu.Registers.C.Should().BeFalse("Bit shifted out (bit 30) was 0");
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_Execute_LsrsRegister_StandardCase()
            {
                // Arrange
                var opcode = InstructionEmiter.LsrsRegister(R5, R0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 0xff00000f;
                Cpu.Registers[R0] = 0xff003302;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R5].Should().Be(0x3fc00003);
                Cpu.Registers.PC.Should().Be(0x20000002);
                Cpu.Registers.C.Should().BeTrue();
            }

            [Fact]
            public void Should_ResultInZero_When_ShiftingBy32()
            {
                // Port from JS: 'should return zero for `lsrs r2, r3` with 32 bit shift'
                // Arrange
                var opcode = InstructionEmiter.LsrsRegister(R2, R3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 10; // ...00001010
                Cpu.Registers[R3] = 32;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R2].Should().Be(0);
                Cpu.Registers.Z.Should().BeTrue();
                // Bit 31 of '10' is 0, so Carry should be 0
                Cpu.Registers.C.Should().BeFalse();
            }

            [Fact]
            public void Should_ResultInZero_And_ClearCarry_When_ShiftingByMoreThan32()
            {
                // New Test: Edge case > 32
                // Arrange
                var opcode = InstructionEmiter.LsrsRegister(R2, R3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 0xFFFFFFFF;
                Cpu.Registers[R3] = 33; // Shift > 32
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R2].Should().Be(0);
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeFalse(); // For shift > 32, C becomes 0
            }

            [Fact]
            public void Should_PreserveValue_And_PreserveCarry_When_ShiftIsZero()
            {
                // New Test: Shift by 0 (Register variant)
                // Note: Unlike Immediate #0 (which means 32), Register 0 means 0.
                // Arrange
                var opcode = InstructionEmiter.LsrsRegister(R0, R1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0x12345678;
                Cpu.Registers[R1] = 0;
                Cpu.Registers.C = true; // Should persist

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(0x12345678);
                Cpu.Registers.C.Should()
                    .BeTrue("Shift by 0 in register mode should not affect Carry");
                Cpu.Registers.Z.Should().BeFalse();
            }

            [Fact]
            public void Should_SetCarryToBit31_When_ShiftingBy32()
            {
                // Critical case: LSR by 32. Result is 0, Carry becomes original Bit 31.
                // Arrange
                var opcode = InstructionEmiter.LsrsRegister(R0, R1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0x80001234; // Bit 31 is 1
                Cpu.Registers[R1] = 32;
                Cpu.Registers.C = false;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(0);
                Cpu.Registers.C.Should().BeTrue("LSR by 32 should set Carry to original Bit 31");
            }
        }
    }

    public class Orrs : CpuTestBase
    {
        [Fact]
        public void Should_CalculateBitwiseOr_And_UpdateNegativeFlag()
        {
            // Arrange
            var opcode = InstructionEmiter.Orrs(R5, R0);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R5] = 0xf00f0000;
            Cpu.Registers[R0] = 0xf000ffff;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be(0xf00fffff);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public class Rev : CpuTestBase
    {
        [Fact]
        public void Should_ReverseByteOrder_Of_32BitWord()
        {
            // Arrange
            var opcode = InstructionEmiter.Rev(R2, R3);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R3] = 0x11223344;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R2.Should().Be(0x44332211);
        }
    }

    public class Rev16 : CpuTestBase
    {
        [Fact]
        public void ShouldExecuteRev16R0R5Instruction()
        {
            // Arrange
            var opcode = InstructionEmiter.Rev16(R0, R5);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers[R5] = 0x11223344;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(0x22114433);
        }
    }

    public class Revsh : CpuTestBase
    {
        [Fact]
        public void Should_ReverseLowerHalfword_And_SignExtend()
        {
            // Arrange
            var opcode = InstructionEmiter.Revsh(R1, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R2] = 0xeeaa55f0;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R1].Should().Be(0xfffff055);
        }
    }

    public class Tst : CpuTestBase
    {
        [Fact]
        public void Should_SetNegativeFlag_When_ResultHasSignBitSet()
        {
            // Arrange
            var opcode = InstructionEmiter.Tst(R1, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R1] = 0xf0000000;
            Cpu.Registers[R3] = 0xf0004000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.N.Should().BeTrue();
        }

        [Fact]
        public void Should_SetZeroFlag_When_ResultIsZero()
        {
            // Arrange
            var opcode = InstructionEmiter.Tst(R1, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R1] = 0xf0;
            Cpu.Registers[R3] = 0x0f;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.Z.Should().BeTrue();
        }
    }
}

using FluentAssertions;
using RP2040.Core.Helpers;
using RP2040.tests.Fixtures;

namespace RP2040.tests.Cpu.Instructions;

public abstract class ArithmeticOpsTests
{
    public class Adcs : CpuTestBase
    {
        [Fact]
        public void Should_AddTwoRegisters_IncludingCarry()
        {
            // Arrange
            var opcode = InstructionEmiter.Adcs(R5, R4);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R4] = 55;
            Cpu.Registers[R5] = 66;
            Cpu.Registers.C = true;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be(122u);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_SetNegativeAndOverflow_When_AddingMaxPositiveWithCarry()
        {
            // Arrange
            var opcode = InstructionEmiter.Adcs(R5, R4);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R4] = 0x7fffffff;
            Cpu.Registers[R5] = 0;
            Cpu.Registers.C = true;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be(0x80000000u);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeTrue();
        }

        [Fact]
        public void Should_PropagateCarry_WithoutSettingOverflow_When_OperandsAreZero()
        {
            // Arrange
            var opcode = InstructionEmiter.Adcs(R3, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R2] = 0;
            Cpu.Registers[R3] = 0;
            Cpu.Registers.C = true;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R3].Should().Be(1u);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_SetZeroCarryAndOverflow_When_SelfAddingMinNegative()
        {
            // Arrange
            var opcode = InstructionEmiter.Adcs(R0, R0);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 0x80000000;
            Cpu.Registers.C = false;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeTrue();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeTrue();
        }
    }

    public abstract class Add
    {
        public class SpRelative : CpuTestBase
        {
            [Fact]
            public void Should_IncrementStackPointer_ByImmediate7()
            {
                // Arrange
                var opcode = InstructionEmiter.AddSpImm7(0x10);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers.SP = 0x10000040;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.SP.Should().Be(0x10000050);
            }

            [Fact]
            public void Should_CalculateAddressFromStackPointer_AndStoreInRegister()
            {
                // Arrange
                var opcode = InstructionEmiter.AddSpImm8(R1, 0x10);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers.SP = 0x54;
                Cpu.Registers[R1] = 0;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.SP.Should().Be(0x54);
                Cpu.Registers[R1].Should().Be(0x64);
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_AddHighRegister_To_LowRegister()
            {
                // Arrange
                var opcode = InstructionEmiter.AddHighRegisters(R1, IP);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 66;
                Cpu.Registers[R12] = 44;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(110);
            }

            [Fact]
            public void Should_NotUpdateFlags_When_ResultIsZero()
            {
                // Arrange
                var opcode = InstructionEmiter.AddHighRegisters(R3, R12);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x00002000;
                Cpu.Registers[R12] = 0xffffe000;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R3].Should().Be(0x00000000);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_AddRegisterToStackPointer_And_PreserveFlags()
            {
                // Arrange
                var opcode = InstructionEmiter.AddHighRegisters(SP, R8);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[SP] = 0x20030000;
                Cpu.Registers.Z = true;
                Cpu.Registers[R8] = 0x13;

                // Act
                Cpu.Step();

                // Assert: ARMv6-M §A6.7.2 — ADD SP, Rm does NOT force 4-byte alignment.
                // The raw sum is written to SP.
                Cpu.Registers[SP].Should().Be(0x20030013);
                Cpu.Registers.Z.Should().BeTrue();
            }

            [Fact]
            public void Should_AddRegisterToProgramCounter_And_AlignResult()
            {
                // Arrange
                var opcode = InstructionEmiter.AddHighRegisters(PC, R8);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R8] = 0x11;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[PC].Should().Be(0x20000014);
            }

            [Fact]
            public void Should_ReadProgramCounterAsInstructionAddressPlusFour_When_RmIsPc()
            {
                // Arrange: `add r0, pc` — the base of rustc/LLVM's halfword jump tables
                // (`lsls r0, #1; add r0, pc; ldrh r0, [r0, #4]; lsls r0, #1; add pc, r0`).
                var opcode = InstructionEmiter.AddHighRegisters(R0, PC);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0x10;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R0].Should().Be(0x20000014);
            }

            [Fact]
            public void Should_PreserveFlags_When_UsingEncodingT2_WithLowRegisters()
            {
                // Arrange
                var opcode = InstructionEmiter.AddHighRegisters(R1, R2);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 10;
                Cpu.Registers[R2] = 20;

                Cpu.Registers.N = true;
                Cpu.Registers.Z = true;
                Cpu.Registers.C = true;
                Cpu.Registers.V = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(30);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeTrue();
            }
        }
    }

    public abstract class Adds
    {
        public class Immediate3 : CpuTestBase
        {
            [Fact]
            public void Should_AddSmallImmediate_And_ClearFlags()
            {
                // Arrange
                var opcode = InstructionEmiter.AddsImm3(R1, R2, 3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 2;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(5);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeFalse();
            }
        }

        public class Immediate8 : CpuTestBase
        {
            [Fact]
            public void Should_AddLargeImmediate_And_SetZeroAndCarryFlags_OnUnsignedOverflow()
            {
                // Arrange
                var opcode = InstructionEmiter.AddsImm8(R1, 1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 0xffffffff;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(0);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeTrue();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_AddTwoRegisters_StandardCase()
            {
                // Arrange
                var opcode = InstructionEmiter.AddsRegister(R1, R2, R7);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 2;
                Cpu.Registers[R7] = 27;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(29);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_SetOverflowFlag_When_AddingTwoPositiveNumbers_ResultsInNegative()
            {
                // Arrange
                var opcode = InstructionEmiter.AddsRegister(R4, R4, R2);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R2] = 0x74bc8000;
                Cpu.Registers[R4] = 0x43740000;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R4].Should().Be(0xb8308000);
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeTrue();
            }

            [Fact]
            public void Should_GenerateCarryAndOverflow_OnSelfAddition()
            {
                // Arrange
                var opcode = InstructionEmiter.AddsRegister(R1, R1, R1);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R1] = 0xbf8d1424;
                Cpu.Registers.C = true;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers[R1].Should().Be(0x7f1a2848);
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeTrue();
            }
        }
    }

    public class Adr : CpuTestBase
    {
        [Fact]
        public void Should_CalculateAddress_RelativeToProgramCounter()
        {
            // Arrange
            var opcode = InstructionEmiter.Adr(R4, 0x50);
            Bus.WriteHalfWord(0x20000000, opcode);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R4].Should().Be(0x20000054);
        }
    }

    public class Cmn : CpuTestBase
    {
        [Fact]
        public void Should_UpdateFlags_And_DiscardResult_When_AddingTwoRegisters()
        {
            // Arrange
            const uint negativeTwo = (uint)(-2 & 0xFFFFFFFF);
            var opcode = InstructionEmiter.Cmn(R7, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R2] = 1;
            Cpu.Registers[R7] = negativeTwo;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R2].Should().Be(1);
            Cpu.Registers[R7].Should().Be(negativeTwo);

            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }
    }

    public abstract class Cmp
    {
        public class Immediate : CpuTestBase
        {
            [Fact]
            public void Should_SetNegativeFlag_When_RegisterIsLessThanImmediate()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpImm(R5, 66);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R5] = 60;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_SetCarryFlag_When_RegisterIsGreaterOrEqual_ToImmediate()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpImm(R0, 0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0x80010133;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }
        }

        public class Register : CpuTestBase
        {
            [Fact]
            public void Should_SetCarryFlag_When_RnIsGreaterThanRm()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpRegister(R5, R0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 56;
                Cpu.Registers[R5] = 60;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_ClearCarryFlag_When_UnsignedBorrowOccurs()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpRegister(R2, R0);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R0] = 0xb71b0000;
                Cpu.Registers[R2] = 0x00b71b00;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_SetOverflowFlag_When_SubtractingMinNegativeFromZero()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpRegister(R3, R7);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0;
                Cpu.Registers[R7] = 0x80000000;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeTrue();
            }

            [Fact]
            public void Should_SetCarryFlag_When_SubtractingZeroFromMinNegative()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpRegister(R3, R7);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x80000000;
                Cpu.Registers[R7] = 0;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }
        }

        public class HighRegister : CpuTestBase
        {
            [Fact]
            public void Should_CompareHighAndLowRegisters_And_SetCarry()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpHighRegister(R11, R3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x00000008;
                Cpu.Registers[R11] = 0xffffffff;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_CompareTwoHighRegisters_WithPositiveResult()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpHighRegister(IP, R6);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R6] = 56;
                Cpu.Registers[R12] = 60;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeFalse();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_SetCarryFlag_When_HighRegisterIsNegative_And_ComparedWithZero()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpHighRegister(R11, R3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0;
                Cpu.Registers[R11] = 0x80000000;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeTrue();
                Cpu.Registers.V.Should().BeFalse();
            }

            [Fact]
            public void Should_SetOverflowFlag_When_ZeroIsComparedWith_HighRegisterNegative()
            {
                // Arrange
                var opcode = InstructionEmiter.CmpHighRegister(R11, R3);
                Bus.WriteHalfWord(0x20000000, opcode);

                Cpu.Registers[R3] = 0x80000000;
                Cpu.Registers[R11] = 0;

                // Act
                Cpu.Step();

                // Assert
                Cpu.Registers.N.Should().BeTrue();
                Cpu.Registers.Z.Should().BeFalse();
                Cpu.Registers.C.Should().BeFalse();
                Cpu.Registers.V.Should().BeTrue();
            }
        }
    }

    public class Muls : CpuTestBase
    {
        [Fact]
        public void Should_MultiplyTwoRegisters_And_StoreResult()
        {
            // Arrange
            var opcode = InstructionEmiter.Muls(R0, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 5;
            Cpu.Registers[R2] = 1000000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R2].Should().Be(5000000);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
        }

        [Fact]
        public void Should_WrapAround_When_ResultExceeds32Bits()
        {
            // Arrange
            var opcode = InstructionEmiter.Muls(R0, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 2654435769;
            Cpu.Registers[R2] = 340573321;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R2].Should().Be(1);
        }

        [Fact]
        public void Should_SetZeroFlag_When_MultiplyingByZero()
        {
            // Arrange
            var opcode = InstructionEmiter.Muls(R0, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 0;
            Cpu.Registers[R2] = 1000000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R2].Should().Be(0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeTrue();
        }

        [Fact]
        public void Should_SetNegativeFlag_When_ResultIsInterpretedAsSignedNegative()
        {
            // Arrange
            var opcode = InstructionEmiter.Muls(R0, R2);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R0] = 0xFFFFFFFF;
            Cpu.Registers[R2] = 1000000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R2].Should().Be((uint)(-1000000 & 0xFFFFFFFF));
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
        }
    }

    public class Rsbs : CpuTestBase
    {
        [Fact]
        public void Should_NegateRegisterAndSetFlags_When_SubtractingFromZero()
        {
            // Arrange
            var opcode = InstructionEmiter.Rsbs(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R3] = 100;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(unchecked((uint)-100));
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_SetZeroAndCarryFlags_When_NegatingZero()
        {
            // Arrange
            var opcode = InstructionEmiter.Rsbs(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);

            Cpu.Registers[R3] = 0;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R0].Should().Be(0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeTrue();
            Cpu.Registers.C.Should().BeTrue(); // No borrow en 0-0
            Cpu.Registers.V.Should().BeFalse();
        }
    }

    public class Sub : CpuTestBase
    {
        [Fact]
        public void Should_Execute_SubSpInstruction()
        {
            // Arrange
            var opcode = InstructionEmiter.SubSp(0x10);
            Cpu.Registers.SP = 0x10000040;
            Bus.WriteHalfWord(0x20000000, opcode);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.SP.Should().Be(0x10000030);
        }
    }

    public class Subs : CpuTestBase
    {
        [Fact]
        public void Should_Execute_SubsR1_With_Overflow()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsImm8(R1, 1);
            Cpu.Registers[R1] = unchecked((uint)-0x80000000);
            Bus.WriteHalfWord(0x20000000, opcode);

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R1].Should().Be(0x7fffffff);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeTrue();
        }

        [Fact]
        public void Should_Execute_SubsR5R3()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsImm3(R5, R3, 5);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R3 = 0;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers[R5].Should().Be((uint)(-5 & uint.MaxValue));
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_Execute_SubsR5R3R2()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsReg(R5, R3, R2);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R3 = 6;
            Cpu.Registers.R2 = 5;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R5.Should().Be(1);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_Execute_SubsR3R3R2()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsReg(R3, R3, R2);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R2 = 8;
            Cpu.Registers.R3 = 0xffffffff;

            // Act
            Cpu.Step();

            // Arrange
            Cpu.Registers.R3.Should().Be(0xfffffff7);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_Execute_SubsR5R3R2_And_Set_NVFlags()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsReg(R5, R3, R2);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R3 = 0;
            Cpu.Registers.R2 = 0x80000000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R5.Should().Be(0x80000000);
            Cpu.Registers.N.Should().BeTrue();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeTrue();
        }

        [Fact]
        public void Should_Execute_SubsR5R3R2_And_Set_ZCFlags()
        {
            // Arrange
            var opcode = InstructionEmiter.SubsReg(R3, R3, R2);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R3 = 0x80000000;
            Cpu.Registers.R2 = 0x80000000;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R5.Should().Be(0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeTrue();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeFalse();
        }
    }

    public class Sbcs : CpuTestBase
    {
        [Fact]
        public void Should_Execute_SbcsR0R3()
        {
            // Arrange
            var opcode = InstructionEmiter.Sbcs(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R0 = 100;
            Cpu.Registers.R3 = 55;
            Cpu.Registers.C = false;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R0.Should().Be(44);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeFalse();
            Cpu.Registers.C.Should().BeTrue();
            Cpu.Registers.V.Should().BeFalse();
        }

        [Fact]
        public void Should_Execute_SbcsR0R3_To_Be_0()
        {
            // Arrange
            var opcode = InstructionEmiter.Sbcs(R0, R3);
            Bus.WriteHalfWord(0x20000000, opcode);
            Cpu.Registers.R0 = 0;
            Cpu.Registers.R3 = 0xffffffff;
            Cpu.Registers.C = false;

            // Act
            Cpu.Step();

            // Assert
            Cpu.Registers.R0.Should().Be(0);
            Cpu.Registers.N.Should().BeFalse();
            Cpu.Registers.Z.Should().BeTrue();
            Cpu.Registers.C.Should().BeFalse();
            Cpu.Registers.V.Should().BeFalse();
        }
    }
}

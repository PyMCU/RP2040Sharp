using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RP2040.Core.Cpu.Instructions;
using unsafe InstructionHandler = delegate* managed<ushort, RP2040.Core.Cpu.CortexM0Plus, void>;

namespace RP2040.Core.Cpu;

// If I make this static make sure it is not disposable
public sealed unsafe class InstructionDecoder : IDisposable
{
    public static InstructionDecoder Instance { get; } = new InstructionDecoder();

    private readonly InstructionHandler* _fastTablePtr;

    /// <summary>Raw native <c>delegate*</c> table, keyed by 16-bit opcode. Consumed by
    /// <see cref="Decoders.NativeLutDecoder"/> so the monomorphized run loop can index it directly.</summary>
    public InstructionHandler* Table => _fastTablePtr;

    bool _disposed;

    private readonly struct OpcodeRule(ushort mask, ushort pattern, InstructionHandler handler)
    {
        public readonly ushort Mask = mask;
        public readonly ushort Pattern = pattern;
        public readonly InstructionHandler Handler = handler;
    }

    public InstructionDecoder()
    {
        _fastTablePtr = (InstructionHandler*)NativeMemory.AllocZeroed(65536, (nuint)sizeof(InstructionHandler));

        InstructionHandler undefinedPtr = &HandleUndefined;
        new Span<nuint>(_fastTablePtr, 65536).Fill((nuint)undefinedPtr);

        ReadOnlySpan<OpcodeRule> rules =
        [
            // ================================================================
            // GROUP 1: Mask 0xFFFF (Exact Match - Max Priority)
            // ================================================================
            // MRS Rd, spec_reg (F3EF)
            new OpcodeRule(0xFFFF, 0xF3EF, &SystemOps.Mrs),
            // DMB, DSB, ISB (F3BF)
            // Mask: 1111 1111 1111 1111
            new OpcodeRule(0xFFFF, 0xF3BF, &SystemOps.Barrier),
            // CPSIE i (enable interrupts)
            new OpcodeRule(0xFFFF, 0xB662, &SystemOps.Cpsie),
            // CPSID i (disable interrupts)
            new OpcodeRule(0xFFFF, 0xB672, &SystemOps.Cpsid),
            // NOP — must be exact match to prevent CBNZ(mask 0xFB00) from overriding
            new OpcodeRule(0xFFFF, 0xBF00, &SystemOps.Nop),
            // SEV
            new OpcodeRule(0xFFFF, 0xBF40, &SystemOps.Sev),
            // WFE
            new OpcodeRule(0xFFFF, 0xBF20, &SystemOps.Wfe),
            // WFI
            new OpcodeRule(0xFFFF, 0xBF30, &SystemOps.Wfi),
            // ================================================================
            // GROUP 2: Mask 0xFFF0
            // ================================================================
            // MSR spec_reg, Rn (F38x)
            new OpcodeRule(0xFFF0, 0xF380, &SystemOps.Msr),
            // CLZ Rd, Rm (Thumb-2 32-bit: first halfword 0xFABx)
            new OpcodeRule(0xFFF0, 0xFAB0, &BitOps.Clz),
            // ================================================================
            // GROUP 3: Mask 0xFFC0 (10 bits significant)
            // IMPORTANT: Must come before 0xFF00 to prevent generic instructions
            // (like ORRS or ADD generic) from shadowing these specific opcodes.
            // ================================================================
            // ADCS (Rd, Rm)
            new OpcodeRule(0xFFC0, 0x4140, &ArithmeticOps.Adcs),
            // ANDS (Rn, Rm)
            new OpcodeRule(0xFFC0, 0x4000, &BitOps.Ands),
            // ASRS (Register) - Encoding T2
            new OpcodeRule(0xFFC0, 0x4100, &BitOps.AsrsRegister),
            // BICS (Rdn, Rm)
            new OpcodeRule(0xFFC0, 0x4380, &BitOps.Bics),
            // CMN (Rn, Rm)
            new OpcodeRule(0xFFC0, 0x42C0, &ArithmeticOps.Cmn),
            // TST Rn, Rm (Test bits)
            new OpcodeRule(0xFFC0, 0x4200, &BitOps.Tst),
            // RSBS Rd, Rn, #0 (Negate)
            new OpcodeRule(0xFFC0, 0x4240, &ArithmeticOps.Rsbs),
            // CMP Rn, Rm (Low Registers - Encoding T1)
            new OpcodeRule(0xFFC0, 0x4280, &ArithmeticOps.CmpRegister),
            // EORS Rdn, Rm
            new OpcodeRule(0xFFC0, 0x4040, &BitOps.Eors),
            // MULS Rn, Rdm - (Must be before ORRS 0x4300)
            new OpcodeRule(0xFFC0, 0x4340, &ArithmeticOps.Muls),
            // MVNS Rd, Rm
            new OpcodeRule(0xFFC0, 0x43C0, &BitOps.Mvns),
            // LSRS (Register) - Encoding T2
            new OpcodeRule(0xFFC0, 0x40C0, &BitOps.LsrsRegister),
            // LSLS (Register) - Encoding T2
            new OpcodeRule(0xFFC0, 0x4080, &BitOps.LslsRegister),
            // LSLS Rd, Rm, #0
            new OpcodeRule(0xFFC0, 0x0000, &BitOps.LslsZero),
            // LSRS Rd, Rm, #0 (Shift 32)
            new OpcodeRule(0xFFC0, 0x0800, &BitOps.LsrsImm32),
            // Rev16 Rd, Rn
            new OpcodeRule(0xFFC0, 0xBA40, &BitOps.Rev16),
            // REVSH Rd, Rm
            new OpcodeRule(0xFFC0, 0xBAC0, &BitOps.Revsh),
            // REV (Rd, Rn)
            new OpcodeRule(0xFFC0, 0xBA00, &BitOps.Rev),
            // SBCS (Rn, Rm)
            new OpcodeRule(0xFFC0, 0x4180, &ArithmeticOps.Sbcs),
            // ROR (register)
            new OpcodeRule(0xFFC0, 0x41C0, &BitOps.Ror),
            // SXTH Rd, Rm
            new OpcodeRule(0xFFC0, 0xB200, &BitOps.Sxth),
            // SXTB Rd, Rm
            new OpcodeRule(0xFFC0, 0xB240, &BitOps.Sxtb),
            // UXTH Rd, Rm
            new OpcodeRule(0xFFC0, 0xB280, &BitOps.Uxth),
            // UXTB Rd, Rm
            new OpcodeRule(0xFFC0, 0xB2C0, &BitOps.Uxtb),
            // ================================================================
            // GROUP 4: Mask 0xFF87 (High Register Special Cases)
            // CRITICAL: These are specific cases of the 0xFF00 generic group.
            // They verify Bit 7 (DN) and Bits 0-2 (Rd/Rm).
            // ================================================================
            // 1. High Priority: ADD PC, Rm (R15)
            new OpcodeRule(0xFF87, 0x4487, &ArithmeticOps.AddHighToPc),
            // 2. High Priority: ADD SP, Rm (R13)
            new OpcodeRule(0xFF87, 0x4485, &ArithmeticOps.AddHighToSp),
            // BLX Rm
            new OpcodeRule(0xFF87, 0x4780, &FlowOps.Blx),
            // BX Rm
            new OpcodeRule(0xFF87, 0x4700, &FlowOps.Bx),
            // 1. MOV PC, Rm (High Priority)
            new OpcodeRule(0xFF87, 0x4687, &BitOps.MovToPc),
            // 2. MOV SP, Rm (High Priority)
            new OpcodeRule(0xFF87, 0x4685, &BitOps.MovToSp),
            // ================================================================
            // GROUP 5: Mask 0xFF80
            // ================================================================
            // ADD (SP + imm7)
            new OpcodeRule(0xFF80, 0xB000, &ArithmeticOps.AddSpImmediate7),
            // Sub (SP - imm)
            new OpcodeRule(0xFF80, 0xB080, &ArithmeticOps.SubSp),
            // Stack Operations — must be before CBZ/CBNZ (0xF900 mask) since 0xFF00 is more specific
            // but CBZ/CBNZ would match 0xB5xx (PUSH with LR) due to broader mask 0xF900
            new OpcodeRule(0xFF00, 0xBC00, &MemoryOps.Pop),
            new OpcodeRule(0xFF00, 0xBD00, &MemoryOps.PopPc),
            new OpcodeRule(0xFF00, 0xB400, &MemoryOps.Push),
            new OpcodeRule(0xFF00, 0xB500, &MemoryOps.PushLr),
            // ================================================================
            // GROUP 5b: Mask 0xFB00 — CBZ / CBNZ
            // NOTE: Hint instructions (NOP/WFE/WFI/SEV) overlap with CBNZ(i=1)
            // but are already registered in Group 1 with exact match, so they
            // take priority in the lookup table.
            // ================================================================
            // CBZ  Rn, label  (0xB1xx i=0, 0xB3xx i=1)
            new OpcodeRule(0xF900, 0xB100, &FlowOps.Cbz),
            // CBNZ Rn, label (0xB9xx i=0, 0xBBxx i=1)
            new OpcodeRule(0xF900, 0xB900, &FlowOps.Cbnz),
            // ================================================================
            // GROUP 6: Mask 0xFF00 (8 bits significant - Broad Categories)
            // ================================================================
            // BKPT #imm8 — must be before NOP group (0xBF00) and hint range
            new OpcodeRule(0xFF00, 0xBE00, &SystemOps.Bkpt),
            // SVC #imm8 — must be before conditional branches (0xDF00 range)
            new OpcodeRule(0xFF00, 0xDF00, &SystemOps.Svc),
            // 3. Low Priority: ADD Generic (R0-R12, R14)
            new OpcodeRule(0xFF00, 0x4400, &ArithmeticOps.AddHighToReg),
            // CMP Rn, Rm (High Registers - Encoding T2)
            new OpcodeRule(0xFF00, 0x4500, &ArithmeticOps.CmpHighRegister),
            // 3. MOV Rd, Rm (Generic Fallback)
            new OpcodeRule(0xFF00, 0x4600, &BitOps.MovRegister),
            // MULS (0x4340) and MVNS (0x43C0) are subsets and were handled above
            // ORRS (Rd, Rm) - NOTE: This covers 0x4300-0x43FF.
            new OpcodeRule(0xFF00, 0x4300, &BitOps.Orrs),
            // Stack Operations
            new OpcodeRule(0xFF00, 0xBC00, &MemoryOps.Pop),
            new OpcodeRule(0xFF00, 0xBD00, &MemoryOps.PopPc),
            // Conditional Branches (T1)
            // SVC (0xDF00) is technically caught here if not handled separately.
            // Ensure the handler filters 0xF (SVC) or add a specific SVC rule with higher priority.
            new OpcodeRule(0xFF00, 0xD000, &FlowOps.Beq),
            new OpcodeRule(0xFF00, 0xD100, &FlowOps.Bne),
            new OpcodeRule(0xFF00, 0xD200, &FlowOps.Bcs),
            new OpcodeRule(0xFF00, 0xD300, &FlowOps.Bcc),
            new OpcodeRule(0xFF00, 0xD400, &FlowOps.Bmi),
            new OpcodeRule(0xFF00, 0xD500, &FlowOps.Bpl),
            new OpcodeRule(0xFF00, 0xD600, &FlowOps.Bvs),
            new OpcodeRule(0xFF00, 0xD700, &FlowOps.Bvc),
            new OpcodeRule(0xFF00, 0xD800, &FlowOps.Bhi),
            new OpcodeRule(0xFF00, 0xD900, &FlowOps.Bls),
            new OpcodeRule(0xFF00, 0xDA00, &FlowOps.Bge),
            new OpcodeRule(0xFF00, 0xDB00, &FlowOps.Blt),
            new OpcodeRule(0xFF00, 0xDC00, &FlowOps.Bgt),
            new OpcodeRule(0xFF00, 0xDD00, &FlowOps.Ble),
            // ================================================================
            // GROUP 7: Mask 0xFE00
            // ================================================================
            // ADDS (Rd, Rn, Rm) - Encoding T1 Register
            new OpcodeRule(0xFE00, 0x1800, &ArithmeticOps.AddsRegister),
            // ADDS (Rd, Rn, imm3)
            new OpcodeRule(0xFE00, 0x1C00, &ArithmeticOps.AddsImmediate3),
            // SUBS (imm3)
            new OpcodeRule(0xFE00, 0x1E00, &ArithmeticOps.SubsImmediate3),
            // SUBS (Register)
            new OpcodeRule(0xFE00, 0x1A00, &ArithmeticOps.SubsRegister),
            // LDR (register)
            new OpcodeRule(0xFE00, 0x5800, &MemoryOps.LdrRegister),
            // STR (register)
            new OpcodeRule(0xFE00, 0x5000, &MemoryOps.StrRegister),
            // STRH (register)
            new OpcodeRule(0xFE00, 0x5200, &MemoryOps.StrhRegister),
            // STRB (register)
            new OpcodeRule(0xFE00, 0x5400, &MemoryOps.StrbRegister),
            // LDRSB (register, sign-extend)
            new OpcodeRule(0xFE00, 0x5600, &MemoryOps.Ldrsb),
            // LDRH (register)
            new OpcodeRule(0xFE00, 0x5A00, &MemoryOps.LdrhRegister),
            // LDRB (register)
            new OpcodeRule(0xFE00, 0x5C00, &MemoryOps.LdrbRegister),
            // LDRSH (register, sign-extend)
            new OpcodeRule(0xFE00, 0x5E00, &MemoryOps.Ldrsh),
            // ================================================================
            // GROUP 8: Mask 0xF800 (5 bits significant - Most Generic)
            // ================================================================
            // ADD (Rd = SP + imm8)
            new OpcodeRule(0xF800, 0xA800, &ArithmeticOps.AddSpImmediate8),
            // ADDS (Rd, imm8)
            new OpcodeRule(0xF800, 0x3000, &ArithmeticOps.AddsImmediate8),
            // SUBS (imm8)
            new OpcodeRule(0xF800, 0x3800, &ArithmeticOps.SubsImmediate8),
            // ADR (Rd, imm8)
            new OpcodeRule(0xF800, 0xA000, &ArithmeticOps.Adr),
            // ASRS (Rd, Rm, imm5)
            new OpcodeRule(0xF800, 0x1000, &BitOps.AsrsImm5),
            // BL (Branch with Link)
            new OpcodeRule(0xF800, 0xF000, &FlowOps.Bl),
            // B (Unconditional) - T2
            new OpcodeRule(0xF800, 0xE000, &FlowOps.Branch),
            // CMP Rn, #imm8
            new OpcodeRule(0xF800, 0x2800, &ArithmeticOps.CmpImmediate),
            // MOVS (Rd, #imm8)
            new OpcodeRule(0xF800, 0x2000, &BitOps.Movs),
            // LDMIA (Load Multiple Increment After)
            new OpcodeRule(0xF800, 0xC800, &MemoryOps.Ldmia),
            // STMIA (Store Multiple Increment After)
            new OpcodeRule(0xF800, 0xC000, &MemoryOps.Stmia),
            // LDR (literal)
            new OpcodeRule(0xF800, 0x4800, &MemoryOps.LdrLiteral),
            // LDR (imm5)
            new OpcodeRule(0xF800, 0x6800, &MemoryOps.LdrImmediate),
            // LDR (SP, imm8)
            new OpcodeRule(0xF800, 0x9800, &MemoryOps.LdrSpRelative),
            // STR (imm5)
            new OpcodeRule(0xF800, 0x6000, &MemoryOps.StrImmediate),
            // STR (SP + imm8)
            new OpcodeRule(0xF800, 0x9000, &MemoryOps.StrSpRelative),
            // STRB (imm5)
            new OpcodeRule(0xF800, 0x7000, &MemoryOps.StrbImmediate),
            // STRH (imm5)
            new OpcodeRule(0xF800, 0x8000, &MemoryOps.StrhImmediate),
            // LDRB (imm5)
            new OpcodeRule(0xF800, 0x7800, &MemoryOps.LdrbImmediate),
            // LDRH (imm5)
            new OpcodeRule(0xF800, 0x8800, &MemoryOps.LdrhImmediate),
            // LSLS (Rd, Rm, imm5)
            new OpcodeRule(0xF800, 0x0000, &BitOps.LslsImm5),
            // LSRS (Rd, Rm, imm5)
            new OpcodeRule(0xF800, 0x0800, &BitOps.LsrsImm5),
            // ================================================================
            // GROUP 9: Mask 0xBF00
            // ================================================================
            // NOP (Hint)
            new OpcodeRule(0xBF00, 0xBF00, &SystemOps.Nop),
        ];

        for (var i = 0; i < 65536; i++)
        {
            var opcode = (ushort)i;
            foreach (ref readonly var rule in rules)
            {
                if ((opcode & rule.Mask) != rule.Pattern)
                    continue;
                _fastTablePtr[i] = rule.Handler;
                break;
            }
        }
    }

    [ExcludeFromCodeCoverage]
    ~InstructionDecoder()
    {
        Dispose(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispatch(ushort opcode, CortexM0Plus cpu)
    {
        _fastTablePtr[opcode](opcode, cpu);
    }

    public nuint GetHandler(ushort opcode)
    {
        return (nuint)_fastTablePtr[opcode];
    }

    private static void HandleUndefined(ushort opcode, CortexM0Plus cpu)
    {
        // ARMv6-M B1.5.6: executing an UNDEFINED encoding raises HardFault.
        // Do not throw a C# exception — let the handler vector take over.
        System.Console.Error.WriteLine($"Undefined instruction 0x{opcode:X4} at PC=0x{cpu.Registers.PC:X8}");
        cpu.TriggerHardFault();
    }

    [ExcludeFromCodeCoverage]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // Implement the virtual dispose for explicit dispose, it is a good practice to ensure resources are released
    [ExcludeFromCodeCoverage]
    private void Dispose(bool disposing)
    {
        _ = disposing;
        if (_disposed)
            return;

        NativeMemory.Free(_fastTablePtr);

        _disposed = true;
    }
}

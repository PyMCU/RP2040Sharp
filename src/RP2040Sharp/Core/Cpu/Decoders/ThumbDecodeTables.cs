using RP2040.Core.Cpu.Instructions;
using unsafe InstructionHandler = delegate* managed<ushort, RP2040.Core.Cpu.CortexM0Plus, void>;

namespace RP2040.Core.Cpu.Decoders;

/// <summary>Managed handler signature for a decoded 16-bit Thumb instruction.</summary>
public delegate void OpcodeAction(ushort opcode, CortexM0Plus cpu);

/// <summary>Stable identifier for every distinct 16-bit handler (plus the undefined catch-all). Used as
/// the payload of <see cref="ThumbDecodeTables.IdTable"/> so the <see cref="SwitchDecoder"/> can br_table
/// over it, and as the index into the managed handler map.</summary>
public enum HandlerId : ushort
{
        Undefined,
        SystemOps_Mrs,
        SystemOps_Barrier,
        SystemOps_Cpsie,
        SystemOps_Cpsid,
        SystemOps_Nop,
        SystemOps_Sev,
        SystemOps_Wfe,
        SystemOps_Wfi,
        SystemOps_Msr,
        BitOps_Clz,
        ArithmeticOps_Adcs,
        BitOps_Ands,
        BitOps_AsrsRegister,
        BitOps_Bics,
        ArithmeticOps_Cmn,
        BitOps_Tst,
        ArithmeticOps_Rsbs,
        ArithmeticOps_CmpRegister,
        BitOps_Eors,
        ArithmeticOps_Muls,
        BitOps_Mvns,
        BitOps_LsrsRegister,
        BitOps_LslsRegister,
        BitOps_LslsZero,
        BitOps_LsrsImm32,
        BitOps_Rev16,
        BitOps_Revsh,
        BitOps_Rev,
        ArithmeticOps_Sbcs,
        BitOps_Ror,
        BitOps_Sxth,
        BitOps_Sxtb,
        BitOps_Uxth,
        BitOps_Uxtb,
        ArithmeticOps_AddHighToPc,
        ArithmeticOps_AddHighToSp,
        FlowOps_Blx,
        FlowOps_Bx,
        BitOps_MovToPc,
        BitOps_MovToSp,
        ArithmeticOps_AddSpImmediate7,
        ArithmeticOps_SubSp,
        MemoryOps_Pop,
        MemoryOps_PopPc,
        MemoryOps_Push,
        MemoryOps_PushLr,
        FlowOps_Cbz,
        FlowOps_Cbnz,
        SystemOps_Bkpt,
        SystemOps_Svc,
        ArithmeticOps_AddHighToReg,
        ArithmeticOps_CmpHighRegister,
        BitOps_MovRegister,
        BitOps_Orrs,
        FlowOps_Beq,
        FlowOps_Bne,
        FlowOps_Bcs,
        FlowOps_Bcc,
        FlowOps_Bmi,
        FlowOps_Bpl,
        FlowOps_Bvs,
        FlowOps_Bvc,
        FlowOps_Bhi,
        FlowOps_Bls,
        FlowOps_Bge,
        FlowOps_Blt,
        FlowOps_Bgt,
        FlowOps_Ble,
        ArithmeticOps_AddsRegister,
        ArithmeticOps_AddsImmediate3,
        ArithmeticOps_SubsImmediate3,
        ArithmeticOps_SubsRegister,
        MemoryOps_LdrRegister,
        MemoryOps_StrRegister,
        MemoryOps_StrhRegister,
        MemoryOps_StrbRegister,
        MemoryOps_Ldrsb,
        MemoryOps_LdrhRegister,
        MemoryOps_LdrbRegister,
        MemoryOps_Ldrsh,
        ArithmeticOps_AddSpImmediate8,
        ArithmeticOps_AddsImmediate8,
        ArithmeticOps_SubsImmediate8,
        ArithmeticOps_Adr,
        BitOps_AsrsImm5,
        FlowOps_Bl,
        FlowOps_Branch,
        ArithmeticOps_CmpImmediate,
        BitOps_Movs,
        MemoryOps_Ldmia,
        MemoryOps_Stmia,
        MemoryOps_LdrLiteral,
        MemoryOps_LdrImmediate,
        MemoryOps_LdrSpRelative,
        MemoryOps_StrImmediate,
        MemoryOps_StrSpRelative,
        MemoryOps_StrbImmediate,
        MemoryOps_StrhImmediate,
        MemoryOps_LdrbImmediate,
        MemoryOps_LdrhImmediate,
        BitOps_LslsImm5,
        BitOps_LsrsImm5,
}

/// <summary>
/// Shared decode tables for the interchangeable 16-bit decoders. The classification rules (mask/pattern
/// → handler) live here as <see cref="Rules"/>, a copy of the SAME (mask, pattern, method) list the native
/// <c>delegate*</c> table in <see cref="InstructionDecoder"/> is built from — first match wins. From this
/// single rules list, by re-running the exact same match order over every opcode, this class derives:
///   • <see cref="IdTable"/>      — 65536 handler ids (payload for the switch/br_table decoder)
///   • <see cref="ManagedTable"/> — 65536 managed <see cref="OpcodeAction"/> delegates (the portable LUT)
/// Deriving from the rules (not by reverse-mapping native pointers) is required for correctness under
/// Mono/WASM AOT, where managed function-pointer identity is not stable.
/// </summary>
public static unsafe class ThumbDecodeTables
{
    private readonly struct Rule(ushort mask, ushort pattern, HandlerId id)
    {
        public readonly ushort Mask = mask;
        public readonly ushort Pattern = pattern;
        public readonly HandlerId Id = id;
    }

    // Same order and same (mask, pattern) as InstructionDecoder's native rule list — first match wins.
    private static readonly Rule[] Rules =
    [
        new(0xFFFF, 0xF3EF, HandlerId.SystemOps_Mrs),
        new(0xFFFF, 0xF3BF, HandlerId.SystemOps_Barrier),
        new(0xFFFF, 0xB662, HandlerId.SystemOps_Cpsie),
        new(0xFFFF, 0xB672, HandlerId.SystemOps_Cpsid),
        new(0xFFFF, 0xBF00, HandlerId.SystemOps_Nop),
        new(0xFFFF, 0xBF40, HandlerId.SystemOps_Sev),
        new(0xFFFF, 0xBF20, HandlerId.SystemOps_Wfe),
        new(0xFFFF, 0xBF30, HandlerId.SystemOps_Wfi),
        new(0xFFF0, 0xF380, HandlerId.SystemOps_Msr),
        new(0xFFF0, 0xFAB0, HandlerId.BitOps_Clz),
        new(0xFFC0, 0x4140, HandlerId.ArithmeticOps_Adcs),
        new(0xFFC0, 0x4000, HandlerId.BitOps_Ands),
        new(0xFFC0, 0x4100, HandlerId.BitOps_AsrsRegister),
        new(0xFFC0, 0x4380, HandlerId.BitOps_Bics),
        new(0xFFC0, 0x42C0, HandlerId.ArithmeticOps_Cmn),
        new(0xFFC0, 0x4200, HandlerId.BitOps_Tst),
        new(0xFFC0, 0x4240, HandlerId.ArithmeticOps_Rsbs),
        new(0xFFC0, 0x4280, HandlerId.ArithmeticOps_CmpRegister),
        new(0xFFC0, 0x4040, HandlerId.BitOps_Eors),
        new(0xFFC0, 0x4340, HandlerId.ArithmeticOps_Muls),
        new(0xFFC0, 0x43C0, HandlerId.BitOps_Mvns),
        new(0xFFC0, 0x40C0, HandlerId.BitOps_LsrsRegister),
        new(0xFFC0, 0x4080, HandlerId.BitOps_LslsRegister),
        new(0xFFC0, 0x0000, HandlerId.BitOps_LslsZero),
        new(0xFFC0, 0x0800, HandlerId.BitOps_LsrsImm32),
        new(0xFFC0, 0xBA40, HandlerId.BitOps_Rev16),
        new(0xFFC0, 0xBAC0, HandlerId.BitOps_Revsh),
        new(0xFFC0, 0xBA00, HandlerId.BitOps_Rev),
        new(0xFFC0, 0x4180, HandlerId.ArithmeticOps_Sbcs),
        new(0xFFC0, 0x41C0, HandlerId.BitOps_Ror),
        new(0xFFC0, 0xB200, HandlerId.BitOps_Sxth),
        new(0xFFC0, 0xB240, HandlerId.BitOps_Sxtb),
        new(0xFFC0, 0xB280, HandlerId.BitOps_Uxth),
        new(0xFFC0, 0xB2C0, HandlerId.BitOps_Uxtb),
        new(0xFF87, 0x4487, HandlerId.ArithmeticOps_AddHighToPc),
        new(0xFF87, 0x4485, HandlerId.ArithmeticOps_AddHighToSp),
        new(0xFF87, 0x4780, HandlerId.FlowOps_Blx),
        new(0xFF87, 0x4700, HandlerId.FlowOps_Bx),
        new(0xFF87, 0x4687, HandlerId.BitOps_MovToPc),
        new(0xFF87, 0x4685, HandlerId.BitOps_MovToSp),
        new(0xFF80, 0xB000, HandlerId.ArithmeticOps_AddSpImmediate7),
        new(0xFF80, 0xB080, HandlerId.ArithmeticOps_SubSp),
        new(0xFF00, 0xBC00, HandlerId.MemoryOps_Pop),
        new(0xFF00, 0xBD00, HandlerId.MemoryOps_PopPc),
        new(0xFF00, 0xB400, HandlerId.MemoryOps_Push),
        new(0xFF00, 0xB500, HandlerId.MemoryOps_PushLr),
        new(0xF900, 0xB100, HandlerId.FlowOps_Cbz),
        new(0xF900, 0xB900, HandlerId.FlowOps_Cbnz),
        new(0xFF00, 0xBE00, HandlerId.SystemOps_Bkpt),
        new(0xFF00, 0xDF00, HandlerId.SystemOps_Svc),
        new(0xFF00, 0x4400, HandlerId.ArithmeticOps_AddHighToReg),
        new(0xFF00, 0x4500, HandlerId.ArithmeticOps_CmpHighRegister),
        new(0xFF00, 0x4600, HandlerId.BitOps_MovRegister),
        new(0xFF00, 0x4300, HandlerId.BitOps_Orrs),
        new(0xFF00, 0xBC00, HandlerId.MemoryOps_Pop),
        new(0xFF00, 0xBD00, HandlerId.MemoryOps_PopPc),
        new(0xFF00, 0xD000, HandlerId.FlowOps_Beq),
        new(0xFF00, 0xD100, HandlerId.FlowOps_Bne),
        new(0xFF00, 0xD200, HandlerId.FlowOps_Bcs),
        new(0xFF00, 0xD300, HandlerId.FlowOps_Bcc),
        new(0xFF00, 0xD400, HandlerId.FlowOps_Bmi),
        new(0xFF00, 0xD500, HandlerId.FlowOps_Bpl),
        new(0xFF00, 0xD600, HandlerId.FlowOps_Bvs),
        new(0xFF00, 0xD700, HandlerId.FlowOps_Bvc),
        new(0xFF00, 0xD800, HandlerId.FlowOps_Bhi),
        new(0xFF00, 0xD900, HandlerId.FlowOps_Bls),
        new(0xFF00, 0xDA00, HandlerId.FlowOps_Bge),
        new(0xFF00, 0xDB00, HandlerId.FlowOps_Blt),
        new(0xFF00, 0xDC00, HandlerId.FlowOps_Bgt),
        new(0xFF00, 0xDD00, HandlerId.FlowOps_Ble),
        new(0xFE00, 0x1800, HandlerId.ArithmeticOps_AddsRegister),
        new(0xFE00, 0x1C00, HandlerId.ArithmeticOps_AddsImmediate3),
        new(0xFE00, 0x1E00, HandlerId.ArithmeticOps_SubsImmediate3),
        new(0xFE00, 0x1A00, HandlerId.ArithmeticOps_SubsRegister),
        new(0xFE00, 0x5800, HandlerId.MemoryOps_LdrRegister),
        new(0xFE00, 0x5000, HandlerId.MemoryOps_StrRegister),
        new(0xFE00, 0x5200, HandlerId.MemoryOps_StrhRegister),
        new(0xFE00, 0x5400, HandlerId.MemoryOps_StrbRegister),
        new(0xFE00, 0x5600, HandlerId.MemoryOps_Ldrsb),
        new(0xFE00, 0x5A00, HandlerId.MemoryOps_LdrhRegister),
        new(0xFE00, 0x5C00, HandlerId.MemoryOps_LdrbRegister),
        new(0xFE00, 0x5E00, HandlerId.MemoryOps_Ldrsh),
        new(0xF800, 0xA800, HandlerId.ArithmeticOps_AddSpImmediate8),
        new(0xF800, 0x3000, HandlerId.ArithmeticOps_AddsImmediate8),
        new(0xF800, 0x3800, HandlerId.ArithmeticOps_SubsImmediate8),
        new(0xF800, 0xA000, HandlerId.ArithmeticOps_Adr),
        new(0xF800, 0x1000, HandlerId.BitOps_AsrsImm5),
        new(0xF800, 0xF000, HandlerId.FlowOps_Bl),
        new(0xF800, 0xE000, HandlerId.FlowOps_Branch),
        new(0xF800, 0x2800, HandlerId.ArithmeticOps_CmpImmediate),
        new(0xF800, 0x2000, HandlerId.BitOps_Movs),
        new(0xF800, 0xC800, HandlerId.MemoryOps_Ldmia),
        new(0xF800, 0xC000, HandlerId.MemoryOps_Stmia),
        new(0xF800, 0x4800, HandlerId.MemoryOps_LdrLiteral),
        new(0xF800, 0x6800, HandlerId.MemoryOps_LdrImmediate),
        new(0xF800, 0x9800, HandlerId.MemoryOps_LdrSpRelative),
        new(0xF800, 0x6000, HandlerId.MemoryOps_StrImmediate),
        new(0xF800, 0x9000, HandlerId.MemoryOps_StrSpRelative),
        new(0xF800, 0x7000, HandlerId.MemoryOps_StrbImmediate),
        new(0xF800, 0x8000, HandlerId.MemoryOps_StrhImmediate),
        new(0xF800, 0x7800, HandlerId.MemoryOps_LdrbImmediate),
        new(0xF800, 0x8800, HandlerId.MemoryOps_LdrhImmediate),
        new(0xF800, 0x0000, HandlerId.BitOps_LslsImm5),
        new(0xF800, 0x0800, HandlerId.BitOps_LsrsImm5),
        new(0xBF00, 0xBF00, HandlerId.SystemOps_Nop),
    ];

    /// <summary>65536-entry table of handler ids, indexed by 16-bit opcode.</summary>
    public static readonly ushort[] IdTable = new ushort[65536];

    /// <summary>65536-entry table of managed handler delegates, indexed by 16-bit opcode.</summary>
    public static readonly OpcodeAction[] ManagedTable = new OpcodeAction[65536];

    static ThumbDecodeTables()
    {
        var managedById = new OpcodeAction[System.Enum.GetValues<HandlerId>().Length];
        managedById[(int)HandlerId.Undefined] = Undefined;
        managedById[(int)HandlerId.SystemOps_Mrs] = SystemOps.Mrs;
        managedById[(int)HandlerId.SystemOps_Barrier] = SystemOps.Barrier;
        managedById[(int)HandlerId.SystemOps_Cpsie] = SystemOps.Cpsie;
        managedById[(int)HandlerId.SystemOps_Cpsid] = SystemOps.Cpsid;
        managedById[(int)HandlerId.SystemOps_Nop] = SystemOps.Nop;
        managedById[(int)HandlerId.SystemOps_Sev] = SystemOps.Sev;
        managedById[(int)HandlerId.SystemOps_Wfe] = SystemOps.Wfe;
        managedById[(int)HandlerId.SystemOps_Wfi] = SystemOps.Wfi;
        managedById[(int)HandlerId.SystemOps_Msr] = SystemOps.Msr;
        managedById[(int)HandlerId.BitOps_Clz] = BitOps.Clz;
        managedById[(int)HandlerId.ArithmeticOps_Adcs] = ArithmeticOps.Adcs;
        managedById[(int)HandlerId.BitOps_Ands] = BitOps.Ands;
        managedById[(int)HandlerId.BitOps_AsrsRegister] = BitOps.AsrsRegister;
        managedById[(int)HandlerId.BitOps_Bics] = BitOps.Bics;
        managedById[(int)HandlerId.ArithmeticOps_Cmn] = ArithmeticOps.Cmn;
        managedById[(int)HandlerId.BitOps_Tst] = BitOps.Tst;
        managedById[(int)HandlerId.ArithmeticOps_Rsbs] = ArithmeticOps.Rsbs;
        managedById[(int)HandlerId.ArithmeticOps_CmpRegister] = ArithmeticOps.CmpRegister;
        managedById[(int)HandlerId.BitOps_Eors] = BitOps.Eors;
        managedById[(int)HandlerId.ArithmeticOps_Muls] = ArithmeticOps.Muls;
        managedById[(int)HandlerId.BitOps_Mvns] = BitOps.Mvns;
        managedById[(int)HandlerId.BitOps_LsrsRegister] = BitOps.LsrsRegister;
        managedById[(int)HandlerId.BitOps_LslsRegister] = BitOps.LslsRegister;
        managedById[(int)HandlerId.BitOps_LslsZero] = BitOps.LslsZero;
        managedById[(int)HandlerId.BitOps_LsrsImm32] = BitOps.LsrsImm32;
        managedById[(int)HandlerId.BitOps_Rev16] = BitOps.Rev16;
        managedById[(int)HandlerId.BitOps_Revsh] = BitOps.Revsh;
        managedById[(int)HandlerId.BitOps_Rev] = BitOps.Rev;
        managedById[(int)HandlerId.ArithmeticOps_Sbcs] = ArithmeticOps.Sbcs;
        managedById[(int)HandlerId.BitOps_Ror] = BitOps.Ror;
        managedById[(int)HandlerId.BitOps_Sxth] = BitOps.Sxth;
        managedById[(int)HandlerId.BitOps_Sxtb] = BitOps.Sxtb;
        managedById[(int)HandlerId.BitOps_Uxth] = BitOps.Uxth;
        managedById[(int)HandlerId.BitOps_Uxtb] = BitOps.Uxtb;
        managedById[(int)HandlerId.ArithmeticOps_AddHighToPc] = ArithmeticOps.AddHighToPc;
        managedById[(int)HandlerId.ArithmeticOps_AddHighToSp] = ArithmeticOps.AddHighToSp;
        managedById[(int)HandlerId.FlowOps_Blx] = FlowOps.Blx;
        managedById[(int)HandlerId.FlowOps_Bx] = FlowOps.Bx;
        managedById[(int)HandlerId.BitOps_MovToPc] = BitOps.MovToPc;
        managedById[(int)HandlerId.BitOps_MovToSp] = BitOps.MovToSp;
        managedById[(int)HandlerId.ArithmeticOps_AddSpImmediate7] = ArithmeticOps.AddSpImmediate7;
        managedById[(int)HandlerId.ArithmeticOps_SubSp] = ArithmeticOps.SubSp;
        managedById[(int)HandlerId.MemoryOps_Pop] = MemoryOps.Pop;
        managedById[(int)HandlerId.MemoryOps_PopPc] = MemoryOps.PopPc;
        managedById[(int)HandlerId.MemoryOps_Push] = MemoryOps.Push;
        managedById[(int)HandlerId.MemoryOps_PushLr] = MemoryOps.PushLr;
        managedById[(int)HandlerId.FlowOps_Cbz] = FlowOps.Cbz;
        managedById[(int)HandlerId.FlowOps_Cbnz] = FlowOps.Cbnz;
        managedById[(int)HandlerId.SystemOps_Bkpt] = SystemOps.Bkpt;
        managedById[(int)HandlerId.SystemOps_Svc] = SystemOps.Svc;
        managedById[(int)HandlerId.ArithmeticOps_AddHighToReg] = ArithmeticOps.AddHighToReg;
        managedById[(int)HandlerId.ArithmeticOps_CmpHighRegister] = ArithmeticOps.CmpHighRegister;
        managedById[(int)HandlerId.BitOps_MovRegister] = BitOps.MovRegister;
        managedById[(int)HandlerId.BitOps_Orrs] = BitOps.Orrs;
        managedById[(int)HandlerId.FlowOps_Beq] = FlowOps.Beq;
        managedById[(int)HandlerId.FlowOps_Bne] = FlowOps.Bne;
        managedById[(int)HandlerId.FlowOps_Bcs] = FlowOps.Bcs;
        managedById[(int)HandlerId.FlowOps_Bcc] = FlowOps.Bcc;
        managedById[(int)HandlerId.FlowOps_Bmi] = FlowOps.Bmi;
        managedById[(int)HandlerId.FlowOps_Bpl] = FlowOps.Bpl;
        managedById[(int)HandlerId.FlowOps_Bvs] = FlowOps.Bvs;
        managedById[(int)HandlerId.FlowOps_Bvc] = FlowOps.Bvc;
        managedById[(int)HandlerId.FlowOps_Bhi] = FlowOps.Bhi;
        managedById[(int)HandlerId.FlowOps_Bls] = FlowOps.Bls;
        managedById[(int)HandlerId.FlowOps_Bge] = FlowOps.Bge;
        managedById[(int)HandlerId.FlowOps_Blt] = FlowOps.Blt;
        managedById[(int)HandlerId.FlowOps_Bgt] = FlowOps.Bgt;
        managedById[(int)HandlerId.FlowOps_Ble] = FlowOps.Ble;
        managedById[(int)HandlerId.ArithmeticOps_AddsRegister] = ArithmeticOps.AddsRegister;
        managedById[(int)HandlerId.ArithmeticOps_AddsImmediate3] = ArithmeticOps.AddsImmediate3;
        managedById[(int)HandlerId.ArithmeticOps_SubsImmediate3] = ArithmeticOps.SubsImmediate3;
        managedById[(int)HandlerId.ArithmeticOps_SubsRegister] = ArithmeticOps.SubsRegister;
        managedById[(int)HandlerId.MemoryOps_LdrRegister] = MemoryOps.LdrRegister;
        managedById[(int)HandlerId.MemoryOps_StrRegister] = MemoryOps.StrRegister;
        managedById[(int)HandlerId.MemoryOps_StrhRegister] = MemoryOps.StrhRegister;
        managedById[(int)HandlerId.MemoryOps_StrbRegister] = MemoryOps.StrbRegister;
        managedById[(int)HandlerId.MemoryOps_Ldrsb] = MemoryOps.Ldrsb;
        managedById[(int)HandlerId.MemoryOps_LdrhRegister] = MemoryOps.LdrhRegister;
        managedById[(int)HandlerId.MemoryOps_LdrbRegister] = MemoryOps.LdrbRegister;
        managedById[(int)HandlerId.MemoryOps_Ldrsh] = MemoryOps.Ldrsh;
        managedById[(int)HandlerId.ArithmeticOps_AddSpImmediate8] = ArithmeticOps.AddSpImmediate8;
        managedById[(int)HandlerId.ArithmeticOps_AddsImmediate8] = ArithmeticOps.AddsImmediate8;
        managedById[(int)HandlerId.ArithmeticOps_SubsImmediate8] = ArithmeticOps.SubsImmediate8;
        managedById[(int)HandlerId.ArithmeticOps_Adr] = ArithmeticOps.Adr;
        managedById[(int)HandlerId.BitOps_AsrsImm5] = BitOps.AsrsImm5;
        managedById[(int)HandlerId.FlowOps_Bl] = FlowOps.Bl;
        managedById[(int)HandlerId.FlowOps_Branch] = FlowOps.Branch;
        managedById[(int)HandlerId.ArithmeticOps_CmpImmediate] = ArithmeticOps.CmpImmediate;
        managedById[(int)HandlerId.BitOps_Movs] = BitOps.Movs;
        managedById[(int)HandlerId.MemoryOps_Ldmia] = MemoryOps.Ldmia;
        managedById[(int)HandlerId.MemoryOps_Stmia] = MemoryOps.Stmia;
        managedById[(int)HandlerId.MemoryOps_LdrLiteral] = MemoryOps.LdrLiteral;
        managedById[(int)HandlerId.MemoryOps_LdrImmediate] = MemoryOps.LdrImmediate;
        managedById[(int)HandlerId.MemoryOps_LdrSpRelative] = MemoryOps.LdrSpRelative;
        managedById[(int)HandlerId.MemoryOps_StrImmediate] = MemoryOps.StrImmediate;
        managedById[(int)HandlerId.MemoryOps_StrSpRelative] = MemoryOps.StrSpRelative;
        managedById[(int)HandlerId.MemoryOps_StrbImmediate] = MemoryOps.StrbImmediate;
        managedById[(int)HandlerId.MemoryOps_StrhImmediate] = MemoryOps.StrhImmediate;
        managedById[(int)HandlerId.MemoryOps_LdrbImmediate] = MemoryOps.LdrbImmediate;
        managedById[(int)HandlerId.MemoryOps_LdrhImmediate] = MemoryOps.LdrhImmediate;
        managedById[(int)HandlerId.BitOps_LslsImm5] = BitOps.LslsImm5;
        managedById[(int)HandlerId.BitOps_LsrsImm5] = BitOps.LsrsImm5;

        for (var op = 0; op < 65536; op++)
        {
            var opcode = (ushort)op;
            HandlerId id = HandlerId.Undefined;
            foreach (var r in Rules)
            {
                if ((opcode & r.Mask) != r.Pattern) continue;
                id = r.Id;
                break;
            }

            IdTable[op]      = (ushort)id;
            ManagedTable[op] = managedById[(int)id];
        }
    }

    /// <summary>Managed equivalent of InstructionDecoder.HandleUndefined: ARMv6-M B1.5.6 raises HardFault.</summary>
    internal static void Undefined(ushort opcode, CortexM0Plus cpu)
    {
        System.Console.Error.WriteLine($"Undefined instruction 0x{opcode:X4} at PC=0x{cpu.Registers.PC:X8}");
        cpu.TriggerHardFault();
    }
}

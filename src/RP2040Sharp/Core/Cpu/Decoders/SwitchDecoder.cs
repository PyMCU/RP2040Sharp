using System.Runtime.CompilerServices;
using RP2040.Core.Cpu.Instructions;

namespace RP2040.Core.Cpu.Decoders;

/// <summary>
/// Dispatches with a <c>switch</c> over a per-opcode handler id (<see cref="ThumbDecodeTables.IdTable"/>),
/// letting the JIT emit a jump table (br_table on WASM) with a DIRECT call in each arm — no indirect
/// call. On runtimes where <c>call_indirect</c> is expensive (WASM) this can win; on native it usually
/// loses to the function-pointer table.
/// </summary>
public readonly struct SwitchDecoder : IInstructionDecoder
{
    private readonly ushort[] _ids;

    public SwitchDecoder(ushort[] ids) => _ids = ids;

    public static SwitchDecoder Create() => new(ThumbDecodeTables.IdTable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispatch16(ushort opcode, CortexM0Plus cpu)
    {
        switch ((HandlerId)_ids[opcode])
        {
            case HandlerId.SystemOps_Mrs: SystemOps.Mrs(opcode, cpu); break;
            case HandlerId.SystemOps_Barrier: SystemOps.Barrier(opcode, cpu); break;
            case HandlerId.SystemOps_Cpsie: SystemOps.Cpsie(opcode, cpu); break;
            case HandlerId.SystemOps_Cpsid: SystemOps.Cpsid(opcode, cpu); break;
            case HandlerId.SystemOps_Nop: SystemOps.Nop(opcode, cpu); break;
            case HandlerId.SystemOps_Sev: SystemOps.Sev(opcode, cpu); break;
            case HandlerId.SystemOps_Wfe: SystemOps.Wfe(opcode, cpu); break;
            case HandlerId.SystemOps_Wfi: SystemOps.Wfi(opcode, cpu); break;
            case HandlerId.SystemOps_Msr: SystemOps.Msr(opcode, cpu); break;
            case HandlerId.BitOps_Clz: BitOps.Clz(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Adcs: ArithmeticOps.Adcs(opcode, cpu); break;
            case HandlerId.BitOps_Ands: BitOps.Ands(opcode, cpu); break;
            case HandlerId.BitOps_AsrsRegister: BitOps.AsrsRegister(opcode, cpu); break;
            case HandlerId.BitOps_Bics: BitOps.Bics(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Cmn: ArithmeticOps.Cmn(opcode, cpu); break;
            case HandlerId.BitOps_Tst: BitOps.Tst(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Rsbs: ArithmeticOps.Rsbs(opcode, cpu); break;
            case HandlerId.ArithmeticOps_CmpRegister: ArithmeticOps.CmpRegister(opcode, cpu); break;
            case HandlerId.BitOps_Eors: BitOps.Eors(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Muls: ArithmeticOps.Muls(opcode, cpu); break;
            case HandlerId.BitOps_Mvns: BitOps.Mvns(opcode, cpu); break;
            case HandlerId.BitOps_LsrsRegister: BitOps.LsrsRegister(opcode, cpu); break;
            case HandlerId.BitOps_LslsRegister: BitOps.LslsRegister(opcode, cpu); break;
            case HandlerId.BitOps_LslsZero: BitOps.LslsZero(opcode, cpu); break;
            case HandlerId.BitOps_LsrsImm32: BitOps.LsrsImm32(opcode, cpu); break;
            case HandlerId.BitOps_Rev16: BitOps.Rev16(opcode, cpu); break;
            case HandlerId.BitOps_Revsh: BitOps.Revsh(opcode, cpu); break;
            case HandlerId.BitOps_Rev: BitOps.Rev(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Sbcs: ArithmeticOps.Sbcs(opcode, cpu); break;
            case HandlerId.BitOps_Ror: BitOps.Ror(opcode, cpu); break;
            case HandlerId.BitOps_Sxth: BitOps.Sxth(opcode, cpu); break;
            case HandlerId.BitOps_Sxtb: BitOps.Sxtb(opcode, cpu); break;
            case HandlerId.BitOps_Uxth: BitOps.Uxth(opcode, cpu); break;
            case HandlerId.BitOps_Uxtb: BitOps.Uxtb(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddHighToPc: ArithmeticOps.AddHighToPc(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddHighToSp: ArithmeticOps.AddHighToSp(opcode, cpu); break;
            case HandlerId.FlowOps_Blx: FlowOps.Blx(opcode, cpu); break;
            case HandlerId.FlowOps_Bx: FlowOps.Bx(opcode, cpu); break;
            case HandlerId.BitOps_MovToPc: BitOps.MovToPc(opcode, cpu); break;
            case HandlerId.BitOps_MovToSp: BitOps.MovToSp(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddSpImmediate7: ArithmeticOps.AddSpImmediate7(opcode, cpu); break;
            case HandlerId.ArithmeticOps_SubSp: ArithmeticOps.SubSp(opcode, cpu); break;
            case HandlerId.MemoryOps_Pop: MemoryOps.Pop(opcode, cpu); break;
            case HandlerId.MemoryOps_PopPc: MemoryOps.PopPc(opcode, cpu); break;
            case HandlerId.MemoryOps_Push: MemoryOps.Push(opcode, cpu); break;
            case HandlerId.MemoryOps_PushLr: MemoryOps.PushLr(opcode, cpu); break;
            case HandlerId.FlowOps_Cbz: FlowOps.Cbz(opcode, cpu); break;
            case HandlerId.FlowOps_Cbnz: FlowOps.Cbnz(opcode, cpu); break;
            case HandlerId.SystemOps_Bkpt: SystemOps.Bkpt(opcode, cpu); break;
            case HandlerId.SystemOps_Svc: SystemOps.Svc(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddHighToReg: ArithmeticOps.AddHighToReg(opcode, cpu); break;
            case HandlerId.ArithmeticOps_CmpHighRegister: ArithmeticOps.CmpHighRegister(opcode, cpu); break;
            case HandlerId.BitOps_MovRegister: BitOps.MovRegister(opcode, cpu); break;
            case HandlerId.BitOps_Orrs: BitOps.Orrs(opcode, cpu); break;
            case HandlerId.FlowOps_Beq: FlowOps.Beq(opcode, cpu); break;
            case HandlerId.FlowOps_Bne: FlowOps.Bne(opcode, cpu); break;
            case HandlerId.FlowOps_Bcs: FlowOps.Bcs(opcode, cpu); break;
            case HandlerId.FlowOps_Bcc: FlowOps.Bcc(opcode, cpu); break;
            case HandlerId.FlowOps_Bmi: FlowOps.Bmi(opcode, cpu); break;
            case HandlerId.FlowOps_Bpl: FlowOps.Bpl(opcode, cpu); break;
            case HandlerId.FlowOps_Bvs: FlowOps.Bvs(opcode, cpu); break;
            case HandlerId.FlowOps_Bvc: FlowOps.Bvc(opcode, cpu); break;
            case HandlerId.FlowOps_Bhi: FlowOps.Bhi(opcode, cpu); break;
            case HandlerId.FlowOps_Bls: FlowOps.Bls(opcode, cpu); break;
            case HandlerId.FlowOps_Bge: FlowOps.Bge(opcode, cpu); break;
            case HandlerId.FlowOps_Blt: FlowOps.Blt(opcode, cpu); break;
            case HandlerId.FlowOps_Bgt: FlowOps.Bgt(opcode, cpu); break;
            case HandlerId.FlowOps_Ble: FlowOps.Ble(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddsRegister: ArithmeticOps.AddsRegister(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddsImmediate3: ArithmeticOps.AddsImmediate3(opcode, cpu); break;
            case HandlerId.ArithmeticOps_SubsImmediate3: ArithmeticOps.SubsImmediate3(opcode, cpu); break;
            case HandlerId.ArithmeticOps_SubsRegister: ArithmeticOps.SubsRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrRegister: MemoryOps.LdrRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_StrRegister: MemoryOps.StrRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_StrhRegister: MemoryOps.StrhRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_StrbRegister: MemoryOps.StrbRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_Ldrsb: MemoryOps.Ldrsb(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrhRegister: MemoryOps.LdrhRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrbRegister: MemoryOps.LdrbRegister(opcode, cpu); break;
            case HandlerId.MemoryOps_Ldrsh: MemoryOps.Ldrsh(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddSpImmediate8: ArithmeticOps.AddSpImmediate8(opcode, cpu); break;
            case HandlerId.ArithmeticOps_AddsImmediate8: ArithmeticOps.AddsImmediate8(opcode, cpu); break;
            case HandlerId.ArithmeticOps_SubsImmediate8: ArithmeticOps.SubsImmediate8(opcode, cpu); break;
            case HandlerId.ArithmeticOps_Adr: ArithmeticOps.Adr(opcode, cpu); break;
            case HandlerId.BitOps_AsrsImm5: BitOps.AsrsImm5(opcode, cpu); break;
            case HandlerId.FlowOps_Bl: FlowOps.Bl(opcode, cpu); break;
            case HandlerId.FlowOps_Branch: FlowOps.Branch(opcode, cpu); break;
            case HandlerId.ArithmeticOps_CmpImmediate: ArithmeticOps.CmpImmediate(opcode, cpu); break;
            case HandlerId.BitOps_Movs: BitOps.Movs(opcode, cpu); break;
            case HandlerId.MemoryOps_Ldmia: MemoryOps.Ldmia(opcode, cpu); break;
            case HandlerId.MemoryOps_Stmia: MemoryOps.Stmia(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrLiteral: MemoryOps.LdrLiteral(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrImmediate: MemoryOps.LdrImmediate(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrSpRelative: MemoryOps.LdrSpRelative(opcode, cpu); break;
            case HandlerId.MemoryOps_StrImmediate: MemoryOps.StrImmediate(opcode, cpu); break;
            case HandlerId.MemoryOps_StrSpRelative: MemoryOps.StrSpRelative(opcode, cpu); break;
            case HandlerId.MemoryOps_StrbImmediate: MemoryOps.StrbImmediate(opcode, cpu); break;
            case HandlerId.MemoryOps_StrhImmediate: MemoryOps.StrhImmediate(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrbImmediate: MemoryOps.LdrbImmediate(opcode, cpu); break;
            case HandlerId.MemoryOps_LdrhImmediate: MemoryOps.LdrhImmediate(opcode, cpu); break;
            case HandlerId.BitOps_LslsImm5: BitOps.LslsImm5(opcode, cpu); break;
            case HandlerId.BitOps_LsrsImm5: BitOps.LsrsImm5(opcode, cpu); break;
            default: ThumbDecodeTables.Undefined(opcode, cpu); break;
        }
    }
}

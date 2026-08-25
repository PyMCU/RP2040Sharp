using System.Runtime.CompilerServices;

namespace RP2040.Core.Cpu.Instructions;

public static class SystemOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Barrier(ushort opcodeH1, CortexM0Plus cpu)
    {
        cpu.Registers.PC += 2;
        // rp2040js DMB/DSB/ISB: base 1 (loop) + 2 (32-bit instruction).
        cpu.Cycles += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Nop(ushort opcodeH1, CortexM0Plus cpu)
    {
        // Do nothing
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mrs(ushort opcodeH1, CortexM0Plus cpu)
    {
        ref var pc = ref cpu.Registers.PC;
        var opcodeH2 = cpu.Bus.ReadHalfWord(pc);
        pc += 2;

        var sysm = opcodeH2 & 0xFF;
        var rd = (opcodeH2 >> 8) & 0xF;

        uint result = 0;

        if ((sysm & 0xF8) == 0)
        {
            var fullPsr = cpu.Registers.GetxPsr();

            uint mask = 0;
            if ((sysm & 1) != 0)
                mask |= 0x1FF;
            if ((sysm & 4) == 0)
                mask |= 0xF0000000;

            result = fullPsr & mask;
        }
        else
        {
            var isPrivileged = (cpu.Registers.IPSR != 0) || ((cpu.Registers.CONTROL & 1) == 0);
            switch (sysm)
            {
                case 8: // MSP
                    var usePsp = (cpu.Registers.IPSR == 0) && ((cpu.Registers.CONTROL & 2) != 0);
                    result = usePsp ? cpu.Registers.MSP_Storage : cpu.Registers.SP;
                    break;

                case 9: // PSP
                    var usePsp2 = (cpu.Registers.IPSR == 0) && ((cpu.Registers.CONTROL & 2) != 0);
                    result = usePsp2 ? cpu.Registers.SP : cpu.Registers.PSP_Storage;
                    break;

                case 16: // PRIMASK
                    result = cpu.Registers.PRIMASK & 1;
                    break;

                case 20: // CONTROL
                    result = cpu.Registers.CONTROL & 3;
                    isPrivileged = true;
                    break;

                default:
                    result = 0;
                    break;
            }
            if (!isPrivileged)
                result = 0;
        }

        cpu.Registers[rd] = result;
        // rp2040js MRS: base 1 (loop) + 2 (32-bit instruction).
        cpu.Cycles += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Msr(ushort opcodeH1, CortexM0Plus cpu)
    {
        ref var pc = ref cpu.Registers.PC;
        var opcodeH2 = cpu.Bus.ReadHalfWord(pc);
        pc += 2;

        var rn = opcodeH1 & 0xF;
        var sysm = opcodeH2 & 0xFF;
        var value = cpu.Registers[rn];

        if ((sysm & 0xF8) == 0)
        {
            if ((sysm & 4) == 0)
            {
                cpu.Registers.N = (int)value < 0; // Bit 31
                cpu.Registers.Z = (value & 0x40000000) != 0;
                cpu.Registers.C = (value & 0x20000000) != 0;
                cpu.Registers.V = (value & 0x10000000) != 0;
            }
        }
        else
        {
            var isPrivileged = (cpu.Registers.IPSR != 0) || ((cpu.Registers.CONTROL & 1) == 0);

            if (isPrivileged)
            {
                switch (sysm)
                {
                    case 8: // MSP
                        var alignedMsp = value & 0xFFFFFFFC;

                        var isMspActive =
                            (cpu.Registers.IPSR != 0) || ((cpu.Registers.CONTROL & 2) == 0);

                        if (isMspActive)
                            cpu.Registers.SP = alignedMsp;
                        else
                            cpu.Registers.MSP_Storage = alignedMsp;
                        break;

                    case 9: // PSP
                        var alignedPsp = value & 0xFFFFFFFC;

                        var isPspActive =
                            (cpu.Registers.IPSR == 0) && ((cpu.Registers.CONTROL & 2) != 0);

                        if (isPspActive)
                            cpu.Registers.SP = alignedPsp;
                        else
                            cpu.Registers.PSP_Storage = alignedPsp;
                        break;

                    case 16: // PRIMASK
                        cpu.Registers.PRIMASK = value & 1;
                        cpu.Registers.InterruptsUpdated = true;
                        break;

                    case 20: // CONTROL
                        var oldControl = cpu.Registers.CONTROL;
                        var newNpriv = value & 1;
                        var newSpsel = (cpu.Registers.IPSR == 0) ? (value & 2) : (oldControl & 2);

                        var newControl = newNpriv | newSpsel;

                        if (((oldControl ^ newControl) & 2) != 0)
                        {
                            cpu.Registers.CONTROL = newControl;
                            cpu.UpdateStackPointerSource();
                        }
                        else
                        {
                            cpu.Registers.CONTROL = newControl;
                        }
                        break;
                }
            }
        }
        // rp2040js MSR: base 1 (loop) + 2 (32-bit instruction).
        cpu.Cycles += 2;
    }

    // ================================================================
    // CPS (Change Processor State) — exact opcodes, Group 1 (mask 0xFFFF)
    // CPSIE i = 0xB662  → PRIMASK = 0 (interrupts enabled)
    // CPSID i = 0xB672  → PRIMASK = 1 (interrupts disabled)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cpsie(ushort opcode, CortexM0Plus cpu)
    {
        cpu.Registers.PRIMASK = 0;
        cpu.Registers.InterruptsUpdated = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cpsid(ushort opcode, CortexM0Plus cpu)
    {
        cpu.Registers.PRIMASK = 1;
    }

    // ================================================================
    // Hint instructions — exact opcodes, Group 1 (mask 0xFFFF)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Wfi(ushort opcode, CortexM0Plus cpu)
    {
        // rp2040js WFI: base 1 (loop) + 1 for the instruction itself, then it starts waiting.
        cpu.Cycles++;
        cpu.Registers.Waiting = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Wfe(ushort opcode, CortexM0Plus cpu)
    {
        // rp2040js WFE: base 1 (loop) + 1 for the instruction itself.
        cpu.Cycles++;
        if (!cpu.Registers.EventRegistered)
            cpu.Registers.Waiting = true;
        else
            cpu.Registers.EventRegistered = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sev(ushort opcode, CortexM0Plus cpu)
    {
        // SEV is cluster-wide: it sets the event register of BOTH cores, not just the issuer.
        cpu.SignalEvent();
    }

    // ================================================================
    // BKPT — mask=0xFF00, pattern=0xBE00
    // ARMv6-M §C1.7.2: if a debug monitor is configured (OnBreakpoint handler is set),
    // invoke it and continue.  Otherwise, the processor raises a HardFault, as if no
    // debug monitor is present — matching real hardware behaviour where a BKPT without
    // a connected debugger faults the core.
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bkpt(ushort opcode, CortexM0Plus cpu)
    {
        var imm8 = (byte)(opcode & 0xFF);
        if (cpu.OnBreakpoint != null)
        {
            cpu.OnBreakpoint.Invoke(imm8);
        }
        else
        {
            // No debugger attached — escalate to HardFault per ARMv6-M §C1.7.2.
            cpu.TriggerHardFault();
        }
    }

    // ================================================================
    // SVC (Supervisor Call) — mask=0xFF00, pattern=0xDF00
    // Triggers exception entry for EXC_SVCALL (11)
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Svc(ushort opcode, CortexM0Plus cpu)
    {
        cpu.Registers.PendingSVCall = true;
        cpu.Registers.InterruptsUpdated = true;
    }
}

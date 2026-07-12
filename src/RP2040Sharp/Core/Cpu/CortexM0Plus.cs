using System.Runtime.CompilerServices;
using RP2040.Core.Memory;

[module: SkipLocalsInit]

namespace RP2040.Core.Cpu;

public sealed unsafe class CortexM0Plus
{
    public readonly BusInterconnect Bus;
    public Registers Registers;
    public long Cycles;

    /// <summary>0 = Core0, 1 = Core1. Used by SIO to return the correct CPUID and route FIFOs.</summary>
    public int CoreId { get; set; }

    /// <summary>
    /// True when the CPU has entered the ARMv6-M lockup state (§B1.5.13).
    /// Lockup occurs when a HardFault is triggered while already executing in the HardFault handler
    /// (or NMI handler), because the hardware cannot escalate further.  Once locked up, no more
    /// instructions execute and the CPU effectively halts.
    /// </summary>
    public bool IsLockedUp { get; private set; }

    private readonly InstructionDecoder _decoder;

    private byte* _fetchPtr;
    private uint _fetchMask;
    private uint _currentRegionId;

    private const uint EXC_RETURN_HANDLER = 0xFFFFFFF1; // Return to Handler mode, using MSP
    private const uint EXC_RETURN_THREAD_MSP = 0xFFFFFFF9; // Return to Thread mode, using MSP
    private const uint EXC_RETURN_THREAD_PSP = 0xFFFFFFFD; // Return to Thread mode, using PSP

    private const uint EXC_NMI = 2;
    private const uint EXC_HARDFAULT = 3;
    private const uint EXC_SVCALL = 11;
    private const uint EXC_PENDSV = 14;
    private const uint EXC_SYSTICK = 15;

    /// <summary>Called when a BKPT instruction is executed. Parameter is the imm8 value.</summary>
    public Action<byte>? OnBreakpoint;

    /// <summary>
    /// Called when the CPU enters the ARMv6-M lockup state (§B1.5.13).
    /// Parameters are the faulting PC and SP at the time of lockup.
    /// When <c>null</c>, the default handler writes a diagnostic message to
    /// <see cref="System.Console.Error"/>.
    /// </summary>
    public Action<uint, uint>? OnLockup;

    /// <summary>
    /// Called when the CPU takes an exception (ISR / fault / SysTick / PendSV / SVCall).
    /// The parameter is the exception number being entered (same value written to IPSR).
    /// Fires once per exception entry — never on the per-instruction hot path — and is
    /// intended for profiling/tracing the boundary between thread code and handler code.
    /// </summary>
    public Action<uint>? OnExceptionEntry;

    /// <summary>
    /// Called when the CPU returns from an exception (EXC_RETURN consumed). The parameter
    /// is the EXC_RETURN value that drove the return. By the time this fires the registers,
    /// SP and PC have already been restored to the resumed context, so a profiler can read
    /// the return PC / stack to identify the task being resumed.
    /// Fires once per exception return — never on the per-instruction hot path.
    /// </summary>
    public Action<uint>? OnExceptionReturn;

    /// <summary>
    /// Native hooks: when the PC equals a registered address (Thumb bit stripped), the
    /// corresponding delegate is called instead of fetching/executing an instruction.
    /// The delegate is responsible for updating registers as needed.  After the delegate
    /// returns the CPU automatically performs <c>PC = LR &amp; ~1</c> (same as BX LR).
    /// </summary>
    private Dictionary<uint, Action<CortexM0Plus>>? _nativeHooks;
    private uint _nativeHookMax;

    public void RegisterNativeHook(uint address, Action<CortexM0Plus> hook)
    {
        _nativeHooks ??= new Dictionary<uint, Action<CortexM0Plus>>();
        address &= ~1u;
        _nativeHooks[address] = hook;
        if (address > _nativeHookMax) _nativeHookMax = address;
    }

    public CortexM0Plus(BusInterconnect bus)
    {
        Bus = bus;
        _decoder = InstructionDecoder.Instance;
        Reset();
    }

    public void Reset()
    {
        IsLockedUp = false;
        Registers.SP = Bus.ReadWord(0x00000000);
        Registers.PC = Bus.ReadWord(0x00000004) & 0xFFFFFFFE;  // strip Thumb bit, same as ExceptionEntry

        UpdateFetchCache(Registers.PC);

        Registers.N = false;
        Registers.Z = false;
        Registers.C = false;
        Registers.V = false;

        // All IRQs reset to priority level 0 (each InterruptPrioritiesN is the bitmask of
        // IRQs currently at that level; NVIC IPR writes move bits between buckets).
        Registers.InterruptPriorities0 = 0x03FFFFFF;
        Registers.InterruptPriorities1 = 0;
        Registers.InterruptPriorities2 = 0;
        Registers.InterruptPriorities3 = 0;
        Registers.SHPR2 = 0;
        Registers.SHPR3 = 0;

        Cycles = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateFetchCache(uint pc)
    {
        _currentRegionId = pc >> 28;

        switch (_currentRegionId)
        {
            case BusInterconnect.REGION_FLASH:
                _fetchPtr = Bus.PtrFlash;
                _fetchMask = Bus.MaskFlash & ~1u;
                break;
            case BusInterconnect.REGION_SRAM:
                _fetchPtr = Bus.PtrSram;
                _fetchMask = BusInterconnect.MASK_SRAM & ~1u;
                break;
            case BusInterconnect.REGION_BOOTROM:
                _fetchPtr = Bus.PtrBootRom;
                _fetchMask = BusInterconnect.MASK_BOOTROM & ~1u;
                break;
            default:
                _fetchPtr = null;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run(int instructions)
    {
        var decoder = _decoder;

        var fetchPtr = _fetchPtr;
        var fetchMask = _fetchMask;
        var regionId = _currentRegionId;

        while (instructions-- > 0)
        {
            // ARMv6-M §B1.5.13 Lockup: CPU halts when HardFault fires in HardFault/NMI handler.
            if (IsLockedUp) return;

            // Interrupt check — predictable branch (nearly always not taken)
            if (Registers.InterruptsUpdated)
            {
                Registers.InterruptsUpdated = false;
                if (CheckForInterrupts())
                {
                    UpdateFetchCache(Registers.PC);
                    fetchPtr = _fetchPtr;
                    fetchMask = _fetchMask;
                    regionId = _currentRegionId;
                }
            }

            // WFI/WFE sleep: bail out of the current batch, crediting the unused
            // instruction budget as elapsed cycles so the outer Machine.Run can
            // advance time-aware peripherals (Timer, Watchdog, ...) and let an
            // alarm IRQ wake us on the next batch.  Without this, a CPU that
            // sleeps on the very first instruction of a batch produces delta=0
            // and the simulation deadlocks: the timer never ticks → the alarm
            // never fires → WFE never returns.
            if (Registers.Waiting)
            {
                // SEV from another core (or FIFO-write) sets EventRegistered; wake WFE.
                if (Registers.EventRegistered)
                {
                    Registers.Waiting = false;
                    Registers.EventRegistered = false;
                }
                else
                {
                    Cycles += (uint)(instructions + 1);
                    return;
                }
            }

            var pc = Registers.PC;

            // FAST GUARD
            if ((pc >> 28) != regionId)
            {
                // FALLBACK
                UpdateFetchCache(pc);

                fetchPtr = _fetchPtr;
                fetchMask = _fetchMask;
                regionId = _currentRegionId;

                if (fetchPtr == null)
                {
                    // PC landed in an un-executable region — raise HardFault per ARMv6-M spec
                    ExceptionEntry(EXC_HARDFAULT);
                    if (IsLockedUp) return;
                    UpdateFetchCache(Registers.PC);
                    fetchPtr  = _fetchPtr;
                    fetchMask = _fetchMask;
                    regionId  = _currentRegionId;
                    continue;
                }
            }

            // ULTRA-FAST FETCH
            // Check for native hooks — only possible in BootROM (pc < 0x23C5 after LoadFlash).
            if (_nativeHooks != null && pc <= _nativeHookMax && _nativeHooks.TryGetValue(pc, out var nativeHook))
            {
                var pcBeforeHook = Registers.PC; // equals pc (not yet advanced; advance is only in normal dispatch)
                nativeHook(this);
                // If the hook itself changed PC (e.g., to redirect execution), honor that.
                // Otherwise do the standard BX LR return.
                if (Registers.PC == pcBeforeHook)
                {
                    var hookLr = Registers.LR;
                    if (hookLr >= 0xFFFFFFF0)
                        ExceptionReturn(hookLr);
                    else
                        Registers.PC = hookLr & ~1u;
                }
                UpdateFetchCache(Registers.PC);
                fetchPtr  = _fetchPtr;
                fetchMask = _fetchMask;
                regionId  = _currentRegionId;
                Cycles++;
                continue;
            }

            var opcode = Unsafe.ReadUnaligned<ushort>(fetchPtr + (pc & fetchMask));

            // PRE-UPDATE PC (Speculative)
            Registers.PC = pc + 2;

            Cycles++;

            // DISPATCH
            decoder.Dispatch(opcode, this);
        }

        _currentRegionId = regionId;
        _fetchPtr = fetchPtr;
        _fetchMask = fetchMask;
    }

    /// <summary>
    /// Profiling-only sibling of <see cref="Run"/>. Identical execution semantics, but
    /// invokes <paramref name="observer"/> once per instruction (before dispatch). This is a
    /// deliberately separate method so the hot <see cref="Run"/> path is byte-for-byte
    /// unchanged and pays nothing for profiling — mirroring AVR8Sharp's
    /// Execute()/ExecuteProfiling() split. Do not use for throughput-sensitive simulation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RunProfiled(int instructions, IProfilingObserver observer)
    {
        var decoder = _decoder;

        var fetchPtr = _fetchPtr;
        var fetchMask = _fetchMask;
        var regionId = _currentRegionId;

        while (instructions-- > 0)
        {
            if (IsLockedUp) return;

            if (Registers.InterruptsUpdated)
            {
                Registers.InterruptsUpdated = false;
                if (CheckForInterrupts())
                {
                    UpdateFetchCache(Registers.PC);
                    fetchPtr = _fetchPtr;
                    fetchMask = _fetchMask;
                    regionId = _currentRegionId;
                }
            }

            if (Registers.Waiting)
            {
                if (Registers.EventRegistered)
                {
                    Registers.Waiting = false;
                    Registers.EventRegistered = false;
                }
                else
                {
                    Cycles += (uint)(instructions + 1);
                    return;
                }
            }

            var pc = Registers.PC;

            if ((pc >> 28) != regionId)
            {
                UpdateFetchCache(pc);

                fetchPtr = _fetchPtr;
                fetchMask = _fetchMask;
                regionId = _currentRegionId;

                if (fetchPtr == null)
                {
                    ExceptionEntry(EXC_HARDFAULT);
                    if (IsLockedUp) return;
                    UpdateFetchCache(Registers.PC);
                    fetchPtr  = _fetchPtr;
                    fetchMask = _fetchMask;
                    regionId  = _currentRegionId;
                    continue;
                }
            }

            if (_nativeHooks != null && pc <= _nativeHookMax && _nativeHooks.TryGetValue(pc, out var nativeHook))
            {
                observer.OnInstruction(pc, Unsafe.ReadUnaligned<ushort>(fetchPtr + (pc & fetchMask)), Cycles);

                var pcBeforeHook = Registers.PC;
                nativeHook(this);
                if (Registers.PC == pcBeforeHook)
                {
                    var hookLr = Registers.LR;
                    if (hookLr >= 0xFFFFFFF0)
                        ExceptionReturn(hookLr);
                    else
                        Registers.PC = hookLr & ~1u;
                }
                UpdateFetchCache(Registers.PC);
                fetchPtr  = _fetchPtr;
                fetchMask = _fetchMask;
                regionId  = _currentRegionId;
                Cycles++;
                continue;
            }

            var opcode = Unsafe.ReadUnaligned<ushort>(fetchPtr + (pc & fetchMask));

            observer.OnInstruction(pc, opcode, Cycles);

            Registers.PC = pc + 2;

            Cycles++;

            decoder.Dispatch(opcode, this);
        }

        _currentRegionId = regionId;
        _fetchPtr = fetchPtr;
        _fetchMask = fetchMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Step()
    {
        var pc = Registers.PC;
        var opcode = Bus.ReadHalfWord(pc);
        Registers.PC = pc + 2;
        Cycles++;
        _decoder.Dispatch(opcode, this);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // NoInlining (it is not used commonly)
    public void UpdateStackPointerSource()
    {
        if (Registers.IPSR != 0)
            return;

        var switchToPsp = (Registers.CONTROL & 2) != 0;

        if (switchToPsp)
        {
            Registers.MSP_Storage = Registers.SP;
            Registers.SP = Registers.PSP_Storage;
        }
        else
        {
            Registers.PSP_Storage = Registers.SP;
            Registers.SP = Registers.MSP_Storage;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ExceptionEntry(uint exceptionNumber)
    {
        if (exceptionNumber == EXC_HARDFAULT)
        {
            // ARMv6-M §B1.5.13: a HardFault that occurs while executing the HardFault handler
            // (or NMI handler) cannot escalate further — the processor enters Lockup.
            // In lockup the CPU stops fetching instructions and drives the bus with a defined
            // repeating pattern.  We model this by setting IsLockedUp and returning early so
            // the Run() loop stops executing instructions.
            if (Registers.IPSR == EXC_HARDFAULT || Registers.IPSR == EXC_NMI)
            {
                IsLockedUp = true;
                if (OnLockup is not null)
                    OnLockup(Registers.PC, Registers.SP);
                else
                    System.Console.Error.WriteLine(
                        $"CPU LOCKUP: HardFault in handler mode (IPSR={Registers.IPSR}) " +
                        $"callerPC=0x{Registers.PC:X8} SP=0x{Registers.SP:X8}");
                return;
            }
            System.Console.Error.WriteLine($"HardFault: callerPC=0x{Registers.PC:X8} LR=0x{Registers.LR:X8} SP=0x{Registers.SP:X8}");
        }
        var framePtr = Registers.SP;

        var needsAlign = (framePtr & 4) != 0;
        var framePtrAlign = needsAlign ? 1u : 0u;

        var stackAdjust = 0x20u + (needsAlign ? 4u : 0u);
        var finalSp = framePtr - stackAdjust;

        var frameBase = finalSp;

        Bus.WriteWord(frameBase + 0x00, Registers.R0);
        Bus.WriteWord(frameBase + 0x04, Registers.R1);
        Bus.WriteWord(frameBase + 0x08, Registers.R2);
        Bus.WriteWord(frameBase + 0x0C, Registers.R3);
        Bus.WriteWord(frameBase + 0x10, Registers.R12);
        Bus.WriteWord(frameBase + 0x14, Registers.LR);
        Bus.WriteWord(frameBase + 0x18, Registers.PC & 0xFFFFFFFE); // Return Address

        var xpsr = Registers.GetxPsr() | (framePtrAlign << 9);
        Bus.WriteWord(frameBase + 0x1C, xpsr);

        if (Registers.IPSR > 0)
        {
            Registers.LR = EXC_RETURN_HANDLER;
        }
        else
        {
            Registers.LR =
                (Registers.CONTROL & 2) != 0 ? EXC_RETURN_THREAD_PSP : EXC_RETURN_THREAD_MSP;
        }

        if ((Registers.CONTROL & 2) != 0)
        {
            Registers.PSP_Storage = finalSp;
            Registers.SP = Registers.MSP_Storage;
        }
        else
        {
            Registers.SP = finalSp;
        }

        Registers.IPSR = exceptionNumber;
        Registers.CONTROL &= ~2u;

        uint vtor = Registers.VTOR;
        var vectorAddress = vtor + (exceptionNumber * 4);

        var targetPc = Bus.ReadWord(vectorAddress);
        Registers.PC = targetPc & 0xFFFFFFFE;

        Cycles += 12; // Exception Entry cost (aprox 12-15 cycles)

        OnExceptionEntry?.Invoke(exceptionNumber);
    }

    // ================================================================
    // Interrupt / Exception management (called by PPB peripheral)
    // ================================================================

    public void SetInterrupt(int irq, bool pending)
    {
        if (irq is < 0 or > 25) return;
        var bit = 1u << irq;
        if (pending)
            Registers.PendingInterrupts |= bit;
        else
            Registers.PendingInterrupts &= ~bit;
        Registers.InterruptsUpdated = true;
    }

    public void TriggerNmi() { Registers.PendingNMI = true; Registers.InterruptsUpdated = true; }
    public void TriggerSysTick() { Registers.PendingSystick = true; Registers.InterruptsUpdated = true; }
    public void TriggerPendSv() { Registers.PendingPendSV = true; Registers.InterruptsUpdated = true; }
    public void TriggerHardFault() => ExceptionEntry(EXC_HARDFAULT);

    private int IrqPriority(int irq)
    {
        var bit = 1u << irq;
        if ((Registers.InterruptPriorities0 & bit) != 0) return 0;
        if ((Registers.InterruptPriorities1 & bit) != 0) return 1;
        if ((Registers.InterruptPriorities2 & bit) != 0) return 2;
        return 3;
    }

    // ARMv6-M §B1.5.4: group priority of an exception. 256 = Thread mode base level
    // (lower than any configurable priority), so anything pending can preempt it.
    private int ExceptionPriority(uint exceptionNumber) => exceptionNumber switch
    {
        0 => 256,
        EXC_NMI => -2,
        EXC_HARDFAULT => -1,
        EXC_SVCALL => (int)(Registers.SHPR2 >> 30),
        EXC_PENDSV => (int)((Registers.SHPR3 >> 22) & 3),
        EXC_SYSTICK => (int)(Registers.SHPR3 >> 30),
        >= 16 => IrqPriority((int)exceptionNumber - 16),
        _ => -3,
    };

    /// <summary>Returns true if an interrupt was taken (PC changed).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CheckForInterrupts()
    {
        // Per ARMv6-M spec, WFI wakes when any pending+enabled exception exists,
        // even if PRIMASK=1 prevents it from being taken.  The common firmware
        // pattern is: disable_irq → check_work → WFI → enable_irq.  Without this
        // wake-only behaviour the CPU would sleep forever with PRIMASK=1.
        if (Registers.Waiting)
        {
            var wakeIrq = (Registers.PendingInterrupts & Registers.EnabledInterrupts) != 0
                          || Registers.PendingNMI
                          || Registers.PendingSystick
                          || Registers.PendingPendSV;
            if (wakeIrq)
                Registers.Waiting = false;
        }

        // ARMv6-M §B1.5.4: an exception only preempts when its group priority is strictly
        // higher (numerically lower) than the current execution priority.  Without this
        // gate a level/held IRQ (e.g. IO_IRQ_BANK0 before the handler acks INTR) preempts
        // its own handler after every instruction, the stack unwinds into RAM and the core
        // ends up in a HardFault lockup loop.  PRIMASK boosts execution priority to 0.
        var execPriority = ExceptionPriority(Registers.IPSR);
        if (Registers.PRIMASK != 0 && execPriority > 0)
            execPriority = 0;

        // Pick the highest-priority pending exception; ties resolve to the lowest
        // exception number (candidates are considered in exception-number order).
        uint bestExc = 0;
        var bestPriority = execPriority;

        if (Registers.PendingNMI && -2 < bestPriority)
        {
            bestExc = EXC_NMI;
            bestPriority = -2;
        }
        if (Registers.PendingSVCall)
        {
            var p = ExceptionPriority(EXC_SVCALL);
            if (p < bestPriority) { bestExc = EXC_SVCALL; bestPriority = p; }
        }
        if (Registers.PendingPendSV)
        {
            var p = ExceptionPriority(EXC_PENDSV);
            if (p < bestPriority) { bestExc = EXC_PENDSV; bestPriority = p; }
        }
        if (Registers.PendingSystick)
        {
            var p = ExceptionPriority(EXC_SYSTICK);
            if (p < bestPriority) { bestExc = EXC_SYSTICK; bestPriority = p; }
        }
        var pending = Registers.PendingInterrupts & Registers.EnabledInterrupts;
        while (pending != 0)
        {
            var irq = System.Numerics.BitOperations.TrailingZeroCount(pending);
            pending &= pending - 1;
            var p = IrqPriority(irq);
            if (p < bestPriority)
            {
                bestExc = (uint)(irq + 16); // IRQ0 = Exception 16
                bestPriority = p;
            }
        }

        if (bestExc == 0)
            return false;

        switch (bestExc)
        {
            case EXC_NMI: Registers.PendingNMI = false; break;
            case EXC_SVCALL: Registers.PendingSVCall = false; break;
            case EXC_PENDSV: Registers.PendingPendSV = false; break;
            case EXC_SYSTICK: Registers.PendingSystick = false; break;
            default: Registers.PendingInterrupts &= ~(1u << ((int)bestExc - 16)); break;
        }
        Registers.Waiting = false;
        ExceptionEntry(bestExc);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ExceptionReturn(uint excReturn)
    {
        var returnToThread = (excReturn & 8) != 0;
        var usePsp = (excReturn & 4) != 0;

        if (!returnToThread && usePsp)
        {
            usePsp = false;
        }

        if (returnToThread)
        {
            // ARMv6-M §B1.5.8: when returning to Thread mode, IPSR becomes 0.
            Registers.IPSR = 0;

            if (usePsp)
            {
                Registers.MSP_Storage = Registers.SP;
                Registers.SP = Registers.PSP_Storage;
                Registers.CONTROL |= 2;
            }
            else
            {
                Registers.CONTROL &= ~2u;
            }
        }

        var framePtr = Registers.SP;

        Registers.R0 = Bus.ReadWord(framePtr + 0x00);
        Registers.R1 = Bus.ReadWord(framePtr + 0x04);
        Registers.R2 = Bus.ReadWord(framePtr + 0x08);
        Registers.R3 = Bus.ReadWord(framePtr + 0x0C);
        Registers.R12 = Bus.ReadWord(framePtr + 0x10);
        Registers.LR = Bus.ReadWord(framePtr + 0x14);
        var retPC = Bus.ReadWord(framePtr + 0x18);
        var xpsr = Bus.ReadWord(framePtr + 0x1C);

        Registers.N = (xpsr & 0x80000000) != 0;
        Registers.Z = (xpsr & 0x40000000) != 0;
        Registers.C = (xpsr & 0x20000000) != 0;
        Registers.V = (xpsr & 0x10000000) != 0;

        // ARMv6-M §B1.5.8: when returning to Handler mode (nested interrupt),
        // IPSR must be restored from the stacked xPSR (bits [5:0] = exception number).
        if (!returnToThread)
            Registers.IPSR = xpsr & 0x3Fu;

        var alignAdjust = (xpsr & (1 << 9)) != 0;
        var stackFree = 0x20u + (alignAdjust ? 4u : 0u);

        Registers.SP += stackFree;
        Registers.PC = retPC & 0xFFFFFFFE;

        Cycles += 10;
        // After returning from an ISR, re-check interrupts so that a still-pending
        // higher-priority IRQ (e.g. USB after SysTick) fires immediately, AND signal
        // that an event was registered so the next WFE consumes it instead of sleeping.
        // Without `EventRegistered = true`, pico-sdk WFE-loops that expect to be woken
        // by the very IRQ we just serviced will deadlock — the alarm fires once, the
        // handler runs, but the WFE that follows sleeps forever waiting for an event
        // that already happened. rp2040js cortex-m0-core.ts:339-341 sets both flags.
        Registers.InterruptsUpdated = true;
        Registers.EventRegistered = true;

        OnExceptionReturn?.Invoke(excReturn);
    }
}

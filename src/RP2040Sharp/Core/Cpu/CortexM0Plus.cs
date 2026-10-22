// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
using System.Runtime.CompilerServices;
using RP2040.Core.Memory;

[module: SkipLocalsInit]

namespace RP2040.Core.Cpu;

/// <summary>Selects which interchangeable 16-bit decoder <see cref="CortexM0Plus.Run(int)"/> uses.</summary>
public enum DecoderType
{
    /// <summary>Native function-pointer lookup table (default).</summary>
    NativeLut,
    /// <summary>Managed-delegate lookup table (portable; no unsafe).</summary>
    Lut,
    /// <summary>Switch / br_table over a per-opcode handler id.</summary>
    Switch,
}

public sealed unsafe class CortexM0Plus
{
    public readonly BusInterconnect Bus;
    public Registers Registers;
    public long Cycles;

    // ── Cycle-accurate timing model (always on) ──────────────────────────────
    // The core reproduces the EXACT per-instruction cycle model of rp2040js (cortex-m0-core.ts
    // executeInstruction): each instruction costs a base cycle (the run loop's single Cycles++)
    // plus the per-type penalties the real M0+ pays — taken branches, BL/BLX/BX, LDM/STM/PUSH/POP
    // by register, MRS/MSR/barriers, WFI/WFE — added straight-line in the handler the decode table
    // already dispatched to (no reclassification, no branch), and the memory wait-states of
    // cyclesIO() by region. There is no opt-in flag: the accounting is part of the instruction's
    // work, exactly as rp2040js's deltaCycles is. Verified cycle-for-cycle against rp2040js.

    /// <summary>
    /// Memory wait-states for a data access, reproducing rp2040js <c>cyclesIO(addr, write)</c>
    /// (cortex-m0-core.ts): SIO region (0xD) is single-cycle (0 wait), the APB region (0x4) adds
    /// 3 cycles on a read and 4 on a write, and every other region (SRAM/flash/bootrom/…) adds 1.
    /// The value is added on top of the instruction's base cycle, exactly as rp2040js does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long CyclesIO(uint address, bool write)
    {
        var region = address >> 28;
        if (region == 0xD) return 0;              // SIO_START_ADDRESS = 0xD0000000
        if (region == 0x4) return write ? 4 : 3;  // APB_START_ADDRESS = 0x40000000
        return 1;
    }

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

    // ── Interchangeable 16-bit decoder (see Decoders/) ───────────────────────
    // The execution loop is monomorphized per decoder struct; the active one is chosen with a single
    // per-batch switch in Run. NativeLut (function-pointer table) is the default. Lut (managed-delegate
    // table) and Switch (br_table over a handler id) exist so the fastest can be selected per runtime
    // target (native vs WASM) — see the decoder benchmark. Behaviour is identical across all three; they
    // are all derived from the same classification rules that build InstructionDecoder's native table.
    private Decoders.NativeLutDecoder _nativeLutDecoder;
    private Decoders.LutDecoder       _lutDecoder;
    private Decoders.SwitchDecoder    _switchDecoder;
    private DecoderType               _activeDecoder = DecoderType.NativeLut;

    /// <summary>Selects the 16-bit instruction decoder used by <see cref="Run(int)"/>. Behavior is
    /// identical across all three; only dispatch mechanism (and thus throughput) differs. Default is
    /// <see cref="DecoderType.NativeLut"/>.</summary>
    public void SetDecoder(DecoderType type) => _activeDecoder = type;

    /// <summary>The 16-bit decoder currently selected by <see cref="SetDecoder"/>.</summary>
    public DecoderType ActiveDecoder => _activeDecoder;

    private byte* _fetchPtr;
    private uint _fetchMask;
    private uint _fetchBase;
    private uint _fetchLen;

    private const uint EXC_RETURN_HANDLER = 0xFFFFFFF1; // Return to Handler mode, using MSP
    private const uint EXC_RETURN_THREAD_MSP = 0xFFFFFFF9; // Return to Thread mode, using MSP
    private const uint EXC_RETURN_THREAD_PSP = 0xFFFFFFFD; // Return to Thread mode, using PSP

    private const uint SCR_SLEEPONEXIT = 1u << 1;
    private const uint SCR_SEVONPEND   = 1u << 4;

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
        _nativeLutDecoder = Decoders.NativeLutDecoder.Create();
        _lutDecoder       = Decoders.LutDecoder.Create();
        _switchDecoder    = Decoders.SwitchDecoder.Create();
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
        Registers.InterruptPriorities0 = 0xFFFFFFFF;   // all 32 NVIC lines (26-31 are the unassigned lines the SDK hands out as user IRQs)
        Registers.InterruptPriorities1 = 0;
        Registers.InterruptPriorities2 = 0;
        Registers.InterruptPriorities3 = 0;
        Registers.SHPR2 = 0;
        Registers.SHPR3 = 0;

        Cycles = 0;
    }

    /// <summary>
    /// Point the fetch window at the memory holding <paramref name="pc"/>. The run loop tests every fetch against
    /// the window with one unsigned compare, so a PC outside every executable range (the unmapped gaps, the
    /// peripheral regions, past the end of SRAM or the boot ROM) misses it each time and lands here with no
    /// window, which raises the HardFault.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateFetchCache(uint pc)
    {
        if (pc < BusInterconnect.BOOTROM_SIZE)
        {
            _fetchPtr = Bus.PtrBootRom;
            _fetchMask = BusInterconnect.MASK_BOOTROM & ~1u;
            _fetchBase = 0;
            _fetchLen = BusInterconnect.BOOTROM_SIZE;
        }
        else if (pc - BusInterconnect.FLASH_START_ADDRESS < XipSpan)
        {
            _fetchPtr = Bus.PtrFlash;
            _fetchMask = Bus.MaskFlash & ~1u;
            _fetchBase = BusInterconnect.FLASH_START_ADDRESS;
            _fetchLen = XipSpan;
        }
        else if (pc - BusInterconnect.SRAM_START_ADDRESS < BusInterconnect.SRAM_SIZE)
        {
            _fetchPtr = Bus.PtrSram;
            _fetchMask = BusInterconnect.MASK_SRAM & ~1u;
            _fetchBase = BusInterconnect.SRAM_START_ADDRESS;
            _fetchLen = BusInterconnect.SRAM_SIZE;
        }
        else
        {
            // No direct fetch window. A zero-length one makes the loop's range test fail again on the next
            // fetch and re-raise the HardFault. Without this, a fault handler (or any PC change that stays
            // in the same unfetchable range, or a Reset() vector pointing at one) matched the cached window
            // and read through the null pointer.
            _fetchPtr = null;
            _fetchBase = 0;
            _fetchLen = 0;
        }
    }

    private const uint XipSpan = 0x08000000;   // 0x10000000-0x17FFFFFF, see BusInterconnect

    /// <summary>
    /// Execute up to <paramref name="instructions"/> steps using the currently selected 16-bit decoder.
    /// The body is monomorphized per decoder struct by <see cref="RunGeneric{TDecoder}"/>; this public
    /// entry point keeps its historical signature and picks the concrete loop with a single per-batch
    /// switch (the selection cost is amortized over the whole batch, not paid per instruction).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Run(int instructions)
    {
        switch (_activeDecoder)
        {
            case DecoderType.NativeLut: RunGeneric<Decoders.NativeLutDecoder, NoRunLimit>(ref _nativeLutDecoder, instructions); break;
            case DecoderType.Lut:       RunGeneric<Decoders.LutDecoder, NoRunLimit>(ref _lutDecoder, instructions); break;
            case DecoderType.Switch:    RunGeneric<Decoders.SwitchDecoder, NoRunLimit>(ref _switchDecoder, instructions); break;
            default:                    RunGeneric<Decoders.NativeLutDecoder, NoRunLimit>(ref _nativeLutDecoder, instructions); break;
        }
    }

    /// <summary>
    /// A bounded run: slices of <paramref name="slice"/> instructions, one after the other in the same loop, for as
    /// long as <paramref name="gate"/> lets each slice end go by (<see cref="ISliceGate.NextSlice"/>). Each slice is
    /// exactly what <see cref="Run"/>(<paramref name="slice"/>) would execute, a sleep included: falling asleep
    /// charges the rest of the slice and ends the run, as it ends a <see cref="Run"/>. A separate instantiation of
    /// the run loop, so <see cref="Run"/> pays nothing for it.
    /// </summary>
    public void RunGated(ISliceGate gate, int slice)
    {
        Gate = gate;
        GateSlice = slice;
        try
        {
            switch (_activeDecoder)
            {
                case DecoderType.Lut:    RunGeneric<Decoders.LutDecoder, SliceRunLimit>(ref _lutDecoder, slice); break;
                case DecoderType.Switch: RunGeneric<Decoders.SwitchDecoder, SliceRunLimit>(ref _switchDecoder, slice); break;
                default:                 RunGeneric<Decoders.NativeLutDecoder, SliceRunLimit>(ref _nativeLutDecoder, slice); break;
            }
        }
        finally { Gate = null; }
    }

    /// <summary>The gate and slice length of the bounded run in flight (<see cref="RunGated"/>).</summary>
    internal ISliceGate? Gate;
    internal int GateSlice;

    // ── Cut slices (the event-bounded guard of the native adapter, docs/pin-level-buses.md) ──

    /// <summary>A cut run (<see cref="RunCut"/>, <see cref="RunGatedCut"/>) stops before the first slot that would start
    /// at or after this cycle, and charges a sleep only up to it.</summary>
    internal long CutCycle = long.MaxValue;

    /// <summary>Set (by the machine, from inside a slot) to stop a cut run right after the slot in flight.</summary>
    internal bool CutRequest;

    /// <summary>Slots of the slice in flight that a cut run did not run (0 when it ran to the end of the slice).</summary>
    internal int CutLeft;

    /// <summary>
    /// <see cref="Run"/>, stopped early by <see cref="CutCycle"/> or <see cref="CutRequest"/>: the slots it runs are
    /// exactly the first ones <see cref="Run"/> would. The caller makes sure the first slot runs (the cut cycle is past
    /// the current one and no request is pending) and reads <see cref="CutLeft"/> after.
    /// </summary>
    internal void RunCut(int instructions)
    {
        CutLeft = 0;
        switch (_activeDecoder)
        {
            case DecoderType.Lut:    RunGeneric<Decoders.LutDecoder, CutRunLimit>(ref _lutDecoder, instructions); break;
            case DecoderType.Switch: RunGeneric<Decoders.SwitchDecoder, CutRunLimit>(ref _switchDecoder, instructions); break;
            default:                 RunGeneric<Decoders.NativeLutDecoder, CutRunLimit>(ref _nativeLutDecoder, instructions); break;
        }
    }

    /// <summary><see cref="RunGated"/> with the cut of <see cref="RunCut"/>: <see cref="CutLeft"/> is then counted in
    /// the slice the run ended in.</summary>
    internal void RunGatedCut(ISliceGate gate, int slice)
    {
        Gate = gate;
        GateSlice = slice;
        CutLeft = 0;
        try
        {
            switch (_activeDecoder)
            {
                case DecoderType.Lut:    RunGeneric<Decoders.LutDecoder, SliceCutRunLimit>(ref _lutDecoder, slice); break;
                case DecoderType.Switch: RunGeneric<Decoders.SwitchDecoder, SliceCutRunLimit>(ref _switchDecoder, slice); break;
                default:                 RunGeneric<Decoders.NativeLutDecoder, SliceCutRunLimit>(ref _nativeLutDecoder, slice); break;
            }
        }
        finally { Gate = null; }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunGeneric<TDecoder, TLimit>(ref TDecoder decoder, int instructions)
        where TDecoder : struct, Decoders.IInstructionDecoder
        where TLimit : struct, IRunLimit
    {
        var fetchPtr = _fetchPtr;
        var fetchMask = _fetchMask;
        var fetchBase = _fetchBase;
        var fetchLen = _fetchLen;

        while (instructions-- > 0 || TLimit.Refill(this, ref instructions))
        {
            // A cut run stops before the slot (folded away in every other run).
            if (TLimit.HasCut && (CutRequest || Cycles >= CutCycle)) { CutLeft = instructions + 1; break; }

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
                    fetchBase = _fetchBase;
                    fetchLen = _fetchLen;
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
                    if (TLimit.HasCut)
                    {
                        // A cut run sleeps up to the cut cycle only (at least one slot: checked above).
                        long left = instructions + 1L, room = CutCycle - Cycles;
                        if (room < left) { Cycles += room; CutLeft = (int)(left - room); break; }
                    }
                    Cycles += (uint)(instructions + 1);
                    return;
                }
            }

            var pc = Registers.PC;

            // FAST GUARD
            if (pc - fetchBase >= fetchLen)
            {
                // FALLBACK
                UpdateFetchCache(pc);

                fetchPtr = _fetchPtr;
                fetchMask = _fetchMask;
                fetchBase = _fetchBase;
                fetchLen = _fetchLen;

                if (fetchPtr == null)
                {
                    // PC landed in an un-executable region — raise HardFault per ARMv6-M spec
                    ExceptionEntry(EXC_HARDFAULT);
                    if (IsLockedUp) return;
                    UpdateFetchCache(Registers.PC);
                    fetchPtr  = _fetchPtr;
                    fetchMask = _fetchMask;
                    fetchBase = _fetchBase;
                    fetchLen = _fetchLen;
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
                fetchBase = _fetchBase;
                fetchLen = _fetchLen;
                Cycles++;
                continue;
            }

            var opcode = Unsafe.ReadUnaligned<ushort>(fetchPtr + (pc & fetchMask));

            // PRE-UPDATE PC (Speculative)
            Registers.PC = pc + 2;

            Cycles++;

            // DISPATCH
            decoder.Dispatch16(opcode, this);
        }

        _fetchBase = fetchBase;
        _fetchLen = fetchLen;
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
        var fetchBase = _fetchBase;
        var fetchLen = _fetchLen;

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
                    fetchBase = _fetchBase;
                    fetchLen = _fetchLen;
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

            if (pc - fetchBase >= fetchLen)
            {
                UpdateFetchCache(pc);

                fetchPtr = _fetchPtr;
                fetchMask = _fetchMask;
                fetchBase = _fetchBase;
                fetchLen = _fetchLen;

                if (fetchPtr == null)
                {
                    ExceptionEntry(EXC_HARDFAULT);
                    if (IsLockedUp) return;
                    UpdateFetchCache(Registers.PC);
                    fetchPtr  = _fetchPtr;
                    fetchMask = _fetchMask;
                    fetchBase = _fetchBase;
                    fetchLen = _fetchLen;
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
                fetchBase = _fetchBase;
                fetchLen = _fetchLen;
                Cycles++;
                continue;
            }

            var opcode = Unsafe.ReadUnaligned<ushort>(fetchPtr + (pc & fetchMask));

            observer.OnInstruction(pc, opcode, Cycles);

            Registers.PC = pc + 2;

            Cycles++;

            decoder.Dispatch(opcode, this);
        }

        _fetchBase = fetchBase;
        _fetchLen = fetchLen;
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

        // rp2040js models exception entry as 0 extra cycles: the handler's first instruction is
        // charged its own base on the next step, so nothing is added here.

        OnExceptionEntry?.Invoke(exceptionNumber);
    }

    // ================================================================
    // Interrupt / Exception management (called by PPB peripheral)
    // ================================================================

    /// <summary>
    /// The other processor. On silicon a system peripheral's IRQ line is wired to BOTH cores'
    /// interrupt controllers (RP2040 datasheet §2.3.2): each NVIC independently enables or masks
    /// it, and a core that has it disabled simply latches a pending bit it never takes. Without
    /// the peer, an ISR registered on Core 1 — or a Core 1 <c>WFE</c> waiting for a timer alarm,
    /// which is what <c>_thread</c> + <c>time.sleep()</c> comes down to — is never woken.
    /// SEV also crosses here: it is an event for the whole cluster, not for the issuing core.
    /// </summary>
    public CortexM0Plus? SiblingCore;

    /// <summary>
    /// SIO_IRQ_PROC0 / SIO_IRQ_PROC1: the only two RP2040 IRQ numbers that are per-core rather
    /// than shared, so they must NOT reach the other core's NVIC.
    /// </summary>
    private const int IRQ_SIO_PROC0 = 15, IRQ_SIO_PROC1 = 16;

    public void SetInterrupt(int irq, bool pending)
    {
        if (irq is < 0 or > 31) return;
        if (irq is not (IRQ_SIO_PROC0 or IRQ_SIO_PROC1))
            SiblingCore?.SetInterruptLine(irq, pending);
        SetInterruptLine(irq, pending);
    }

    /// <summary>
    /// Drives a line of THIS core's NVIC only, for interrupt sources that are wired per core (IO_BANK0's
    /// PROC0 / PROC1 interrupts reach the NVIC of the core they belong to, not both).
    /// </summary>
    public void SetInterruptLocal(int irq, bool pending)
    {
        if (irq is < 0 or > 31) return;
        SetInterruptLine(irq, pending);
    }

    /// <summary>Software-pends a line in THIS core's NVIC only (NVIC_ISPR is per core; <c>irq_set_pending()</c>).</summary>
    public void PendInterruptLocal(int irq)
    {
        if (irq is < 0 or > 31) return;
        SetInterruptLine(irq, true);
    }

    /// <summary>Drives this core's NVIC line only — never crosses to the sibling.</summary>
    private void SetInterruptLine(int irq, bool pending)
    {
        var bit = 1u << irq;
        if (pending)
        {
            // SCR.SEVONPEND (B3.2.7): a not-pending -> pending transition, even of a disabled interrupt,
            // registers an event that wakes WFE.
            if ((Registers.SCR & SCR_SEVONPEND) != 0 && (Registers.PendingInterrupts & bit) == 0)
                Registers.EventRegistered = true;
            Registers.PendingInterrupts |= bit;
        }
        else
        {
            Registers.PendingInterrupts &= ~bit;
        }
        Registers.InterruptsUpdated = true;
    }

    /// <summary>
    /// SEV: sets the event register of every processor in the cluster (ARMv6-M §B1.5.19). On the
    /// RP2040 this is how pico-sdk's alarm callback (<c>sleep_until_callback</c> → <c>__sev()</c>,
    /// serviced on Core 0) releases the other core from <c>best_effort_wfe_or_timeout</c>'s WFE.
    /// </summary>
    public void SignalEvent()
    {
        Registers.EventRegistered = true;
        if (SiblingCore is { } peer) peer.Registers.EventRegistered = true;
    }

    public void TriggerNmi() { Registers.PendingNMI = true; Registers.InterruptsUpdated = true; }
    public void TriggerSysTick() { Registers.PendingSystick = true; Registers.InterruptsUpdated = true; }
    public void TriggerPendSv() { Registers.PendingPendSV = true; Registers.InterruptsUpdated = true; }
    public void TriggerHardFault() => ExceptionEntry(EXC_HARDFAULT);

    /// <summary>
    /// BXWritePC / LoadWritePC (A2.3.1) for the cases the inlined fast path does not take. An EXC_RETURN value
    /// (0xFFFFFFF0-0xFFFFFFFF) only means "return from exception" in Handler mode; in Thread mode it is just an
    /// address nothing can be fetched from. An even target sets EPSR.T = 0, which faults when the next
    /// instruction is fetched, so the HardFault is taken here with the target as the faulting address.
    /// </summary>
    public void BxWritePc(uint target)
    {
        if (target >= 0xFFFFFFF0 && Registers.IPSR != 0)
        {
            ExceptionReturn(target);
            return;
        }
        if ((target & 1) == 0)
        {
            Registers.PC = target;
            ExceptionEntry(EXC_HARDFAULT);
            return;
        }
        Registers.PC = target & 0xFFFFFFFE;
    }

    /// <summary>
    /// HardFault from a synchronous (precise) fault: ARMv6-M B1.5.6 ReturnAddress() is the address of the
    /// instruction that caused it (UNDEFINED, BKPT with no debugger, unaligned or unmapped access), not the
    /// next one, so the caller passes the faulting instruction's address.
    /// </summary>
    public void TriggerHardFaultAt(uint instructionAddress)
    {
        Registers.PC = instructionAddress;
        ExceptionEntry(EXC_HARDFAULT);
    }

    /// <summary>
    /// SVC executed. It pends SVCall like any exception, unless SVCall's group priority is not strictly
    /// higher than the current execution priority (PRIMASK counts as priority 0, so SVC with interrupts
    /// masked, or from a handler at the same or a higher priority): then it escalates to HardFault
    /// (B1.5.4 "Priority escalation"), with the ReturnAddress of the SVC, i.e. the next instruction.
    /// </summary>
    public void CallSupervisor()
    {
        var execPriority = ExceptionPriority(Registers.IPSR);
        if (Registers.PRIMASK != 0 && execPriority > 0)
            execPriority = 0;
        if (ExceptionPriority(EXC_SVCALL) < execPriority)
        {
            Registers.PendingSVCall = true;
            Registers.InterruptsUpdated = true;
        }
        else
        {
            ExceptionEntry(EXC_HARDFAULT);
        }
    }

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

    /// <summary>
    /// ICSR.VECTPENDING (B3.2.4): number of the highest-priority pending exception, 0 when none. Enable and
    /// priority are honoured, PRIMASK and the current execution priority are not.
    /// </summary>
    public uint HighestPendingException()
    {
        uint best = 0;
        var bestPriority = int.MaxValue;
        if (Registers.PendingNMI) return EXC_NMI;
        if (Registers.PendingSVCall)  { best = EXC_SVCALL; bestPriority = ExceptionPriority(EXC_SVCALL); }
        if (Registers.PendingPendSV)
        {
            var p = ExceptionPriority(EXC_PENDSV);
            if (p < bestPriority) { best = EXC_PENDSV; bestPriority = p; }
        }
        if (Registers.PendingSystick)
        {
            var p = ExceptionPriority(EXC_SYSTICK);
            if (p < bestPriority) { best = EXC_SYSTICK; bestPriority = p; }
        }
        var pending = Registers.PendingInterrupts & Registers.EnabledInterrupts;
        while (pending != 0)
        {
            var irq = System.Numerics.BitOperations.TrailingZeroCount(pending);
            pending &= pending - 1;
            var p = IrqPriority(irq);
            if (p < bestPriority) { best = (uint)(irq + 16); bestPriority = p; }
        }
        return best;
    }

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

        // rp2040js models exception return as 0 extra cycles: the BX/POP-pc that drives it already
        // charged its own penalty, so nothing is added here.
        // After returning from an ISR, re-check interrupts so that a still-pending
        // higher-priority IRQ (e.g. USB after SysTick) fires immediately, AND signal
        // that an event was registered so the next WFE consumes it instead of sleeping.
        // Without `EventRegistered = true`, pico-sdk WFE-loops that expect to be woken
        // by the very IRQ we just serviced will deadlock — the alarm fires once, the
        // handler runs, but the WFE that follows sleeps forever waiting for an event
        // that already happened. rp2040js cortex-m0-core.ts:339-341 sets both flags.
        Registers.InterruptsUpdated = true;
        Registers.EventRegistered = true;

        // SCR.SLEEPONEXIT (B3.2.7): returning to Thread mode puts the core to sleep until the next exception.
        // A still-pending exception is taken first (interrupts are checked before the sleep test), which wakes it.
        if (returnToThread && (Registers.SCR & SCR_SLEEPONEXIT) != 0)
        {
            Registers.EventRegistered = false;   // the return's own event must not cancel the sleep
            Registers.Waiting = true;
        }

        OnExceptionReturn?.Invoke(excReturn);
    }
}

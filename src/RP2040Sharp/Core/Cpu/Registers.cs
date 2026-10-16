using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RP2040.Core.Cpu;

[StructLayout(LayoutKind.Sequential)]
public struct Registers
{
    // --- Low Registers (R0-R7) ---
    public uint R0;
    public uint R1;
    public uint R2;
    public uint R3;
    public uint R4;
    public uint R5;
    public uint R6;
    public uint R7;

    // --- High Registers (R8-R12) ---
    public uint R8;
    public uint R9;
    public uint R10;
    public uint R11;
    public uint R12;

    // R13: Stack Pointer (SP)
    public uint SP;

    // R14: Link Register (LR)
    public uint LR;

    // R15: Program Counter (PC)
    public uint PC;

    // --- Backing Stores for Stack Pointers ---
    public uint MSP_Storage;
    public uint PSP_Storage;

    // --- System Registers ---
    public uint PRIMASK; // Bit 0: PM
    public uint CONTROL; // Bit 1: SPSEL, Bit 0: nPRIV
    public uint IPSR; // Exception Number (0 = Thread Mode)

    // --- Program Status Register (xPSR) ---
    public bool N; // Negative
    public bool Z; // Zero
    public bool C; // Carry
    public bool V; // Overflow

    // --- Interrupt / Exception State ---
    public uint VTOR;                 // Vector Table Offset Register
    public uint PendingInterrupts;    // Bitmap of the 32 NVIC lines pending (0-25 peripherals, 26-31 software/user IRQs)
    public uint EnabledInterrupts;    // Bitmap of the 32 NVIC lines enabled
    public uint InterruptPriorities0; // Priority bucket 0 (highest)
    public uint InterruptPriorities1;
    public uint InterruptPriorities2;
    public uint InterruptPriorities3; // Priority bucket 3 (lowest)
    public uint SHPR2;                // SVC priority (bits 31:24)
    public uint SHPR3;                // PendSV (bits 23:16) + SysTick (bits 31:24) priority
    public bool PendingNMI;
    public bool PendingPendSV;
    public bool PendingSVCall;
    public bool PendingSystick;
    public bool InterruptsUpdated;    // Signal Run() to call CheckForInterrupts
    public bool EventRegistered;      // SEV/WFE event flag
    public bool Waiting;              // WFI/WFE sleep state
    public uint SCR;                  // SCB.SCR: SLEEPONEXIT (bit 1), SLEEPDEEP (bit 2), SEVONPEND (bit 4)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetC() => Unsafe.As<bool, byte>(ref C);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetxPsr()
    {
        uint apsr = 0;
        if (N)
            apsr |= 0x80000000;
        if (Z)
            apsr |= 0x40000000;
        if (C)
            apsr |= 0x20000000;
        if (V)
            apsr |= 0x10000000;

        // xPSR combina APSR, EPSR (Thumb bit siempre 1) e IPSR
        return apsr | 0x01000000 | (IPSR & 0x3F);
    }

    /// <summary>
    /// Write the APSR (condition flags) and IPSR portions of xPSR. Used by the GDB stub
    /// when a debugger writes the cpsr/xPSR register. The EPSR Thumb bit is fixed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetxPsr(uint value)
    {
        N = (value & 0x80000000) != 0;
        Z = (value & 0x40000000) != 0;
        C = (value & 0x20000000) != 0;
        V = (value & 0x10000000) != 0;
        IPSR = value & 0x3F;
    }

    // Interrupt Status Register (IPSR) y Execution (EPSR) se pueden manejar aparte o implícitamente.

    /// <summary>
    /// Helper para obtener el valor indexado (sugar syntax para el Span)
    /// </summary>
    public ref uint this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [UnscopedRef]
        get { return ref Unsafe.Add(ref R0, index); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe uint* GetBasePointer()
    {
        return (uint*)Unsafe.AsPointer(ref R0);
    }
}

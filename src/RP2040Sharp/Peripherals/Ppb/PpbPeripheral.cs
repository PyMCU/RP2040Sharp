using System.Numerics;
using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Ppb;

/// <summary>
/// Private Peripheral Bus (PPB) — NVIC, SysTick, and System Control Block (SCB).
/// Base address: 0xE000E000. Register with BusInterconnect via MapDevice(0xE, ppb).
/// Addresses received from the bus are already masked (address &amp; 0x0FFFFFFF),
/// so 0xE000Exyz arrives as 0x0000Exyz; local offset = address &amp; 0xFFF.
/// </summary>
public sealed class PpbPeripheral : IMemoryMappedDevice, ITickable
{
    /// <summary>
    /// Fired when NVIC_ISER enables new IRQ bits. Subscribers should re-check
    /// their interrupt state (level-triggered IRQs may have been cleared by ICPR).
    /// </summary>
    public Action? OnInterruptEnable;
    // ── SysTick offsets ──────────────────────────────────────────────
    private const uint SYST_CSR   = 0x010;  // Control / Status
    private const uint SYST_RVR   = 0x014;  // Reload Value
    private const uint SYST_CVR   = 0x018;  // Current Value (write clears)
    private const uint SYST_CALIB = 0x01C;  // Calibration (RO, no data)

    // ── NVIC offsets ─────────────────────────────────────────────────
    private const uint NVIC_ISER  = 0x100;  // Set-Enable
    private const uint NVIC_ICER  = 0x180;  // Clear-Enable
    private const uint NVIC_ISPR  = 0x200;  // Set-Pending
    private const uint NVIC_ICPR  = 0x280;  // Clear-Pending
    private const uint NVIC_IPR0  = 0x400;  // Priority R0 (IPR0-IPR7)
    private const uint NVIC_IPR7  = 0x41C;  // Priority R7

    // ── SCB offsets ──────────────────────────────────────────────────
    private const uint SCB_CPUID  = 0xD00;  // Processor ID (RO)
    private const uint SCB_ICSR   = 0xD04;  // Interrupt Control / State
    private const uint SCB_VTOR   = 0xD08;  // Vector Table Offset
    private const uint SCB_AIRCR  = 0xD0C;  // Application Interrupt / Reset Control
    private const uint SCB_SHPR2  = 0xD1C;  // System Handler Priority 2 (SVC bits 31:24)
    private const uint SCB_SHPR3  = 0xD20;  // System Handler Priority 3 (PendSV[23:16] / SysTick[31:24])

    private readonly CortexM0Plus _cpu;

    // SysTick state
    private uint _systCsr;
    private uint _systRvr;
    private long _systCvr;      // kept as long to handle large delta gracefully
    private long _systAnchor;   // _cpu.Cycles at which _systCvr was last brought up to date

    // NVIC priority registers — 8 × uint → 32 IRQs, 2 priority bits each (bits 7:6)
    private readonly uint[] _nvicIpr = new uint[8];

    public uint Size => 0x1000;

    public PpbPeripheral(CortexM0Plus cpu)
    {
        _cpu = cpu;
    }

    // ── ITickable ────────────────────────────────────────────────────

    /// <summary>Bring SysTick up to date with the owning core's cycle counter, firing COUNTFLAG and
    /// the SysTick exception for any reload boundaries crossed since the last sync.
    /// <para>SysTick counts off the core clock, so it must be sampled against <see cref="CortexM0Plus.Cycles"/>
    /// and never against the peripheral tick quantum: firmware busy-waits on CVR
    /// (<c>machine.bitstream</c> times WS2812 bit widths this way), and a CVR that only moves on the
    /// tick boundary makes every such wait expire at the same instant.</para>
    /// <paramref name="deltaCycles"/> is ignored — the router ticks both cores' PPBs with a shared
    /// delta, but each core's SysTick advances with that core's own cycles.</summary>
    public void Tick(long deltaCycles) => SyncSysTick();

    private long SystReload => _systRvr > 0 ? _systRvr : 0xFFFFFF;

    private void SyncSysTick()
    {
        var now = _cpu.Cycles;
        var delta = now - _systAnchor;
        _systAnchor = now;

        if ((_systCsr & 1) == 0 || delta <= 0) return;   // SysTick not enabled, or nothing elapsed

        _systCvr -= delta;

        while (_systCvr <= 0)
        {
            _systCsr |= 1u << 16;   // COUNTFLAG
            _systCvr += SystReload;

            if ((_systCsr & 2) != 0)   // TICKINT
                _cpu.TriggerSysTick();
        }
    }

    // ── IMemoryMappedDevice — reads ──────────────────────────────────

    public uint ReadWord(uint address)
    {
        var offset = address & 0xFFF;

        if (offset >= NVIC_IPR0 && offset <= NVIC_IPR7)
            return _nvicIpr[(offset - NVIC_IPR0) >> 2];

        if (offset is SYST_CSR or SYST_CVR)
            SyncSysTick();

        return offset switch
        {
            SYST_CSR   => _systCsr,
            SYST_RVR   => _systRvr,
            SYST_CVR   => (uint)(_systCvr & 0xFFFFFF),
            SYST_CALIB => 0,
            NVIC_ISER  => _cpu.Registers.EnabledInterrupts,
            NVIC_ICER  => _cpu.Registers.EnabledInterrupts,
            NVIC_ISPR  => _cpu.Registers.PendingInterrupts,
            NVIC_ICPR  => _cpu.Registers.PendingInterrupts,
            SCB_CPUID  => 0x410CC601,   // Cortex-M0+, r0p1
            SCB_ICSR   => BuildIcsr(),
            SCB_VTOR   => _cpu.Registers.VTOR,
            SCB_AIRCR  => 0xFA050000,   // VECTKEY read value, no reset pending
            SCB_SHPR2  => _cpu.Registers.SHPR2,
            SCB_SHPR3  => _cpu.Registers.SHPR3,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    // ── IMemoryMappedDevice — writes ─────────────────────────────────

    public void WriteWord(uint address, uint value)
    {
        var offset = address & 0xFFF;

        if (offset >= NVIC_IPR0 && offset <= NVIC_IPR7)
        {
            var idx = (int)((offset - NVIC_IPR0) >> 2);
            _nvicIpr[idx] = value & 0xC0C0C0C0;   // only top 2 bits per byte
            UpdatePriorityBucket(idx, _nvicIpr[idx]);
            return;
        }

        switch (offset)
        {
            case SYST_CSR:
                SyncSysTick();
                _systCsr = value & 0x7;   // ENABLE | TICKINT | CLKSOURCE
                break;

            case SYST_RVR:
                SyncSysTick();
                _systRvr = value & 0x00FFFFFF;
                break;

            case SYST_CVR:
                SyncSysTick();
                // ARMv6-M: a CVR write clears the counter and COUNTFLAG and must not raise a SysTick
                // exception. Parking at 0 would instead roll over on the very next cycle, so seed the
                // reload the hardware performs on the following clock.
                _systCvr = SystReload;
                _systCsr &= ~(1u << 16);   // clear COUNTFLAG
                break;

            case NVIC_ISER:
                _cpu.Registers.EnabledInterrupts |= value;
                _cpu.Registers.InterruptsUpdated = true;
                OnInterruptEnable?.Invoke();
                break;

            case NVIC_ICER:
                _cpu.Registers.EnabledInterrupts &= ~value;
                break;

            case NVIC_ISPR:
                SetPendingBits(value & 0x3FFFFFF);
                break;

            case NVIC_ICPR:
                _cpu.Registers.PendingInterrupts &= ~value;
                break;

            case SCB_ICSR:
                if ((value & (1u << 31)) != 0) _cpu.TriggerNmi();
                if ((value & (1u << 28)) != 0) _cpu.TriggerPendSv();
                if ((value & (1u << 27)) != 0)
                {
                    _cpu.Registers.PendingPendSV = false;
                    _cpu.Registers.InterruptsUpdated = true;
                }
                if ((value & (1u << 26)) != 0) _cpu.TriggerSysTick();
                if ((value & (1u << 25)) != 0)
                {
                    _cpu.Registers.PendingSystick = false;
                    _cpu.Registers.InterruptsUpdated = true;
                }
                break;

            case SCB_VTOR:
                _cpu.Registers.VTOR = value & 0xFFFFFF00;
                break;

            case SCB_AIRCR:
                // SYSRESETREQ (bit2) could trigger board-level reset; ignored here
                break;

            case SCB_SHPR2:
                _cpu.Registers.SHPR2 = value & 0xC0000000;
                _cpu.Registers.InterruptsUpdated = true;
                break;

            case SCB_SHPR3:
                _cpu.Registers.SHPR3 = value & 0xC0C00000;
                _cpu.Registers.InterruptsUpdated = true;
                break;
        }
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 2) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Private helpers ──────────────────────────────────────────────

    private uint BuildIcsr()
    {
        ref readonly var regs = ref _cpu.Registers;
        var icsr = regs.IPSR & 0x3Fu;
        if (regs.PendingNMI)     icsr |= 1u << 31;
        if (regs.PendingPendSV)  icsr |= 1u << 28;
        if (regs.PendingSystick) icsr |= 1u << 26;
        return icsr;
    }

    private void SetPendingBits(uint mask)
    {
        while (mask != 0)
        {
            var irq = BitOperations.TrailingZeroCount(mask);
            _cpu.SetInterrupt(irq, true);
            mask &= mask - 1;   // clear lowest set bit
        }
    }

    private void UpdatePriorityBucket(int iprIdx, uint iprValue)
    {
        // Each InterruptPrioritiesN field is the bitmask of IRQs currently at priority
        // level N; an IPR write moves its 4 IRQs to the level in bits 7:6 of each byte.
        for (var b = 0; b < 4; b++)
        {
            var irq = iprIdx * 4 + b;
            if (irq > 25) break;
            var level = (int)((iprValue >> (b * 8 + 6)) & 3);
            var bit = 1u << irq;

            _cpu.Registers.InterruptPriorities0 &= ~bit;
            _cpu.Registers.InterruptPriorities1 &= ~bit;
            _cpu.Registers.InterruptPriorities2 &= ~bit;
            _cpu.Registers.InterruptPriorities3 &= ~bit;
            switch (level)
            {
                case 0: _cpu.Registers.InterruptPriorities0 |= bit; break;
                case 1: _cpu.Registers.InterruptPriorities1 |= bit; break;
                case 2: _cpu.Registers.InterruptPriorities2 |= bit; break;
                default: _cpu.Registers.InterruptPriorities3 |= bit; break;
            }
        }
        _cpu.Registers.InterruptsUpdated = true;
    }
}

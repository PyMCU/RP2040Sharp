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
    private const uint SCB_SCR    = 0xD10;  // System Control Register
    private const uint SCB_CCR    = 0xD14;  // Configuration and Control Register (read-only)
    private const uint SCB_SHPR2  = 0xD1C;  // System Handler Priority 2 (SVC bits 31:24)
    private const uint SCB_SHPR3  = 0xD20;  // System Handler Priority 3 (PendSV[23:16] / SysTick[31:24])
    private const uint SCB_SHCSR  = 0xD24;  // System Handler Control and State (SVCALLPENDED bit 15)

    // ── MPU offsets (datasheet 2.4.6: 8 regions, unified) ──────────────
    private const uint MPU_TYPE   = 0xD90;
    private const uint MPU_CTRL   = 0xD94;
    private const uint MPU_RNR    = 0xD98;
    private const uint MPU_RBAR   = 0xD9C;
    private const uint MPU_RASR   = 0xDA0;

    private const uint COUNTFLAG  = 1u << 16;

    // Cycles per 1 us SysTick reference tick (the watchdog's timer_tick). Fixed at the 125 MHz system clock
    // the emulator runs at, the same assumption TimerPeripheral makes (the PLL is a register stub).
    private const long TICK_CYCLES = 125;

    private readonly CortexM0Plus _cpu;

    // SysTick state
    private uint _systCsr;
    private uint _systRvr;
    // RVR has no defined power-on value (ARM ARM B3.3.3 "UNKNOWN") and firmware exists that never writes it:
    // MicroPython's machine.bitstream enables SysTick with CSR = 5, then only clears CVR and polls it as a
    // 24-bit down counter. Until RVR is first programmed the counter therefore reloads 0xFFFFFF, while RVR
    // itself still reads 0 (the SVD reset value). A programmed RVR, including 0, follows the architecture.
    private bool _systRvrWritten;
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

    // MPU registers. The regions are stored and read back, but nothing enforces them: accesses are never
    // checked against an MPU region, so a violation does not fault (see docs/limitations).
    private uint _mpuCtrl, _mpuRnr;
    private readonly uint[] _mpuRbar = new uint[8];
    private readonly uint[] _mpuRasr = new uint[8];

    /// <summary>
    /// AIRCR.SYSRESETREQ written with the VECTKEY (datasheet 2.4.4: it resets the Cortex-M0+ core that asked,
    /// not the debug block or the rest of the chip). The machine performs the reset between core batches.
    /// </summary>
    public Action? OnSystemResetRequest;

    /// <summary>Core reset: the SysTick, NVIC priority, SCR and MPU state this block owns returns to its reset value.</summary>
    public void Reset()
    {
        _systCsr = _systRvr = 0;
        _systRvrWritten = false;
        _systCvr = 0;
        _systAnchor = _cpu.Cycles;
        Array.Clear(_nvicIpr);
        _mpuCtrl = _mpuRnr = 0;
        Array.Clear(_mpuRbar);
        Array.Clear(_mpuRasr);
    }

    /// <summary>
    /// Bring the SysTick counter up to date and report COUNTFLAG / the exception for the reloads crossed.
    /// ARMv6-M B3.3: while enabled the counter, on each clock, reloads from RVR if it is 0, otherwise
    /// decrements, and COUNTFLAG sets on the 1 -> 0 transition, so the period is RVR + 1 clocks and RVR = 0
    /// never fires. A CVR write leaves it at 0 (the next clock reloads). The clock is the core clock when
    /// CLKSOURCE = 1, otherwise the 1 us reference tick (datasheet 2.4.5.1.1).
    /// </summary>
    private void SyncSysTick()
    {
        var now = _cpu.Cycles;
        var before = _systAnchor;
        _systAnchor = now;

        if ((_systCsr & 1) == 0 || now <= before) return;   // SysTick not enabled, or nothing elapsed

        var steps = (_systCsr & 4) != 0 ? now - before : now / TICK_CYCLES - before / TICK_CYCLES;
        if (steps <= 0) return;

        var reload = _systRvrWritten ? (long)_systRvr : 0xFFFFFF;
        var cvr = _systCvr;
        var fired = false;

        if (cvr > 0)
        {
            if (steps < cvr)
            {
                _systCvr = cvr - steps;
                return;
            }
            steps -= cvr;
            cvr = 0;
            fired = true;
        }

        if (reload > 0 && steps > 0)
        {
            var period = reload + 1;
            if (steps >= period) fired = true;
            var rem = steps % period;
            cvr = rem == 0 ? 0 : reload - (rem - 1);
        }

        _systCvr = cvr;
        if (!fired) return;

        _systCsr |= COUNTFLAG;
        if ((_systCsr & 2) != 0)   // TICKINT
            _cpu.TriggerSysTick();
    }

    // ── IMemoryMappedDevice — reads ──────────────────────────────────

    public uint ReadWord(uint address)
    {
        var value = Peek(address);

        // ARMv6-M B3.3.2: COUNTFLAG is cleared by a read of SYST_CSR (not by the read-modify-write a
        // narrow write performs, which goes through Peek).
        if ((address & 0xFFF) == SYST_CSR)
            _systCsr &= ~COUNTFLAG;

        return value;
    }

    /// <summary>Register value without read side effects.</summary>
    private uint Peek(uint address)
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
            SCB_SCR    => _cpu.Registers.SCR,
            SCB_CCR    => 0x208,        // STKALIGN (bit 9) and UNALIGN_TRP (bit 3) read as one (datasheet 2.4.4)
            SCB_SHPR2  => _cpu.Registers.SHPR2,
            SCB_SHPR3  => _cpu.Registers.SHPR3,
            SCB_SHCSR  => _cpu.Registers.PendingSVCall ? 1u << 15 : 0u,   // SVCALLPENDED
            MPU_TYPE   => 0x800,        // DREGION = 8, unified (datasheet 2.4.6)
            MPU_CTRL   => _mpuCtrl,
            MPU_RNR    => _mpuRnr,
            MPU_RBAR   => (_mpuRbar[_mpuRnr] & 0xFFFFFF00) | _mpuRnr,   // VALID reads as 0, REGION reads RNR
            MPU_RASR   => _mpuRasr[_mpuRnr],
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
                _systCsr = (_systCsr & COUNTFLAG) | (value & 0x7);   // ENABLE | TICKINT | CLKSOURCE
                break;

            case SYST_RVR:
                SyncSysTick();
                _systRvr = value & 0x00FFFFFF;
                _systRvrWritten = true;
                break;

            case SYST_CVR:
                SyncSysTick();
                // Datasheet SYST_CVR: any write clears the counter to 0 and clears COUNTFLAG. It raises no
                // exception; the next enabled clock reloads from RVR.
                _systCvr = 0;
                _systCsr &= ~COUNTFLAG;
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
                SetPendingBits(value);
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

            case SCB_SCR:
                _cpu.Registers.SCR = value & 0x16;   // SEVONPEND | SLEEPDEEP | SLEEPONEXIT
                break;

            case SCB_SHCSR:
                _cpu.Registers.PendingSVCall = (value & (1u << 15)) != 0;   // SVCALLPENDED
                _cpu.Registers.InterruptsUpdated = true;
                break;

            case MPU_CTRL: _mpuCtrl = value & 0x7; break;   // PRIVDEFENA | HFNMIENA | ENABLE
            case MPU_RNR:  _mpuRnr = value & 0x7; break;    // 8 regions: unimplemented bits are RAZ/WI
            case MPU_RBAR:
                if ((value & 0x10) != 0)                    // VALID: the REGION field selects the region
                    _mpuRnr = value & 0x7;
                _mpuRbar[_mpuRnr] = value & 0xFFFFFF00;
                break;
            case MPU_RASR: _mpuRasr[_mpuRnr] = value & 0xFFFFFF3F; break;

            case SCB_VTOR:
                _cpu.Registers.VTOR = value & 0xFFFFFF00;
                break;

            case SCB_AIRCR:
                // Writes are ignored unless VECTKEY (bits 31:16) is 0x05FA. SYSRESETREQ resets the core.
                if ((value >> 16) == 0x05FA && (value & 4) != 0)
                    OnSystemResetRequest?.Invoke();
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
        var current = Peek(aligned);
        WriteWord(aligned, (current & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) << 3);
        var current = Peek(aligned);
        WriteWord(aligned, (current & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Private helpers ──────────────────────────────────────────────

    private uint BuildIcsr()
    {
        ref readonly var regs = ref _cpu.Registers;
        var icsr = regs.IPSR & 0x3Fu;                       // VECTACTIVE
        icsr |= _cpu.HighestPendingException() << 12;       // VECTPENDING
        if ((regs.PendingInterrupts & regs.EnabledInterrupts) != 0) icsr |= 1u << 22;   // ISRPENDING
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
            _cpu.PendInterruptLocal(irq);   // ISPR is per-core: must not pend the other core's NVIC too
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
            if (irq > 31) break;
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

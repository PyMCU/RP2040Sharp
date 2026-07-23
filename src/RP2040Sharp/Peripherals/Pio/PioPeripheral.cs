using RP2040.Core;
using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Pio;

/// <summary>
/// RP2040 PIO block.
/// PIO0 base: 0x50200000, PIO1 base: 0x50300000.
/// 4 state machines. 32 × 16-bit instruction words.
/// Implements ITickable; tick at system clock granularity.
/// </summary>
public sealed class PioPeripheral : IMemoryMappedDevice, ITickable
{
    private const int SM_COUNT    = 4;
    private const int INSTR_COUNT = 32;

    // ── Register offsets ─────────────────────────────────────────────
    private const uint REG_CTRL            = 0x000;
    private const uint REG_FSTAT           = 0x004;
    private const uint REG_FDEBUG          = 0x008;
    private const uint REG_FLEVEL          = 0x00C;
    private const uint REG_TXF_BASE        = 0x010;   // TXF0-TXF3, 4 bytes each
    private const uint REG_RXF_BASE        = 0x020;   // RXF0-RXF3, 4 bytes each
    private const uint REG_IRQ             = 0x030;
    private const uint REG_IRQ_FORCE       = 0x034;
    private const uint REG_INPUT_SYNC      = 0x038;
    private const uint REG_DBG_PADOUT      = 0x03C;
    private const uint REG_DBG_PADOE       = 0x040;
    private const uint REG_DBG_CFGINFO     = 0x044;
    private const uint REG_INSTR_MEM_BASE  = 0x048;   // 32 entries × 4 bytes = 0x048..0x0C4
    private const uint REG_SM_BASE         = 0x0C8;   // SM0 starts here, each SM = 6 regs × 4 = 0x18
    private const uint REG_INTR            = 0x128;
    private const uint REG_IRQ0_INTE       = 0x12C;
    private const uint REG_IRQ0_INTF       = 0x130;
    private const uint REG_IRQ0_INTS       = 0x134;
    private const uint REG_IRQ1_INTE       = 0x138;
    private const uint REG_IRQ1_INTF       = 0x13C;
    private const uint REG_IRQ1_INTS       = 0x140;

    private const uint SM_STRIDE = 0x18;  // 6 registers × 4 bytes
    private const uint SM_OFF_CLKDIV    = 0x00;
    private const uint SM_OFF_EXECCTRL  = 0x04;
    private const uint SM_OFF_SHIFTCTRL = 0x08;
    private const uint SM_OFF_ADDR      = 0x0C;
    private const uint SM_OFF_INSTR     = 0x10;
    private const uint SM_OFF_PINCTRL   = 0x14;

    // PIO instruction opcodes (bits [15:13])
    private const int OP_JMP  = 0;
    private const int OP_WAIT = 1;
    private const int OP_IN   = 2;
    private const int OP_OUT  = 3;
    private const int OP_PUSH_PULL = 4;
    private const int OP_MOV  = 5;
    private const int OP_IRQ  = 6;
    private const int OP_SET  = 7;

    private readonly CortexM0Plus _cpu;
    private readonly uint _blockIndex;  // 0=PIO0, 1=PIO1 (for IRQ routing)

    private readonly ushort[] _instrMem = new ushort[INSTR_COUNT];
    private readonly PioStateMachine[] _sm;

    private uint _irq;       // 8-bit IRQ flags
    private uint _fdebug;    // TXOVER/RXUNDER/TXSTALL/RXSTALL per SM
    private uint _irq0Inte;
    private uint _irq0Intf;
    private uint _irq1Inte;
    private uint _irq1Intf;

    // IRQ-state caching: only re-poke the NVIC when the computed IRQ lines actually change, or while
    // the target core sleeps (so the wake heartbeat survives). Removes the per-tick NVIC churn that
    // otherwise dominates fine-grained stepping.
    private bool _lastIrq0, _lastIrq1, _irqStateKnown;
    /// <summary>Returns whether the core that fields PIO interrupts is currently sleeping (WFE/WFI). A
    /// busy core re-checks interrupts on its own, so the per-tick recompute is only needed while it sleeps.</summary>
    public Func<bool>? CoreWaiting;

    /// <summary>Raised when a state machine frees a TX FIFO slot (explicit PULL or autopull), so a
    /// DREQ-paced DMA feeding that SM can re-arm. Mirrors <see cref="OnRxPush"/> for the RX direction.</summary>
    public Action<int>? OnTxConsumed { get; set; }
    /// <summary>Raised when a state machine pushes a word into the RX FIFO (PUSH or autopush), so a
    /// DREQ-paced DMA draining that SM can re-arm. (smIndex, value).</summary>
    public Action<int, uint>? OnRxPush { get; set; }

    /// <summary>Raised when the firmware pushes a word into a TX FIFO (a write to TXFx). (smIndex, value).
    /// Used by test probes to observe the data crossing the SM boundary.</summary>
    public Action<int, uint>? OnTxPush { get; set; }
    /// <summary>Raised when the firmware pulls a word out of an RX FIFO (a read from RXFx). (smIndex, value).
    /// Used by test probes to observe the data crossing the SM boundary.</summary>
    public Action<int, uint>? OnRxPull { get; set; }

    // FIFO helpers that notify the DREQ-resume hooks. Over-notifying is safe: ResumeDreq is
    // reentrancy-guarded and a no-op unless a channel is stalled on that exact DREQ with the source ready.
    private void RxEnqueue(PioStateMachine sm, uint value) { sm.RxFifo.Enqueue(value); OnRxPush?.Invoke(sm.SmIndex, value); }
    private uint TxDequeue(PioStateMachine sm) { var v = sm.TxFifo.Dequeue(); OnTxConsumed?.Invoke(sm.SmIndex); return v; }

    public uint Size => 0x100000;  // up to 1 MB address space per block

    /// <summary>Read current physical GPIO input levels (used by WAIT GPIO, IN PINS).</summary>
    public Func<uint>? ReadGpioIn { get; set; }
    /// <summary>Write physical GPIO output pins: (pinValue, pinMask).</summary>
    public Action<uint, uint>? WriteGpioPins { get; set; }
    /// <summary>Write physical GPIO pin directions: (dirValue, pinMask).</summary>
    public Action<uint, uint>? WriteGpioDirs { get; set; }

    /// <summary>
    /// Whether the block is held in reset. On real RP2040 a PIO powers up clock-gated: register reads
    /// return 0 and writes are no-ops until firmware clears its RESETS bit. The full machine starts the
    /// PIO in reset and clears this when RESETS releases the block; isolated unit tests leave it false.
    /// </summary>
    public bool InReset;

    public PioPeripheral(CortexM0Plus cpu, uint blockIndex)
    {
        _cpu = cpu;
        _blockIndex = blockIndex;
        _sm = new PioStateMachine[SM_COUNT];
        for (var i = 0; i < SM_COUNT; i++)
        {
            _sm[i] = new PioStateMachine();
            _sm[i].SmIndex = i;
            // Default wrap: top=31, bottom=0
            _sm[i].ExecCtrl = (31u << 12);
        }
    }

    // ── ITickable ────────────────────────────────────────────────────

    public void Tick(long deltaCycles)
    {
        for (var s = 0; s < SM_COUNT; s++)
        {
            var sm = _sm[s];
            if (!sm.Enabled) continue;

            // Clock divisor: bits[31:16]=integer (0=65536), bits[15:8]=frac
            var divInt  = (int)((sm.ClkDiv >> 16) & 0xFFFF);
            var divFrac = (int)((sm.ClkDiv >> 8) & 0xFF);
            if (divInt == 0) divInt = 65536;

            var divisor = divInt * 256 + divFrac;
            sm.FracAccum += deltaCycles * 256;
            var steps = sm.FracAccum / divisor;
            sm.FracAccum %= divisor;

            for (var i = 0L; i < steps; i++)
                ExecuteStep(sm, s);
        }
        CheckInterrupts();
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        // Held in reset: the block is clock-gated, every register reads back 0.
        if (InReset)
            return 0;

        // Strip the base (top 20 bits may vary between PIO0/1)
        var off = address & 0xFFFFF;

        if (off >= REG_INSTR_MEM_BASE && off < REG_INSTR_MEM_BASE + INSTR_COUNT * 4)
            return _instrMem[(off - REG_INSTR_MEM_BASE) / 4];

        if (off >= REG_SM_BASE && off < REG_SM_BASE + SM_COUNT * SM_STRIDE)
            return ReadSmReg(off);

        if (off >= REG_TXF_BASE && off < REG_TXF_BASE + SM_COUNT * 4)
            return 0;  // TXF is write-only

        if (off >= REG_RXF_BASE && off < REG_RXF_BASE + SM_COUNT * 4)
        {
            var smIdx = (int)((off - REG_RXF_BASE) / 4);
            var sm = _sm[smIdx];
            if (!sm.RxFifo.TryDequeue(out var v))
            {
                // RX FIFO underflow: reading an empty RX FIFO. TRM §3.7 FDEBUG.RXUNDER = bits [11:8].
                _fdebug |= 1u << (8 + smIdx);
                return 0;
            }
            // Clear RXSTALL for this SM now that RX FIFO has space
            _fdebug &= ~(1u << smIdx);
            // Wake a SM that was stalled waiting for space in the RX FIFO (PUSH block / autopush).
            // rp2040js: readFIFO() → checkWait().
            if (sm.Stalled)
                CheckSmWait(sm, txEvent: false);
            OnRxPull?.Invoke(smIdx, v);
            return v;
        }

        return off switch
        {
            REG_CTRL        => BuildCtrl(),
            REG_FSTAT       => BuildFstat(),
            REG_FDEBUG      => _fdebug,
            REG_FLEVEL      => BuildFlevel(),
            REG_IRQ         => _irq,
            REG_IRQ_FORCE   => 0,
            REG_DBG_CFGINFO => (SM_COUNT << 16) | (INSTR_COUNT << 8) | 2u,
            REG_INTR        => BuildIntr(),
            REG_IRQ0_INTE   => _irq0Inte,
            REG_IRQ0_INTF   => _irq0Intf,
            REG_IRQ0_INTS   => (BuildIntr() | _irq0Intf) & _irq0Inte,
            REG_IRQ1_INTE   => _irq1Inte,
            REG_IRQ1_INTF   => _irq1Intf,
            REG_IRQ1_INTS   => (BuildIntr() | _irq1Intf) & _irq1Inte,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        // Held in reset: the block is clock-gated, register writes are silently dropped.
        if (InReset)
            return;

        var off = address & 0xFFFFF;

        if (off >= REG_INSTR_MEM_BASE && off < REG_INSTR_MEM_BASE + INSTR_COUNT * 4)
        {
            _instrMem[(off - REG_INSTR_MEM_BASE) / 4] = (ushort)value;
            return;
        }

        if (off >= REG_SM_BASE && off < REG_SM_BASE + SM_COUNT * SM_STRIDE)
        {
            WriteSmReg(off, value);
            return;
        }

        if (off >= REG_TXF_BASE && off < REG_TXF_BASE + SM_COUNT * 4)
        {
            var smIdx = (int)((off - REG_TXF_BASE) / 4);
            var sm = _sm[smIdx];
            if (sm.TxFifo.Count < sm.TxDepth)
            {
                sm.TxFifo.Enqueue(value);
                OnTxPush?.Invoke(smIdx, value);
                // Clear TXSTALL for this SM now that TX FIFO has data
                _fdebug &= ~(1u << (24 + smIdx));
                // Wake a SM that was stalled waiting for data in the TX FIFO (PULL block / autopull).
                // rp2040js: writeFIFO() → checkWait().
                if (sm.Stalled)
                    CheckSmWait(sm, txEvent: true);
            }
            else
            {
                _fdebug |= 1u << (16 + smIdx);  // TXOVER bits [19:16] (TRM §3.7)
            }
            return;
        }

        switch (off)
        {
            case REG_CTRL:      WriteCtrl(value); break;
            case REG_FDEBUG:    _fdebug &= ~value; break;  // write 1 to clear
            case REG_IRQ:       _irq &= ~value; IrqUpdated(); break;       // write 1 to clear
            case REG_IRQ_FORCE: _irq |= value & 0xFF; IrqUpdated(); break;
            case REG_IRQ0_INTE: _irq0Inte = value & 0xFFF; CheckInterrupts(); break;
            case REG_IRQ0_INTF: _irq0Intf = value & 0xFFF; CheckInterrupts(); break;
            case REG_IRQ1_INTE: _irq1Inte = value & 0xFFF; CheckInterrupts(); break;
            case REG_IRQ1_INTF: _irq1Intf = value & 0xFFF; CheckInterrupts(); break;
        }
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 2) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Public helpers ───────────────────────────────────────────────

    /// <summary>Read the current output pins for state machine <paramref name="smIndex"/>.</summary>
    public uint GetPins(int smIndex) => _sm[smIndex].GpioPins;

    /// <summary>Returns true if RX FIFO of <paramref name="smIndex"/> has data.</summary>
    public bool RxFifoEmpty(int smIndex) => _sm[smIndex].RxFifo.Count == 0;
    /// <summary>DREQ source for DMA TX: true when TX FIFO has space to accept data.</summary>
    public bool TxFifoNotFull(int smIndex) => _sm[smIndex].TxFifo.Count < _sm[smIndex].TxDepth;

    /// <summary>Read-only snapshot of one state machine's observable state, for tooling/UIs
    /// (a playground PIO view). Purely a getter — does not affect emulation.</summary>
    public readonly record struct PioSmSnapshot(
        bool Enabled, uint Pc, uint ClkDiv, uint PinCtrl, uint Pins, int TxLevel, int RxLevel,
        uint X, uint Y, uint Osr, uint OsrCount, uint Isr, uint IsrCount, uint ExecCtrl);

    /// <summary>Number of state machines in this PIO block (4).</summary>
    public int SmCount => _sm.Length;

    /// <summary>This PIO block's index (0=PIO0, 1=PIO1).</summary>
    public uint BlockIndex => _blockIndex;

    /// <summary>Observable state of state machine <paramref name="smIndex"/> (0-3), read-only.</summary>
    public PioSmSnapshot GetSmState(int smIndex)
    {
        var s = _sm[smIndex];
        return new PioSmSnapshot(s.Enabled, s.PC, s.ClkDiv, s.PinCtrl, s.GpioPins, s.TxFifo.Count, s.RxFifo.Count,
            s.X, s.Y, s.OSR, s.OsrCount, s.ISR, s.IsrCount, s.ExecCtrl);
    }

    /// <summary>Copy of this block's 32-word instruction memory (for a disassembly view). Read-only.</summary>
    public ushort[] GetProgram() => (ushort[])_instrMem.Clone();

    /// <summary>Inject a value directly into the RX FIFO of <paramref name="smIndex"/>.</summary>
    public void InjectRxData(int smIndex, uint value)
    {
        var sm = _sm[smIndex];
        if (sm.RxFifo.Count < sm.RxDepth)
        {
            sm.RxFifo.Enqueue(value);
            OnRxPush?.Invoke(smIndex, value);
        }
    }

    // ── Private: stall wake-up ───────────────────────────────────────

    /// <summary>
    /// Immediately re-evaluate the stall of a SM after a FIFO event, so it can resume within the
    /// same CPU step instead of waiting for the next <see cref="Tick"/>. Mirrors rp2040js
    /// <c>StateMachine.checkWait()</c>.
    /// <para><paramref name="txEvent"/> = true after a TXF write (may unblock a PULL or an
    /// autopull-stalled OUT); false after an RXF read (may unblock a PUSH or a deferred autopush).
    /// The two FIFO events are handled separately: freeing RX space must not disturb a TX-waiter,
    /// and vice-versa.</para>
    /// </summary>
    private void CheckSmWait(PioStateMachine sm, bool txEvent)
    {
        if (txEvent)
        {
            if (sm.TxFifo.Count == 0) return;
            var instr = _instrMem[sm.PC & 0x1F];
            if (((instr >> 13) & 7) == OP_PUSH_PULL)
            {
                // Blocking PULL: its sole effect is to load the OSR — do it and step past it.
                sm.OSR = TxDequeue(sm);
                sm.OsrCount = 32;
                sm.Stalled = false;
                AdvanceSmPc(sm);
            }
            else
            {
                // Autopull stall on an OUT (or MOV): the instruction has not yet produced its
                // result. Just clear the stall so it re-executes and refills the OSR itself —
                // advancing the PC here would skip the OUT entirely.
                sm.Stalled = false;
            }
        }
        else
        {
            if (sm.RxFifo.Count >= sm.RxDepth) return;
            if (sm.AutopushPending)
            {
                // Deferred autopush: the ISR already holds the captured data; flush it and
                // step past the IN that stalled.
                RxEnqueue(sm, sm.ISR);
                sm.ISR = 0; sm.IsrCount = 0;
                sm.AutopushPending = false;
                sm.Stalled = false;
                AdvanceSmPc(sm);
                return;
            }
            var instr = _instrMem[sm.PC & 0x1F];
            if (((instr >> 13) & 7) == OP_PUSH_PULL && (instr & 0x80) == 0)
            {
                // Blocking PUSH: flush the ISR and step past it.
                RxEnqueue(sm, sm.ISR);
                sm.ISR = 0; sm.IsrCount = 0;
                sm.Stalled = false;
                AdvanceSmPc(sm);
            }
        }
    }

    /// <summary>
    /// Re-check IRQ flag waits across all SMs after an IRQ flag change.
    /// Called after IRQ register writes. Mirrors rp2040js <c>RPPIO.irqUpdated()</c>.
    /// </summary>
    private void IrqUpdated()
    {
        for (var i = 0; i < SM_COUNT; i++)
        {
            var sm = _sm[i];
            if (!sm.Stalled) continue;
            // Check if this SM is stalled waiting for an IRQ flag to clear (IRQ WAIT instruction).
            // The exact IRQ index being waited on is not stored separately; we re-evaluate by
            // rescanning. In practice, very few SMs stall on IRQ simultaneously.
            // A simple approach: let the next Tick() re-evaluate, which is safe because IRQ waits
            // re-check every cycle. For immediate wake (matching rp2040js), we would need to store
            // the wait type and index in PioStateMachine — deferred for a future improvement.
        }
        CheckInterrupts();
    }

    private void AdvanceSmPc(PioStateMachine sm)
    {
        if (!sm.PcJumped)
        {
            sm.PC++;
            if (sm.PC > sm.WrapTop)
                sm.PC = sm.WrapBottom;
        }
        sm.PcJumped = false;
    }

    private uint BuildCtrl()
    {
        uint ctrl = 0;
        for (var i = 0; i < SM_COUNT; i++)
            if (_sm[i].Enabled)
                ctrl |= 1u << i;
        return ctrl;
    }

    private void WriteCtrl(uint value)
    {
        // Bits [3:0]: SM_ENABLE
        for (var i = 0; i < SM_COUNT; i++)
            _sm[i].Enabled = (value & (1u << i)) != 0;

        // Bits [7:4]: SM_RESTART — reset PC and shift state.
        // rp2040js restart(): inputShiftCount=0, outputShiftCount=32 (full), waiting=false.
        // TRM §3.7: RESTART clears ISR/ISR-count and OSR-count to "full" state.
        for (var i = 0; i < SM_COUNT; i++)
            if ((value & (1u << (4 + i))) != 0)
            {
                _sm[i].PC = _sm[i].WrapBottom;
                _sm[i].ISR = 0; _sm[i].IsrCount = 0;
                // OSR shift-count is reset such that autopull triggers on the first OUT
                // (RP2040 datasheet §3.5.4.2.1: "After RESTART, the shift counter is set
                // to a value that triggers an autopull"). OsrCount=0 → shifts-so-far=32,
                // which always satisfies any PULL_THRESH ≤ 32.
                _sm[i].OSR = 0; _sm[i].OsrCount = 0;
                _sm[i].Stalled = false;
                _sm[i].AutopushPending = false;
                // Clear EXEC_STALLED status in EXECCTRL (bit 31)
                _sm[i].ExecCtrl &= 0x7FFFFFFFu;
            }

        // Bits [11:8]: CLKDIV_RESTART — reset fractional accumulator
        for (var i = 0; i < SM_COUNT; i++)
            if ((value & (1u << (8 + i))) != 0)
                _sm[i].FracAccum = 0;
    }

    // ── Private: FSTAT ───────────────────────────────────────────────

    private uint BuildFstat()
    {
        // RP2040 TRM §3.7 / rp2040js reference:
        // [27:24] = TXEMPTY (per SM), [19:16] = TXFULL, [11:8] = RXEMPTY, [3:0] = RXFULL
        uint result = 0;
        for (var i = 0; i < SM_COUNT; i++)
        {
            var sm = _sm[i];
            if (sm.RxFifo.Count >= sm.RxDepth) result |= 1u << i;          // RX full [3:0]
            if (sm.RxFifo.Count == 0)           result |= 1u << (8  + i);  // RX empty [11:8]
            if (sm.TxFifo.Count >= sm.TxDepth)  result |= 1u << (16 + i);  // TX full [19:16]
            if (sm.TxFifo.Count == 0)           result |= 1u << (24 + i);  // TX empty [27:24]
        }
        return result;
    }

    // ── Private: FLEVEL ──────────────────────────────────────────────

    private uint BuildFlevel()
    {
        uint result = 0;
        for (var i = 0; i < SM_COUNT; i++)
        {
            var txLevel = (uint)(_sm[i].TxFifo.Count & 0xF);
            var rxLevel = (uint)(_sm[i].RxFifo.Count & 0xF);
            result |= (txLevel | (rxLevel << 4)) << (i * 8);
        }
        return result;
    }

    // ── Private: INTR (dynamic) ──────────────────────────────────────
    // Bits [3:0]=RX not empty per SM, [7:4]=TX not full per SM, [11:8]=IRQ flags 0-3

    private uint BuildIntr()
    {
        uint intr = _irq & 0xF;  // IRQ flags 0-3 in bits [11:8] form, shifted to [11:8]
        intr <<= 8;
        for (var i = 0; i < SM_COUNT; i++)
        {
            var sm = _sm[i];
            if (sm.RxFifo.Count > 0)                  intr |= 1u << i;        // RX not empty [3:0]
            if (sm.TxFifo.Count < sm.TxDepth)          intr |= 1u << (4 + i);  // TX not full [7:4]
        }
        return intr;
    }

    // ── Private: interrupt routing to NVIC ──────────────────────────
    // PIO0_IRQ0=7, PIO0_IRQ1=8, PIO1_IRQ0=9, PIO1_IRQ1=10

    private void CheckInterrupts()
    {
        var intr = BuildIntr();
        var irq0Active = ((intr | _irq0Intf) & _irq0Inte) != 0;
        var irq1Active = ((intr | _irq1Intf) & _irq1Inte) != 0;

        // Re-poke the NVIC only when the IRQ state actually changed, or — to preserve the wake
        // heartbeat — while the target core sleeps. A busy core re-checks interrupts on its own, so
        // skipping the unchanged-state recompute removes the per-tick NVIC churn that otherwise
        // dominated fine-grained stepping, with no change in observed interrupt behaviour.
        var changed = !_irqStateKnown || irq0Active != _lastIrq0 || irq1Active != _lastIrq1;
        if (!changed && CoreWaiting != null && !CoreWaiting()) return;
        _lastIrq0 = irq0Active; _lastIrq1 = irq1Active; _irqStateKnown = true;

        // PIO0_IRQ0=7, PIO0_IRQ1=8, PIO1_IRQ0=9, PIO1_IRQ1=10
        _cpu.SetInterrupt((int)(7 + _blockIndex * 2),     irq0Active);
        _cpu.SetInterrupt((int)(7 + _blockIndex * 2 + 1), irq1Active);
    }

    // ── Private: SM register read/write ─────────────────────────────

    private uint ReadSmReg(uint off)
    {
        var smIdx = (int)((off - REG_SM_BASE) / SM_STRIDE);
        var reg   = (off - REG_SM_BASE) % SM_STRIDE;
        var sm    = _sm[smIdx];
        return reg switch
        {
            SM_OFF_CLKDIV    => sm.ClkDiv,
            // EXECCTRL bit 31 (EXEC_STALLED) is hardware read-only status; preserve it.
            SM_OFF_EXECCTRL  => sm.ExecCtrl,
            SM_OFF_SHIFTCTRL => sm.ShiftCtrl,
            SM_OFF_ADDR      => sm.PC,
            SM_OFF_INSTR     => _instrMem[sm.PC & 0x1F],
            SM_OFF_PINCTRL   => sm.PinCtrl,
            _ => 0,
        };
    }

    private void WriteSmReg(uint off, uint value)
    {
        var smIdx = (int)((off - REG_SM_BASE) / SM_STRIDE);
        var reg   = (off - REG_SM_BASE) % SM_STRIDE;
        var sm    = _sm[smIdx];
        switch (reg)
        {
            case SM_OFF_CLKDIV:    sm.ClkDiv    = value; break;
            // EXECCTRL bit 31 (EXEC_STALLED) is read-only hardware status;
            // writes must preserve it (rp2040js: execCtrl = (value & 0x7FFFFFFF) | (execCtrl & 0x80000000)).
            case SM_OFF_EXECCTRL:  sm.ExecCtrl  = (value & 0x7FFFFFFFu) | (sm.ExecCtrl & 0x80000000u); break;
            case SM_OFF_SHIFTCTRL: sm.ShiftCtrl = value; break;
            case SM_OFF_ADDR:      break; // read-only
            case SM_OFF_INSTR:
                // SM_INSTR must execute the instruction immediately (rp2040js: executeInstruction(value)),
                // not defer it. Immediate execution is required for correct PC updates visible on the next
                // SM_ADDR read (e.g. firmware writing a JMP and then reading SM_ADDR).
                ExecuteInstr(sm, (ushort)value, (int)((value >> 13) & 7));
                // Reflect stall in EXECCTRL bit 31 (EXEC_STALLED) per TRM §3.7.
                if (sm.Stalled)
                    sm.ExecCtrl |= 0x80000000u;
                else
                    sm.ExecCtrl &= 0x7FFFFFFFu;
                break;
            case SM_OFF_PINCTRL:   sm.PinCtrl   = value; break;
        }
    }

    // ── Private: instruction execution ──────────────────────────────

    private void ExecuteStep(PioStateMachine sm, int smIdx)
    {
        // Burn delay cycles
        if (sm.DelayCounter > 0)
        {
            sm.DelayCounter--;
            return;
        }

        // Complete a deferred autopush before fetching the next instruction. The IN already
        // shifted its data; we must NOT re-execute it (that would shift fresh data). Retry the
        // push, and advance past the IN once it succeeds.
        if (sm.AutopushPending)
        {
            if (sm.RxFifo.Count < sm.RxDepth)
            {
                RxEnqueue(sm, sm.ISR);
                sm.ISR = 0; sm.IsrCount = 0;
                sm.AutopushPending = false;
                sm.Stalled = false;
                sm.PC++;
                if (sm.PC > sm.WrapTop) sm.PC = sm.WrapBottom;
            }
            return;
        }

        if (sm.PC >= INSTR_COUNT) sm.PC = (uint)(sm.WrapBottom & 0x1F);
        var instr = _instrMem[sm.PC];

        var opcode = (instr >> 13) & 0x7;

        sm.PcJumped = false;

        ExecuteInstr(sm, instr, opcode);

        // Update FDEBUG stall bits (sticky — cleared by writing 1 to FDEBUG).
        // RP2040 TRM §3.7 / rp2040js reference bit layout:
        //   [27:24] = TXSTALL (SM stalled waiting to read from empty TX FIFO / autopull)
        //   [3:0]   = RXSTALL (SM stalled waiting to write to full RX FIFO / autopush)
        if (sm.Stalled && opcode == OP_PUSH_PULL)
        {
            if ((instr & 0x80) != 0)
                _fdebug |= 1u << (24 + smIdx);  // TXSTALL bits [27:24]
            else
                _fdebug |= 1u << smIdx;          // RXSTALL bits [3:0]
        }

        // Sideset, delay, and PC advance only on completing cycle (not on stall)
        if (!sm.Stalled)
        {
            // Apply sideset AFTER instruction completes (hardware fires sideset on completion)
            ApplySideset(sm, instr);
            // PINCTRL.SIDESET_COUNT includes enable bit when SIDE_EN is set → no +1 needed
            var sidesetCount = (int)sm.SidesetCount;
            var delayBits = 5 - sidesetCount;
            if (delayBits > 0)
            {
                var delay = (int)((instr >> 8) & ((1 << delayBits) - 1));
                sm.DelayCounter = delay;
            }

            if (!sm.PcJumped)
            {
                sm.PC++;
                if (sm.PC > sm.WrapTop)
                    sm.PC = sm.WrapBottom;
            }
        }
    }

    // Apply sideset bits from instruction field [12:8]
    private void ApplySideset(PioStateMachine sm, ushort instr)
    {
        var sidesetCount = (int)sm.SidesetCount;
        if (sidesetCount == 0) return;

        var field = (instr >> 8) & 0x1F;  // 5 bits: delay+sideset

        // If sideEn bit is set in EXECCTRL, MSB of the field is the enable
        // The enable is always field bit 4 (0x10) — MSB of the 5-bit field — regardless of
        // sidesetCount. PINCTRL.SIDESET_COUNT is inclusive of the enable bit per the TRM.
        var sideEn = sm.SideEn != 0;
        int sideValue;
        int pinCount;
        if (sideEn)
        {
            if ((field & 0x10) == 0) return;  // field bit 4 is the enable gate; 0 → no sideset
            sideValue = (field >> (5 - sidesetCount)) & ((1 << (sidesetCount - 1)) - 1);
            pinCount = sidesetCount - 1;       // one bit consumed by enable
        }
        else
        {
            sideValue = (field >> (5 - sidesetCount)) & ((1 << sidesetCount) - 1);
            pinCount = sidesetCount;
        }

        if (pinCount <= 0) return;

        var sideBase   = (int)sm.SidesetBase;
        var sidePinDir = sm.SidePinDir != 0;
        var pinMask    = pinCount < 32 ? ((1u << pinCount) - 1) << sideBase : 0xFFFFFFFFu;

        for (var bit = 0; bit < pinCount; bit++)
        {
            var pin = (sideBase + bit) & 0x1F;
            var v = (sideValue >> bit) & 1;
            if (sidePinDir)
                sm.GpioPinDirs = (sm.GpioPinDirs & ~(1u << pin)) | ((uint)v << pin);
            else
                sm.GpioPins = (sm.GpioPins & ~(1u << pin)) | ((uint)v << pin);
        }

        // Propagate to physical GPIO (same as SET/OUT pin operations)
        if (sidePinDir)
            WriteGpioDirs?.Invoke(sm.GpioPinDirs, pinMask);
        else
            WriteGpioPins?.Invoke(sm.GpioPins, pinMask);
    }

    private void ExecuteInstr(PioStateMachine sm, ushort instr, int opcode)
    {
        switch (opcode)
        {
            case OP_JMP:      ExecJmp(sm, instr);  break;
            case OP_WAIT:     ExecWait(sm, instr); break;
            case OP_IN:       ExecIn(sm, instr);   break;
            case OP_OUT:      ExecOut(sm, instr);  break;
            case OP_PUSH_PULL:
                if ((instr & 0x80) == 0) ExecPush(sm, instr);
                else                     ExecPull(sm, instr);
                break;
            case OP_MOV:      ExecMov(sm, instr);  break;
            case OP_IRQ:      ExecIrq(sm, instr);  break;
            case OP_SET:      ExecSet(sm, instr);  break;
        }
    }

    // JMP: bits [7:5]=condition, [4:0]=target
    private void ExecJmp(PioStateMachine sm, ushort instr)
    {
        var cond   = (instr >> 5) & 0x7;
        var target = (uint)(instr & 0x1F);
        bool taken = cond switch
        {
            0 => true,                              // always
            1 => sm.X == 0,                         // !X
            2 => sm.X-- != 0,                       // X-- (post-dec, take if was non-zero)
            3 => sm.Y == 0,                         // !Y
            4 => sm.Y-- != 0,                       // Y--
            5 => sm.X != sm.Y,                      // X!=Y
            6 => ((ReadGpioIn?.Invoke() ?? sm.GpioPins) >> (int)sm.JmpPin & 1) != 0,  // PIN (input, not output)
            7 => sm.OsrCount > 0,                   // !OSRE: jump when OSR is not empty
            _ => false,
        };
        if (taken)
        {
            sm.PC = target;
            sm.PcJumped = true;
            sm.Stalled = false;
            return;
        }
        sm.Stalled = false;
    }

    // WAIT: bits [7]=polarity, [6:5]=source, [4:0]=index
    private void ExecWait(PioStateMachine sm, ushort instr)
    {
        var polarity = (instr >> 7) & 1;
        var source   = (instr >> 5) & 3;
        var index    = (uint)(instr & 0x1F);

        // For IRQ source: bit 4 of index is the REL flag — same computation as ExecIrq
        var irqFlagIdx = (index & 0x10) != 0
            ? (int)((index & 0xC) | (((index & 3) + (uint)sm.SmIndex) & 3))
            : (int)(index & 0x7);

        bool condition = source switch
        {
            0 => (((ReadGpioIn?.Invoke() ?? sm.GpioPins) >> (int)index) & 1) == polarity,  // GPIO (absolute)
            1 => (((ReadGpioIn?.Invoke() ?? sm.GpioPins) >> (int)((index + sm.InBase) & 0x1F)) & 1) == polarity,  // PIN relative to IN_BASE
            2 => ((_irq >> irqFlagIdx) & 1) == polarity,   // IRQ flag
            _ => EmuStrict.NoteRet ("pio.wait.src-reserved", $"src={source}", true),  // 3 = reserved
        };

        sm.Stalled = !condition;
        if (condition && source == 2 && polarity == 1)
            _irq &= ~(1u << irqFlagIdx);  // clear IRQ on successful WAIT IRQ
    }

    // IN: bits [7:5]=source, [4:0]=bit count (0=32)
    private void ExecIn(PioStateMachine sm, ushort instr)
    {
        var source   = (instr >> 5) & 0x7;
        var bitCount = (int)(instr & 0x1F);
        if (bitCount == 0) bitCount = 32;

        uint data = source switch
        {
            0 => (ReadGpioIn?.Invoke() ?? sm.GpioPins) >> (int)sm.InBase,  // PINS: read from InBase
            1 => sm.X,
            2 => sm.Y,
            3 => 0,             // NULL
            6 => sm.ISR,
            7 => sm.OSR,
            _ => EmuStrict.NoteRet ("pio.in.src-reserved", $"src={source}", 0u),  // 4,5 = reserved
        };

        if (sm.IsrShiftRight)
        {
            // When bitCount=32: C# shift is mod-32 (>>32 = >>0 = no-op), so handle explicitly
            sm.ISR = bitCount == 32 ? data : (sm.ISR >> bitCount) | (data << (32 - bitCount));
        }
        else
        {
            // When bitCount=32: (1u<<32)-1 = 0 in C# (mod-32 shift), so handle explicitly
            sm.ISR = bitCount == 32 ? data : (sm.ISR << bitCount) | (data & ((1u << bitCount) - 1));
        }
        sm.IsrCount += (uint)bitCount;
        if (sm.IsrCount > 32) sm.IsrCount = 32;  // shift counter saturates at 32 (TRM §3.5.4)

        if (sm.AutopushEnabled && sm.IsrCount >= (uint)sm.AutopushThreshold)
        {
            if (sm.RxFifo.Count < sm.RxDepth)
            {
                RxEnqueue(sm, sm.ISR);
                sm.ISR = 0; sm.IsrCount = 0;
            }
            else
            {
                // RX FIFO full: stall WITHOUT discarding. The data already shifted into the ISR
                // is retained; the deferred push completes once the RX FIFO drains (TRM §3.5.4.2).
                sm.Stalled = true;
                sm.AutopushPending = true;
                _fdebug |= 1u << sm.SmIndex;  // RXSTALL [3:0]
            }
        }
    }

    // OUT: bits [7:5]=destination, [4:0]=bit count (0=32)
    private void ExecOut(PioStateMachine sm, ushort instr)
    {
        var dest     = (instr >> 5) & 0x7;
        var bitCount = (int)(instr & 0x1F);
        if (bitCount == 0) bitCount = 32;

        // Autopull is checked at the START of each OUT cycle (RP2040 datasheet §3.5.4.4):
        // when shifts-so-far ≥ PULL_THRESH, the OSR is considered "empty"; refill it from
        // TX FIFO before extracting bits, or stall if the FIFO is empty. This is what makes
        // autopull-driven OUT sequences see fresh data on every threshold-aligned step,
        // including the very first OUT after RESTART (where OSR is initialised stale).
        if (sm.AutopullEnabled && (32u - sm.OsrCount) >= (uint)sm.AutopullThreshold)
        {
            if (sm.TxFifo.Count == 0)
            {
                sm.Stalled = true;
                _fdebug |= 1u << (24 + sm.SmIndex);  // TXSTALL
                return;
            }
            sm.OSR = TxDequeue(sm);
            sm.OsrCount = 32;
        }

        uint data;
        if (sm.OsrShiftRight)
        {
            // When bitCount=32: (1u<<32)-1 = 0 and >>=32 is no-op in C# (mod-32 shift)
            data   = bitCount == 32 ? sm.OSR : (sm.OSR & ((1u << bitCount) - 1));
            sm.OSR = bitCount == 32 ? 0u     : (sm.OSR >> bitCount);
        }
        else
        {
            // When bitCount=32: <<32 is no-op in C# (mod-32 shift)
            data   = sm.OSR >> (32 - bitCount);  // bitCount=32 → >>0 = full OSR ✓
            sm.OSR = bitCount == 32 ? 0u : (sm.OSR << bitCount);
        }
        unchecked { sm.OsrCount -= (uint)bitCount; }

        switch (dest)
        {
            case 0: {  // PINS: write bitCount pins at OutBase
                var outBase = (int)sm.OutBase;
                var pinMask = bitCount < 32 ? ((1u << bitCount) - 1) << outBase : 0xFFFFFFFFu;
                var pinValue = (data & (bitCount < 32 ? (1u << bitCount) - 1 : 0xFFFFFFFFu)) << outBase;
                sm.GpioPins = (sm.GpioPins & ~pinMask) | pinValue;
                WriteGpioPins?.Invoke(pinValue, pinMask);
                break;
            }
            case 1: sm.X = data; break;
            case 2: sm.Y = data; break;
            case 3: break;  // NULL
            case 4: {  // PINDIRS: write bitCount dirs at OutBase
                var outBase = (int)sm.OutBase;
                var pinMask = bitCount < 32 ? ((1u << bitCount) - 1) << outBase : 0xFFFFFFFFu;
                var pinValue = (data & (bitCount < 32 ? (1u << bitCount) - 1 : 0xFFFFFFFFu)) << outBase;
                sm.GpioPinDirs = (sm.GpioPinDirs & ~pinMask) | pinValue;
                WriteGpioDirs?.Invoke(pinValue, pinMask);
                break;
            }
            case 5: sm.PC = data & 0x1F; sm.PcJumped = true; sm.Stalled = false; return;  // PC
            case 6: sm.ISR = data; sm.IsrCount = (uint)bitCount; break;
            case 7: ExecuteInstr(sm, (ushort)data, (int)((data >> 13) & 7)); return;  // EXEC
        }

        // Autopull is checked at the START of the next OUT (above), so no post-OUT pull
        // is needed here. A non-blocking post-pull would also subtly change semantics by
        // refilling on threshold-exact extractions even when no further OUT is pending.
    }

    // PUSH: bits [6]=IfFull, [5]=Block
    private void ExecPush(PioStateMachine sm, ushort instr)
    {
        var ifFull = (instr & 0x40) != 0;
        var block  = (instr & 0x20) != 0;

        if (ifFull && sm.IsrCount < (uint)sm.AutopushThreshold)
        {
            sm.Stalled = false;
            return;
        }

        DoPush(sm, block);
    }

    private void DoPush(PioStateMachine sm, bool block)
    {
        if (sm.RxFifo.Count >= sm.RxDepth)
        {
            sm.Stalled = block;
            // NOBLOCK: data is discarded but ISR must still be cleared (datasheet §3.5.4.2)
            if (!block) { sm.ISR = 0; sm.IsrCount = 0; }
            return;
        }
        RxEnqueue(sm, sm.ISR);
        sm.ISR = 0;
        sm.IsrCount = 0;
        sm.Stalled = false;
    }

    // PULL: bits [6]=IfEmpty, [5]=Block
    private void ExecPull(PioStateMachine sm, ushort instr)
    {
        var ifEmpty = (instr & 0x40) != 0;
        var block   = (instr & 0x20) != 0;

        if (ifEmpty && sm.OsrCount > 0)
        {
            sm.Stalled = false;
            return;
        }

        DoPull(sm, block);
    }

    private void DoPull(PioStateMachine sm, bool block)
    {
        if (sm.TxFifo.Count == 0)
        {
            if (block) { sm.Stalled = true; return; }
            sm.OSR = sm.X;  // refill with X when non-blocking
        }
        else
        {
            sm.OSR = TxDequeue(sm);
        }
        sm.OsrCount = 32;
        sm.Stalled = false;
    }

    // MOV: bits [7:5]=dest, [4:3]=op, [2:0]=source
    private void ExecMov(PioStateMachine sm, ushort instr)
    {
        var dest   = (instr >> 5) & 0x7;
        var op     = (instr >> 3) & 0x3;  // 0=none, 1=invert, 2=bit-reverse, 3=reserved
        var source = instr & 0x7;

        uint data = source switch
        {
            0 => ReadGpioIn?.Invoke() ?? sm.GpioPins,  // PINS: read physical GPIO
            1 => sm.X,
            2 => sm.Y,
            3 => 0,
            5 => ComputeStatus(sm),
            6 => sm.ISR,
            7 => sm.OSR,
            _ => EmuStrict.NoteRet ("pio.mov.src-reserved", $"src={source}", 0u),  // 4 = reserved
        };

        data = op switch
        {
            1 => ~data,
            2 => BitReverse(data),
            3 => EmuStrict.NoteRet ("pio.mov.op-reserved", "op=3", data),  // 3 = reserved
            _ => data,
        };

        switch (dest)
        {
            case 0: {  // PINS: write via OutBase/OutCount
                var outBase  = (int)sm.OutBase;
                var outCount = (int)sm.OutCount;
                var pinMask  = outCount > 0 ? ((1u << outCount) - 1) << outBase : 0xFFFFFFFFu;
                var pinValue = outCount > 0 ? (data & ((1u << outCount) - 1)) << outBase : data;
                sm.GpioPins = (sm.GpioPins & ~pinMask) | pinValue;
                WriteGpioPins?.Invoke(pinValue, pinMask);
                break;
            }
            case 1: sm.X = data; break;
            case 2: sm.Y = data; break;
            case 4: ExecuteInstr(sm, (ushort)data, (int)((data >> 13) & 7)); return;  // EXEC
            case 5: sm.PC = data & 0x1F; sm.PcJumped = true; sm.Stalled = false; return;  // PC
            case 6:
                // MOV ISR: rp2040js §setMovDestination / TRM §3.4.3 —
                // "The ISR shift count is set to 0 (empty)."
                sm.ISR = data; sm.IsrCount = 0; break;
            case 7:
                // MOV OSR: rp2040js §setMovDestination / TRM §3.4.3 —
                // "The OSR shift count is set to 0 (full, i.e. 32 bits remain)."
                sm.OSR = data; sm.OsrCount = 32; break;
            default: EmuStrict.Note ("pio.mov.dest-reserved", $"dest={dest}"); break;  // 3 = reserved
        }
        sm.Stalled = false;
    }

    // IRQ: bits [6]=clear, [5]=wait, [4:0]=index (bit 4 = REL flag)
    private void ExecIrq(PioStateMachine sm, ushort instr)
    {
        var smIdx   = Array.IndexOf(_sm, sm);
        var doClear = (instr & 0x40) != 0;
        var doWait  = (instr & 0x20) != 0;
        var index   = (uint)(instr & 0x1F);
        var rel     = (index & 0x10) != 0;
        // If REL: bits[3:2] unchanged, bits[1:0] = (index + smIdx) mod 4
        // REL flag is bit 4 of index; must NOT be included in the computed flag index
        var flagIdx = rel ? (int)((index & 0xC) | (((index & 3) + (uint)smIdx) & 3))
                         : (int)(index & 0x7);

        if (doClear)
        {
            _irq &= ~(1u << flagIdx);
        }
        else
        {
            _irq |= 1u << flagIdx;
        }

        if (doWait && !doClear)
            sm.Stalled = (_irq & (1u << flagIdx)) != 0;  // wait for flag to be cleared
        else
            sm.Stalled = false;
    }

    // SET: bits [7:5]=dest, [4:0]=data
    private void ExecSet(PioStateMachine sm, ushort instr)
    {
        var dest = (instr >> 5) & 0x7;
        var data = (uint)(instr & 0x1F);

        switch (dest)
        {
            case 0: {  // PINS: SET_COUNT pins at SET_BASE
                var setBase  = (int)sm.SetBase;
                var setCount = (int)sm.SetCount;
                var pinMask  = setCount > 0 ? ((1u << setCount) - 1) << setBase : 0u;
                var pinValue = setCount > 0 ? (data & ((1u << setCount) - 1)) << setBase : 0u;
                sm.GpioPins = (sm.GpioPins & ~pinMask) | pinValue;
                WriteGpioPins?.Invoke(pinValue, pinMask);
                break;
            }
            case 1: sm.X           = data; break;
            case 2: sm.Y           = data; break;
            case 4: {  // PINDIRS: SET_COUNT dirs at SET_BASE
                var setBase  = (int)sm.SetBase;
                var setCount = (int)sm.SetCount;
                var pinMask  = setCount > 0 ? ((1u << setCount) - 1) << setBase : 0u;
                var pinValue = setCount > 0 ? (data & ((1u << setCount) - 1)) << setBase : 0u;
                sm.GpioPinDirs = (sm.GpioPinDirs & ~pinMask) | pinValue;
                WriteGpioDirs?.Invoke(pinValue, pinMask);
                break;
            }
        }
        sm.Stalled = false;
    }

    private static uint BitReverse(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }

    // STATUS source for MOV: 0xFFFFFFFF if FIFO count < STATUS_N, else 0
    private static uint ComputeStatus(PioStateMachine sm)
    {
        var n = (int)sm.StatusN;
        var count = sm.StatusSel == 0
            ? sm.TxFifo.Count   // TX FIFO level
            : sm.RxFifo.Count;  // RX FIFO level
        return (uint)(count < n ? 0xFFFFFFFF : 0);
    }
}

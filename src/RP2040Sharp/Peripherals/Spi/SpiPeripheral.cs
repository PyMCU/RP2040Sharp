using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.PinLevel;

namespace RP2040.Peripherals.Spi;

/// <summary>
/// RP2040 SPI peripheral (PL022).
/// SPI0 base: 0x4003C000, SPI1 base: 0x40040000.
/// TX/RX FIFOs have capacity 8 each. Transfer simulation via injectable callback. The transfer
/// runs the instant SSPDR is written (RX word captured immediately), while an 8-deep TX FIFO fill
/// level tracks backpressure and drains on <see cref="Tick"/>, so a DMA channel paced by SPI*_TX
/// stalls when the FIFO fills instead of draining its whole buffer at trigger time.
/// With <see cref="EnablePinLevel"/> and SCK routed to a pad, the master instead shifts every word out on the
/// pads through a <see cref="SpiMasterEngine"/> (real 8-deep FIFOs, DSS, CPOL/CPHA, baud, MISO sampled from the
/// pad) and <see cref="OnTransfer"/> is bypassed. The engine runs on core 0's cycle counter and is brought up to
/// the cycle of every access (docs/pin-level-buses.md).
/// </summary>
public sealed class SpiPeripheral : IMemoryMappedDevice, IDeferrableTickable, ISpiMasterHost
{
    private const uint SSPCR0  = 0x000;  // Control 0: SCR, SPH, SPO, FRF, DSS
    private const uint SSPCR1  = 0x004;  // Control 1: SOD, MS, SSE, LBM
    private const uint SSPDR   = 0x008;  // Data register (FIFO)
    private const uint SSPSR   = 0x00C;  // Status
    private const uint SSPCPSR = 0x010;  // Clock prescaler
    private const uint SSPIMSC = 0x014;  // Interrupt mask set/clear
    private const uint SSPRIS  = 0x018;  // Raw interrupt status
    private const uint SSPMIS  = 0x01C;  // Masked interrupt status
    private const uint SSPICR  = 0x020;  // Interrupt clear
    private const uint SSPDMACR= 0x024;  // DMA control

    // PL022 Peripheral ID registers (read-only)
    private const uint SSPPERIPHID0 = 0xFE0;
    private const uint SSPPERIPHID1 = 0xFE4;
    private const uint SSPPERIPHID2 = 0xFE8;
    private const uint SSPPERIPHID3 = 0xFEC;
    private const uint SSPPCELLID0  = 0xFF0;
    private const uint SSPPCELLID1  = 0xFF4;
    private const uint SSPPCELLID2  = 0xFF8;
    private const uint SSPPCELLID3  = 0xFFC;

    // SSPSR bits
    private const uint SR_TFE = 1u << 0;  // TX FIFO empty
    private const uint SR_TNF = 1u << 1;  // TX FIFO not full
    private const uint SR_RNE = 1u << 2;  // RX FIFO not empty
    private const uint SR_RFF = 1u << 3;  // RX FIFO full
    private const uint SR_BSY = 1u << 4;  // Busy

    // SSPCR1 bits
    private const uint CR1_LBM = 1u << 0;  // Loopback mode
    private const uint CR1_SSE = 1u << 1;  // SSP enable
    private const uint CR1_MS  = 1u << 2;  // 1 = slave

    private const int FIFO_DEPTH = 8;

    private readonly CortexM0Plus? _cpu;
    private readonly int _irq;

    private uint _cr0;
    private uint _cr1;
    private uint _cpsr;
    private uint _imsc;
    private uint _ris;
    private uint _dmacr;

    private int _txLevel;   // TX FIFO fill level (drained on Tick)
    private readonly Queue<ushort> _rxFifo = new(FIFO_DEPTH);

    /// <summary>
    /// Transfer callback. Called with the TX byte/halfword; return value is the RX data.
    /// If null, RX data is 0.
    /// </summary>
    public Func<ushort, ushort>? OnTransfer;

    /// <summary>DREQ source for DMA RX: true when RX FIFO has data to read.</summary>
    public bool RxDataAvailable { get { Dreq(); return _rxFifo.Count > 0; } }

    /// <summary>DREQ source for DMA TX: true while the TX FIFO has room for another beat.</summary>
    public bool TxFifoNotFull { get { Dreq(); return _txLevel < FIFO_DEPTH && _txFifo.Count < FIFO_DEPTH; } }

    // ── Pin-level master (opt-in, see EnablePinLevel) ────────────────

    private SpiMasterEngine? _engine;
    private Func<bool>? _routed;
    private Func<long>? _clock;
    private Func<uint>? _periHz;
    private bool _advancing;
    private readonly Queue<ushort> _txFifo = new(FIFO_DEPTH);

    /// <summary>The pin-level master engine, or null when pin-level mode is off.</summary>
    public SpiMasterEngine? PinEngine => _engine;

    /// <summary>Pin-level mode: raised after anything that may have moved the engine's next event (an access, a pad
    /// input), so the machine plans again.</summary>
    internal Action? Changed;

    /// <summary>
    /// Attach a pin-level master. <paramref name="isRouted"/> reports whether firmware has routed SCK to a pad
    /// (FUNCSEL = SPI); while it returns false, or the block is a slave, transfers keep going through
    /// <see cref="OnTransfer"/>. <paramref name="clock"/> is the engine's time base (core 0's cycle at the access in
    /// flight, 8 ns at the core's fixed 125 MHz); <paramref name="periHz"/> is clk_peri as firmware programmed it, so
    /// that the SCK divider, which counts clk_peri, gives the SCK period firmware asked for. The caller registers the
    /// returned engine with IO_BANK0 as the SPI pad function.
    /// </summary>
    public SpiMasterEngine EnablePinLevel(Func<bool> isRouted, Func<long> clock, Func<uint>? periHz = null)
    {
        _routed = isRouted;
        _clock = clock;
        _periHz = periHz;
        _engine = new SpiMasterEngine(this);
        _engine.Skip(clock());
        _engine.BeforeAccess = _ => Advance();
        _engine.AfterInput = () => Changed?.Invoke();
        return _engine;
    }

    private bool PinPathActive => _engine is not null && (_cr1 & CR1_MS) == 0 && (_engine.Busy || _routed!());

    /// <summary>Bring the engine up to the clock (re-entrant calls, from inside its own step, are no-ops).</summary>
    private void Advance()
    {
        var e = _engine;
        if (e is null || _advancing) return;
        var d = _clock!() - e.Now;
        if (d <= 0) return;
        _advancing = true;
        try
        {
            if (e.Idle && _txFifo.Count == 0) e.Skip(d);
            else e.Tick(d);
        }
        finally { _advancing = false; }
    }

    private void Dreq()
    {
        if (_engine is null) return;
        Advance();
        Changed?.Invoke();
    }

    /// <summary>The machine's clock went back (watchdog reboot): restart the engine on it.</summary>
    internal void Rebase(long now) => _engine?.Rebase(now);

    /// <summary>Cycles from <paramref name="now"/> (core 0's clock) to the pin-level master's next pad edge, or
    /// long.MaxValue while it has nothing to shift. Reads no register and syncs nothing.</summary>
    internal long CyclesUntilPadEdge(long now)
    {
        var e = _engine;
        if (e is null || !e.Busy && (_txFifo.Count == 0 || !IsEnabled || !PinPathActive)) return long.MaxValue;
        long d = e.MidFrame ? e.CyclesUntilEvent : 0;
        return d - (now - e.Now);
    }

    /// <summary>Raised when a word lands in the RX FIFO, so a DMA channel stalled on the RX DREQ can re-arm.</summary>
    public Action? OnRxAvailable;

    /// <summary>Raised when the TX FIFO drains on a tick, so a DMA channel stalled on the TX DREQ can re-arm.</summary>
    public Action? OnTxSpace;

    /// <summary>Read-only snapshot of the SPI (PL022) configuration for external inspection.</summary>
    public readonly record struct SpiConfigSnapshot(
        bool Enabled, uint Cpsdvsr, int Scr, int DataBits, int Format, bool Cpol, bool Cpha, bool Loopback);
    public SpiConfigSnapshot GetConfig() => new(
        (_cr1 & CR1_SSE) != 0,
        _cpsr & 0xFF,
        (int)((_cr0 >> 8) & 0xFF),        // SCR
        (int)(_cr0 & 0xF) + 1,            // DSS -> bits
        (int)((_cr0 >> 4) & 0x3),         // FRF
        (_cr0 & (1u << 6)) != 0,          // SPO / CPOL
        (_cr0 & (1u << 7)) != 0,          // SPH / CPHA
        (_cr1 & CR1_LBM) != 0);

    // ── ITickable ─────────────────────────────────────────────────────

    /// <summary>A tick drains a TX FIFO that holds words (at the next sweep, whatever its length); else nothing. In
    /// pin-level mode, also the engine's next step, counted from the engine's own time (never behind the last tick).</summary>
    public long CyclesUntilEvent()
    {
        if (_txLevel > 0) return 0;
        var e = _engine;
        if (e is null) return long.MaxValue;
        if (e.MidFrame) return e.CyclesUntilEvent;   // queued words wait for the frame in flight
        if (_txFifo.Count != 0) return 0;             // a word the engine has yet to take
        return e.CyclesUntilEvent;
    }

    /// <summary>Only the pin-level engine counts with time, and it runs on the clock.</summary>
    public void CatchUp(long cycles) => Advance();

    public void Tick(long deltaCycles)
    {
        // The engine runs on the clock, not on the sweep's cycles (see EnablePinLevel).
        Advance();
        // The transfer already ran at write time; draining the fill level just frees the FIFO
        // space that paced a DMA burst and re-arms the channel.
        if (_txLevel > 0)
        {
            _txLevel = 0;
            OnTxSpace?.Invoke();
        }
    }

    public uint Size => 0x1000;

    public SpiPeripheral(CortexM0Plus? cpu = null, int irq = 0)
    {
        _cpu = cpu;
        _irq = irq;
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        if (_engine is null) return Read(address);
        Advance();
        var v = Read(address);
        Changed?.Invoke();
        return v;
    }

    private uint Read(uint address)
    {
        return address switch
        {
            SSPCR0          => _cr0,
            SSPCR1          => _cr1,
            SSPDR           => ReadData(),
            SSPSR           => BuildStatus(),
            SSPCPSR         => _cpsr,
            SSPIMSC         => _imsc,
            SSPRIS          => _ris,
            SSPMIS          => _ris & _imsc,
            SSPDMACR        => _dmacr,
            SSPPERIPHID0    => 0x22,
            SSPPERIPHID1    => 0x10,
            SSPPERIPHID2    => 0x04,
            SSPPERIPHID3    => 0x00,
            SSPPCELLID0     => 0x0D,
            SSPPCELLID1     => 0xF0,
            SSPPCELLID2     => 0x05,
            SSPPCELLID3     => 0xB1,
            _               => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (_engine is null) { Write(address, value); return; }
        Advance();
        Write(address, value);
        Changed?.Invoke();
    }

    private void Write(uint address, uint value)
    {
        switch (address)
        {
            case SSPCR0:  _cr0  = value; _engine?.Refresh(); break;
            case SSPCR1:
                _cr1 = value & 0xF;
                // TXRIS (bit 3): TX FIFO is always ≤ half full in synchronous simulation.
                // Set when SSP is enabled; clear when disabled.
                if (IsEnabled) _ris |=  (1u << 3);
                else           _ris &= ~(1u << 3);
                _engine?.Refresh();
                UpdateFifoRis();
                CheckInterrupts();
                break;
            case SSPDR:   WriteData((ushort)value); break;
            case SSPCPSR: _cpsr = value & 0xFE; _engine?.Refresh(); break;  // even values only, bits[7:0]
            case SSPIMSC:
                _imsc = value & 0xF;
                CheckInterrupts();
                break;
            case SSPICR:
                _ris &= ~(value & 0x3);  // clear RORIC and RTIC
                CheckInterrupts();
                break;
            case SSPDMACR: _dmacr = value & 0x3; break;
        }
    }

    /// <remarks>A sub-word write to SSPDR goes straight into the TX FIFO. The generic
    /// read-modify-write path below must never touch it: reading SSPDR pops the RX FIFO, so an
    /// 8/16-bit DMA beat (the standard <c>DMA_SIZE_8</c> SPI idiom) would eat the word received by
    /// the previous beat and stall a paced RX channel forever.</remarks>
    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        if (aligned == SSPDR) { WriteDataAccess(value); return; }
        var shift = (int)((address & 2) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    /// <inheritdoc cref="WriteHalfWord"/>
    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        if (aligned == SSPDR) { WriteDataAccess(value); return; }
        var shift = (int)((address & 3) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Private ──────────────────────────────────────────────────────

    private bool IsEnabled => (_cr1 & CR1_SSE) != 0;
    private bool IsLoopback => (_cr1 & CR1_LBM) != 0;

    private void WriteDataAccess(ushort txData)
    {
        if (_engine is null) { WriteData(txData); return; }
        Advance();
        WriteData(txData);
        Changed?.Invoke();
    }

    private void WriteData(ushort txData)
    {
        if (PinPathActive)
        {
            if (!IsEnabled || _txFifo.Count >= FIFO_DEPTH) return;
            _txFifo.Enqueue(txData);
            UpdateFifoRis();
            CheckInterrupts();
            return;
        }

        if (!IsEnabled || _txLevel >= FIFO_DEPTH)
            return;

        ushort rxData;
        if (IsLoopback)
        {
            // Loopback: TX data loops back into RX FIFO directly
            rxData = txData;
        }
        else
        {
            rxData = OnTransfer?.Invoke(txData) ?? 0;
        }

        if (_rxFifo.Count < FIFO_DEPTH)
            _rxFifo.Enqueue(rxData);

        // Track TX FIFO occupancy for DREQ backpressure; the word itself already went out.
        if (_txLevel < FIFO_DEPTH) _txLevel++;

        _ris |= 0x4;  // RXRIS — RX not empty
        _ris |= 0x8;  // TXRIS — TX FIFO ≤ half full (always true after immediate transfer)
        CheckInterrupts();
        OnRxAvailable?.Invoke();
    }

    private uint ReadData()
    {
        if (_rxFifo.TryDequeue(out var data))
        {
            if (_rxFifo.Count == 0)
            {
                _ris &= ~0x4u;  // clear RXRIS
                CheckInterrupts();
            }
            if (_engine is not null) { UpdateFifoRis(); CheckInterrupts(); }
            return data;
        }
        return 0;
    }

    private uint BuildStatus()
    {
        uint sr = 0;
        var tx = _txLevel + _txFifo.Count;
        if (tx == 0)                   sr |= SR_TFE;
        if (_txLevel < FIFO_DEPTH && _txFifo.Count < FIFO_DEPTH) sr |= SR_TNF;
        if (tx > 0 || _engine is { Busy: true }) sr |= SR_BSY;   // still shifting out queued words
        if (_rxFifo.Count > 0)         sr |= SR_RNE;
        if (_rxFifo.Count >= FIFO_DEPTH) sr |= SR_RFF;
        return sr;
    }

    /// <summary>Pin-level mode: RXRIS / TXRIS follow the real FIFO levels (RX at least half full, TX at most half
    /// full) instead of the transaction model's latched bits.</summary>
    private void UpdateFifoRis()
    {
        if (_engine is null || !PinPathActive) return;
        if (_rxFifo.Count >= FIFO_DEPTH / 2) _ris |= 0x4; else _ris &= ~0x4u;
        if (IsEnabled && _txFifo.Count <= FIFO_DEPTH / 2) _ris |= 0x8; else _ris &= ~0x8u;
    }

    // ── ISpiMasterHost (pin-level engine) ───────────────────────────────────────────

    bool ISpiMasterHost.Enabled => IsEnabled && (_cr1 & CR1_MS) == 0;
    bool ISpiMasterHost.HasWord => _txFifo.Count > 0;

    /// <summary>Frame format from SSPCR0 (DSS, SPO, SPH) and the SCK half period in core cycles: CPSDVSR x (1+SCR)
    /// clk_peri cycles per SCK period, scaled from clk_peri to the core's 125 MHz (rounded).</summary>
    SpiFrameFormat ISpiMasterHost.GetFormat()
    {
        var dss = (int)(_cr0 & 0xF) + 1;
        var cpsdvsr = Math.Max(_cpsr & 0xFEu, 2u);
        var scr = (_cr0 >> 8) & 0xFF;
        var period = (long)cpsdvsr * (scr + 1);
        long peri = Math.Max(_periHz?.Invoke() ?? RP2040Machine.CLK_HZ, 1u);
        var half = (period * RP2040Machine.CLK_HZ + peri) / (2 * peri);
        return new SpiFrameFormat(dss < 4 ? 8 : dss, (_cr0 & (1u << 6)) != 0, (_cr0 & (1u << 7)) != 0,
            IsLoopback, (int)Math.Max(half, 1));
    }

    bool ISpiMasterHost.TryTakeWord(out ushort word)
    {
        if (!_txFifo.TryDequeue(out word)) return false;
        UpdateFifoRis();
        CheckInterrupts();
        OnTxSpace?.Invoke();
        return true;
    }

    void ISpiMasterHost.WordDone(ushort received)
    {
        if (_rxFifo.Count < FIFO_DEPTH) _rxFifo.Enqueue(received);
        else _ris |= 0x1;                // RORRIS: receive overrun, word dropped
        UpdateFifoRis();
        CheckInterrupts();
        OnRxAvailable?.Invoke();
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (_ris & _imsc) != 0);
    }

    /// <summary>Inject a byte into the RX FIFO (simulates incoming data).</summary>
    public void InjectByte(byte value)
    {
        Advance();
        if (_rxFifo.Count < FIFO_DEPTH)
        {
            _rxFifo.Enqueue(value);
            OnRxAvailable?.Invoke();
        }
    }
}

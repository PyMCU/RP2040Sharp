using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.PinLevel;

namespace RP2040.Peripherals.Uart;

/// <summary>
/// PL011 UART peripheral (UART0 base 0x40034000, UART1 base 0x40038000).
/// For simulation: TX bytes are immediately forwarded to <see cref="OnByteTransmit"/>;
/// RX bytes come from <see cref="InjectByte"/> via a 32-byte FIFO.
/// With <see cref="EnablePinLevel"/> and the TX pad routed (or UARTCR.LBE on), bytes also queue in a real 32-deep TX
/// FIFO that a <see cref="UartEngine"/> shifts out on the pad at the programmed baud and format, and the RX pad is
/// sampled the same way (framing / parity / break flags in UARTDR and UARTRSR). <see cref="OnByteTransmit"/> still
/// fires at write time. The engine runs on core 0's cycle counter and is brought up to the cycle of every access
/// (docs/pin-level-buses.md).
/// </summary>
public sealed class UartPeripheral : IMemoryMappedDevice, IDeferrableTickable, IUartHost
{
    // PL011 register offsets (local addresses from ApbBridge, i.e., address & 0xFFF)
    private const uint UARTDR    = 0x000;
    private const uint UARTRSR   = 0x004;   // Receive Status / Error Clear
    private const uint UARTFR    = 0x018;   // Flag Register
    private const uint UARTIBRD  = 0x024;   // Integer baud-rate divisor
    private const uint UARTFBRD  = 0x028;   // Fractional baud-rate divisor
    private const uint UARTLCR_H = 0x02C;   // Line Control
    private const uint UARTCR    = 0x030;   // Control Register
    private const uint UARTIFLS  = 0x034;   // FIFO level select
    private const uint UARTIMSC  = 0x038;   // Interrupt mask set/clear
    private const uint UARTRIS   = 0x03C;   // Raw interrupt status
    private const uint UARTMIS   = 0x040;   // Masked interrupt status
    private const uint UARTICR   = 0x044;   // Interrupt clear
    private const uint UARTDMACR = 0x048;   // DMA control

    // PL011 Peripheral ID registers (read-only, return PL011 signature)
    private const uint UARTPERIPHID0 = 0xFE0;
    private const uint UARTPERIPHID1 = 0xFE4;
    private const uint UARTPERIPHID2 = 0xFE8;
    private const uint UARTPERIPHID3 = 0xFEC;
    private const uint UARTPCELLID0  = 0xFF0;
    private const uint UARTPCELLID1  = 0xFF4;
    private const uint UARTPCELLID2  = 0xFF8;
    private const uint UARTPCELLID3  = 0xFFC;

    // UARTFR bits
    private const uint FR_TXFE = 1u << 7;   // TX FIFO empty (1 = idle, buffer empty)
    private const uint FR_RXFF = 1u << 6;   // RX FIFO full
    private const uint FR_TXFF = 1u << 5;   // TX FIFO full
    private const uint FR_RXFE = 1u << 4;   // RX FIFO empty
    private const uint FR_BUSY = 1u << 3;   // UART transmitting

    private const uint INT_RX = 1u << 4;    // RXRIS
    private const uint INT_TX = 1u << 5;    // TXRIS

    private const int FIFO_DEPTH = 32;

    private readonly CortexM0Plus? _cpu;
    private readonly int _irq;

    private readonly Queue<ushort> _rxFifo = new(FIFO_DEPTH);   // data | (FE, PE, BE) << 8 from the pin-level receiver
    private uint _ibrd, _fbrd, _lcrH, _cr, _imsc, _ifls, _dmacr;
    private uint _ris;   // raw interrupt status
    private uint _rsr;   // latched receive errors (FE, PE, BE, OE): pin-level receiver only

    public uint Size => 0x1000;

    /// <summary>Called when a byte is written to UARTDR (TX).</summary>
    public Action<byte>? OnByteTransmit;

    public UartPeripheral(CortexM0Plus? cpu = null, int irq = 0)
    {
        _cpu = cpu;
        _irq = irq;
        _ifls = 0x12;  // default: TX at 1/2 full, RX at 1/2 full
    }

    /// <summary>Inject a byte into the RX FIFO (simulates remote device sending data).</summary>
    public void InjectByte(byte value)
    {
        Advance();
        if (_rxFifo.Count < FIFO_DEPTH)
        {
            _rxFifo.Enqueue(value);
            _ris |= INT_RX;   // RXRIS — RX interrupt raw
            CheckInterrupts();
            OnRxAvailable?.Invoke();
        }
        Changed?.Invoke();
    }

    /// <summary>Like <see cref="InjectByte"/>, but reports whether the byte fit: <c>false</c> when the RX
    /// FIFO was full and the byte was dropped.</summary>
    public bool TryInjectByte(byte value)
    {
        Advance();
        if (_rxFifo.Count >= FIFO_DEPTH) return false;
        InjectByte(value);
        return true;
    }

    /// <summary>DREQ source for DMA RX: true when RX FIFO has data to read.</summary>
    public bool RxDataAvailable { get { Dreq(); return _rxFifo.Count > 0; } }

    /// <summary>DREQ source for DMA TX: always ready in the transaction model; in pin-level mode, room in the TX FIFO.</summary>
    public bool TxFifoNotFull { get { Dreq(); return _txFifo.Count < FIFO_DEPTH; } }

    /// <summary>Raised when a byte lands in the RX FIFO, so a DMA channel stalled on the RX DREQ can re-arm.</summary>
    public Action? OnRxAvailable;

    /// <summary>Pin-level mode: raised when the engine takes a byte off the TX FIFO, so a DMA channel stalled on the TX
    /// DREQ can re-arm.</summary>
    public Action? OnTxSpace;

    /// <summary>Read-only snapshot of the UART line configuration for external inspection.</summary>
    public readonly record struct UartConfigSnapshot(
        bool Enabled, bool TxEnabled, bool RxEnabled, uint Ibrd, uint Fbrd,
        int DataBits, int StopBits, int Parity);   // Parity: 0=none,1=odd,2=even
    public UartConfigSnapshot GetConfig() => new(
        (_cr & 1u) != 0,                                   // UARTEN
        (_cr & (1u << 8)) != 0,                            // TXE
        (_cr & (1u << 9)) != 0,                            // RXE
        _ibrd, _fbrd,
        5 + (int)((_lcrH >> 5) & 0x3),                     // WLEN 5..8
        (_lcrH & (1u << 3)) != 0 ? 2 : 1,                  // STP2
        (_lcrH & (1u << 1)) == 0 ? 0
            : ((_lcrH & (1u << 2)) != 0 ? 2 : 1));

    // ── Pin-level UART (opt-in, see EnablePinLevel) ─────────────────

    private UartEngine? _engine;
    private Func<bool>? _txRouted;
    private Func<long>? _clock;
    private Func<uint>? _periHz;
    private bool _advancing;
    private readonly Queue<byte> _txFifo = new(FIFO_DEPTH);

    /// <summary>The pin-level engine, or null when pin-level mode is off.</summary>
    public UartEngine? PinEngine => _engine;

    /// <summary>Pin-level mode: raised after anything that may have moved the engine's next event (an access, a pad
    /// input), so the machine plans again.</summary>
    internal Action? Changed;

    /// <summary>
    /// Attach the pin-level UART. <paramref name="txRouted"/> reports whether firmware has routed TX to a pad
    /// (FUNCSEL = UART); while it returns false and loopback (UARTCR.LBE) is off, TX keeps using the transaction
    /// model. The receiver always listens (an idle, unrouted RX line never starts a frame). <paramref name="clock"/> is
    /// the engine's time base (core 0's cycle at the access in flight, 8 ns at the core's fixed 125 MHz);
    /// <paramref name="periHz"/> is clk_peri as firmware programmed it, which the baud divisor counts. The caller
    /// registers the returned engine with IO_BANK0 as the UART pad function.
    /// </summary>
    public UartEngine EnablePinLevel(Func<bool> txRouted, Func<long> clock, Func<uint>? periHz = null)
    {
        _txRouted = txRouted;
        _clock = clock;
        _periHz = periHz;
        _engine = new UartEngine(this);
        _engine.Skip(clock());
        _engine.BeforeAccess = _ => Advance();
        _engine.AfterInput = () => Changed?.Invoke();
        return _engine;
    }

    private bool Loopback => (_cr & (1u << 7)) != 0;
    private bool SendBreak => (_lcrH & 1u) != 0;

    private bool PinTxActive => _engine is not null && (_engine.TxBusy || _txFifo.Count > 0 || Loopback || _txRouted!());

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
            if (_txFifo.Count == 0 && e.Quiet(Loopback, SendBreak)) e.Skip(d);
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

    /// <summary>Cycles from <paramref name="now"/> (core 0's clock) to the pin-level transmitter's next TX pad edge, or
    /// long.MaxValue while it has nothing to send. Reads no register and syncs nothing.</summary>
    internal long CyclesUntilPadEdge(long now)
    {
        var e = _engine;
        if (e is null || !e.TxBusy && _txFifo.Count == 0) return long.MaxValue;
        return e.CyclesUntilTxStep - (now - e.Now);
    }

    // ── IDeferrableTickable ───────────────────────────────────────────

    /// <summary>Only the pin-level engine has events: its next step, or the next sweep for a byte it has yet to take.</summary>
    public long CyclesUntilEvent()
    {
        var e = _engine;
        if (e is null) return long.MaxValue;
        if (_txFifo.Count != 0 && e.CyclesUntilTxStep == 0) return 0;
        return e.CyclesUntilEvent;
    }

    public void CatchUp(long cycles) => Advance();

    /// <summary>The engine runs on the clock, not on the sweep's cycles.</summary>
    public void Tick(long deltaCycles) => Advance();

    // ── IMemoryMappedDevice ───────────────────────────────────────────

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
            UARTDR           => ReadData(),
            UARTRSR          => _rsr,        // errors only arise from the pin-level receiver
            UARTFR           => BuildFr(),
            UARTIBRD         => _ibrd,
            UARTFBRD         => _fbrd,
            UARTLCR_H        => _lcrH,
            UARTCR           => _cr,
            UARTIFLS         => _ifls,
            UARTIMSC         => _imsc,
            UARTRIS          => _ris,
            UARTMIS          => _ris & _imsc,
            UARTDMACR        => _dmacr,
            UARTPERIPHID0    => 0x11,
            UARTPERIPHID1    => 0x10,
            UARTPERIPHID2    => 0x34,
            UARTPERIPHID3    => 0x00,
            UARTPCELLID0     => 0x0D,
            UARTPCELLID1     => 0xF0,
            UARTPCELLID2     => 0x05,
            UARTPCELLID3     => 0xB1,
            _ => 0,
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
            case UARTDR:
                OnByteTransmit?.Invoke((byte)(value & 0xFF));
                if (PinTxActive)
                {
                    if (_txFifo.Count < FIFO_DEPTH) _txFifo.Enqueue((byte)value);
                    UpdateTxInterrupt();
                }
                else _ris |= INT_TX;   // TXRIS — TX interrupt (ready for more data)
                CheckInterrupts();
                break;
            case UARTRSR:
                _rsr = 0;   // write any value to clear error flags
                break;
            case UARTIBRD:  _ibrd  = value & 0xFFFF; _engine?.Refresh(); break;
            case UARTFBRD:  _fbrd  = value & 0x3F;   _engine?.Refresh(); break;
            case UARTLCR_H: _lcrH  = value & 0xFF;   _engine?.Refresh(); break;
            case UARTCR:    _cr    = value & 0xFFFF; _engine?.Refresh(); break;
            case UARTIFLS:
                _ifls = value & 0x3F;
                if (PinTxActive) { UpdateTxInterrupt(); CheckInterrupts(); }
                break;
            case UARTIMSC:
                _imsc = value & 0x7FF;
                CheckInterrupts();
                break;
            case UARTICR:
                _ris &= ~value;
                CheckInterrupts();
                break;
            case UARTDMACR: _dmacr = value & 0x7; break;
        }
    }

    /// <remarks>A sub-word write to UARTDR goes straight out. The generic read-modify-write path
    /// below must never touch it: reading UARTDR pops the RX FIFO, so an 8-bit DMA beat (the
    /// standard <c>DMA_SIZE_8</c> UART TX idiom) would swallow a received byte.</remarks>
    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        if (aligned == UARTDR) { WriteWord(UARTDR, value); return; }
        var shift = (int)((address & 2) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    /// <inheritdoc cref="WriteHalfWord"/>
    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        if (aligned == UARTDR) { WriteWord(UARTDR, value); return; }
        var shift = (int)((address & 3) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    private uint ReadData()
    {
        if (_rxFifo.Count == 0)
            return 0;
        var b = _rxFifo.Dequeue();   // data | error flags << 8 (flags only from the pin-level receiver)
        if (_rxFifo.Count == 0)
            _ris &= ~INT_RX;   // clear RXRIS when FIFO empties
        CheckInterrupts();
        return b;
    }

    private uint BuildFr()
    {
        uint fr = 0;
        if (_txFifo.Count == 0)           fr |= FR_TXFE;   // always, in the transaction model (immediate transmit)
        if (_txFifo.Count >= FIFO_DEPTH)  fr |= FR_TXFF;
        if (_txFifo.Count > 0 || _engine is { TxBusy: true }) fr |= FR_BUSY;   // still shifting out queued bytes
        if (_rxFifo.Count == 0)           fr |= FR_RXFE;
        if (_rxFifo.Count >= FIFO_DEPTH)  fr |= FR_RXFF;
        return fr;
    }

    /// <summary>TX FIFO trigger level (bytes) from TXIFLSEL (UARTIFLS bits [2:0]): TXRIS asserts at or below it.</summary>
    private int TxTriggerLevel() => (_ifls & 0x7) switch
    {
        0 => 7 * FIFO_DEPTH / 8,   // TX interrupt at 1/8 full: 7/8 of the FIFO is free
        1 => 3 * FIFO_DEPTH / 4,
        2 => FIFO_DEPTH / 2,
        3 => FIFO_DEPTH / 4,
        _ => FIFO_DEPTH / 8,
    };

    /// <summary>Pin-level TX: TXRIS follows the real FIFO level against the trigger level.</summary>
    private void UpdateTxInterrupt()
    {
        if (_txFifo.Count <= TxTriggerLevel()) _ris |= INT_TX;
        else                                    _ris &= ~INT_TX;
    }

    // ── IUartHost (pin-level engine) ────────────────────────────────────────────────

    bool IUartHost.TxEnabled => (_cr & 1u) != 0 && (_cr & (1u << 8)) != 0;
    bool IUartHost.RxEnabled => (_cr & 1u) != 0 && (_cr & (1u << 9)) != 0;
    bool IUartHost.Loopback => Loopback;
    bool IUartHost.SendBreak => SendBreak;
    bool IUartHost.TxQueued => _txFifo.Count > 0;

    /// <summary>Line format from UARTLCR_H and the bit time in core cycles: 16 x (IBRD + FBRD/64) clk_peri cycles,
    /// scaled from clk_peri to the core's 125 MHz.</summary>
    UartFrameFormat IUartHost.GetFormat()
    {
        var parity = (_lcrH & (1u << 1)) == 0 ? UartParity.None
            : (_lcrH & (1u << 7)) != 0 ? ((_lcrH & (1u << 2)) != 0 ? UartParity.Stick0 : UartParity.Stick1)
            : ((_lcrH & (1u << 2)) != 0 ? UartParity.Even : UartParity.Odd);
        double peri = Math.Max(_periHz?.Invoke() ?? RP2040Machine.CLK_HZ, 1u);
        var bit = _ibrd == 0 ? 0.0 : (_ibrd * 64.0 + _fbrd) / 4.0 * RP2040Machine.CLK_HZ / peri;
        return new UartFrameFormat(5 + (int)((_lcrH >> 5) & 0x3), (_lcrH & (1u << 3)) != 0 ? 2 : 1, parity, bit);
    }

    bool IUartHost.TryTakeTxByte(out byte value)
    {
        if (!_txFifo.TryDequeue(out value)) return false;
        UpdateTxInterrupt();
        CheckInterrupts();
        OnTxSpace?.Invoke();
        return true;
    }

    /// <summary>A frame from the RX pad. As for <see cref="InjectByte"/>, RXRIS is raised for every byte (the core
    /// does not model the RX trigger level or the receive timeout).</summary>
    void IUartHost.ByteReceived(byte value, bool framingError, bool parityError, bool breakError)
    {
        uint flags = (framingError ? 1u : 0) | (parityError ? 2u : 0) | (breakError ? 4u : 0);
        if (framingError) _ris |= 1u << 7;    // FERIS
        if (parityError)  _ris |= 1u << 8;    // PERIS
        if (breakError)   _ris |= 1u << 9;    // BERIS
        _rsr |= flags;
        if (_rxFifo.Count < FIFO_DEPTH)
        {
            _rxFifo.Enqueue((ushort)(value | (flags << 8)));
            _ris |= INT_RX;
            CheckInterrupts();
            OnRxAvailable?.Invoke();
        }
        else
        {
            _ris |= 1u << 10;                 // OERIS: FIFO full, frame dropped
            _rsr |= 8;
            CheckInterrupts();
        }
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (_ris & _imsc) != 0);
    }
}

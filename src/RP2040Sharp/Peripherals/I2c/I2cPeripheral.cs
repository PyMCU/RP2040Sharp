using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.PinLevel;

namespace RP2040.Peripherals.I2c;

/// <summary>
/// RP2040 I2C peripheral (DesignWare DW_apb_i2c).
/// I2C0 base: 0x40044000, I2C1 base: 0x40048000.
/// Transfer simulation via injectable callbacks; optionally (<see cref="EnablePinLevel"/>) the master drives SDA/SCL
/// on the GPIO pads through an <see cref="I2cMasterEngine"/> when firmware routes the I2C function to them. The
/// engine runs on core 0's cycle counter and is brought up to the cycle of every access (docs/pin-level-buses.md).
/// </summary>
public sealed class I2cPeripheral : IMemoryMappedDevice, IDeferrableTickable, II2cMasterHost
{
    private const uint IC_CON                = 0x000;
    private const uint IC_TAR                = 0x004;
    private const uint IC_SAR                = 0x008;   // Slave address
    private const uint IC_DATA_CMD           = 0x010;
    private const uint IC_SS_SCL_HCNT        = 0x014;
    private const uint IC_SS_SCL_LCNT        = 0x018;
    private const uint IC_FS_SCL_HCNT        = 0x01C;
    private const uint IC_FS_SCL_LCNT        = 0x020;
    private const uint IC_INTR_STAT          = 0x02C;
    private const uint IC_INTR_MASK          = 0x030;
    private const uint IC_RAW_INTR_STAT      = 0x034;
    private const uint IC_RX_TL              = 0x038;
    private const uint IC_TX_TL              = 0x03C;
    private const uint IC_CLR_INTR           = 0x040;
    private const uint IC_CLR_RX_UNDER       = 0x044;
    private const uint IC_CLR_RX_OVER        = 0x048;
    private const uint IC_CLR_TX_OVER        = 0x04C;
    private const uint IC_CLR_RD_REQ         = 0x050;
    private const uint IC_CLR_TX_ABRT        = 0x054;
    private const uint IC_CLR_RX_DONE        = 0x058;
    private const uint IC_CLR_ACTIVITY       = 0x05C;
    private const uint IC_CLR_STOP_DET       = 0x060;
    private const uint IC_CLR_START_DET      = 0x064;
    private const uint IC_CLR_GEN_CALL       = 0x068;
    private const uint IC_ENABLE             = 0x06C;
    private const uint IC_STATUS             = 0x070;
    private const uint IC_TXFLR              = 0x074;
    private const uint IC_RXFLR              = 0x078;
    private const uint IC_SDA_HOLD           = 0x07C;
    private const uint IC_TX_ABRT_SOURCE     = 0x080;
    private const uint IC_SLV_DATA_NACK_ONLY = 0x084;
    private const uint IC_DMA_CR             = 0x088;
    private const uint IC_DMA_TDLR           = 0x08C;
    private const uint IC_DMA_RDLR           = 0x090;
    private const uint IC_SDA_SETUP          = 0x094;
    private const uint IC_ACK_GENERAL_CALL   = 0x098;
    private const uint IC_ENABLE_STATUS      = 0x09C;
    private const uint IC_FS_SPKLEN          = 0x0A0;
    private const uint IC_CLR_RESTART_DET    = 0x0A8;
    private const uint IC_COMP_PARAM_1       = 0x0F4;
    private const uint IC_COMP_VERSION       = 0x0F8;
    private const uint IC_COMP_TYPE          = 0x0FC;

    // IC_STATUS bits
    private const uint ST_ACTIVITY  = 1u << 0;
    private const uint ST_TFNF      = 1u << 1;  // TX FIFO not full
    private const uint ST_TFE       = 1u << 2;  // TX FIFO empty
    private const uint ST_RFNE      = 1u << 3;  // RX FIFO not empty
    private const uint ST_RFF       = 1u << 4;  // RX FIFO full
    private const uint ST_MST_ACTV  = 1u << 5;  // Master FSM active

    // IC_RAW_INTR_STAT bit for a transfer abort.
    private const uint INTR_TX_ABRT = 1u << 6;

    // IC_TX_ABRT_SOURCE bits (RP2040 datasheet §4.3.13.10).
    private const uint ABRT_7B_ADDR_NOACK = 1u << 0;  // master sent 7-bit addr, got no ACK

    private const int FIFO_DEPTH = 16;

    private readonly CortexM0Plus? _cpu;
    private readonly int _irq;

    private uint _con  = 0x65;  // default: master, 7-bit, fast-mode enabled, restart enabled
    private uint _tar;
    private uint _sar  = 0x55;  // default slave address
    private uint _ssSclHcnt, _ssSclLcnt;
    private uint _fsSclHcnt = 0x06, _fsSclLcnt = 0x0D;
    private uint _intrMask = 0x8FF;
    private uint _rawIntr;
    private uint _txAbrtSource;
    private uint _rxTl;
    private uint _txTl;
    private uint _enable;
    private uint _sdaHold = 0x1;
    private uint _slvDataNackOnly;
    private uint _dmaCr;
    private uint _dmaTdlr;
    private uint _dmaRdlr;
    private uint _sdaSetup = 0x64;
    private uint _ackGeneralCall = 0x1;
    private uint _fsSpklen = 0x7;

    private readonly Queue<byte> _rxFifo = new(FIFO_DEPTH);

    /// <summary>DREQ source for DMA RX: true when the RX FIFO has data.</summary>
    public bool RxDataAvailable { get { Dreq(); return _rxFifo.Count > 0; } }

    /// <summary>DREQ source for DMA TX: the transaction model drains the TX FIFO immediately, so it always has room; in
    /// pin-level mode the FIFO is real and fills while the engine is on the wire.</summary>
    public bool TxFifoNotFull { get { Dreq(); return _txFifo.Count < FIFO_DEPTH; } }

    /// <summary>Pin-level mode: raised when the engine takes a command off the TX FIFO, so a DMA channel paced by this
    /// I2C's TX DREQ can re-arm.</summary>
    public Action? OnTxSpace;

    // ── Pin-level master (opt-in, see EnablePinLevel) ──
    // With an engine attached and SDA/SCL routed to pads, IC_DATA_CMD feeds a real 16-deep TX FIFO of commands that the
    // engine executes on the wire; the transaction callbacks below are bypassed. Without an engine, or while the pads
    // are not routed, nothing here is used and the block behaves as a transaction model.
    private I2cMasterEngine? _engine;
    private Func<bool>? _routed;
    private Func<long>? _clock;
    private Func<uint>? _icHz;
    private bool _advancing;
    private readonly Queue<I2cCommand> _txFifo = new(FIFO_DEPTH);

    /// <summary>The pin-level master engine, or null when pin-level mode is off.</summary>
    public I2cMasterEngine? PinEngine => _engine;

    /// <summary>Pin-level mode: raised after anything that may have moved the engine's next event (an access, a pad
    /// input), so the machine plans again.</summary>
    internal Action? Changed;

    /// <summary>
    /// Attach a pin-level master. <paramref name="isRouted"/> reports whether firmware has routed both SDA and SCL to
    /// pads (IO_BANK0 FUNCSEL = I2C); while it returns false the block keeps using the transaction callbacks.
    /// <paramref name="clock"/> is the engine's time base (core 0's cycle at the access in flight, 8 ns at the core's
    /// fixed 125 MHz); <paramref name="icHz"/> is the block's clock (clk_sys) as firmware programmed it, which the SCL
    /// counters and the SDA hold count. The caller registers the returned engine with IO_BANK0 as the I2C pad function.
    /// </summary>
    public I2cMasterEngine EnablePinLevel(Func<bool> isRouted, Func<long> clock, Func<uint>? icHz = null)
    {
        _routed = isRouted;
        _clock = clock;
        _icHz = icHz;
        _engine = new I2cMasterEngine(this);
        _engine.Skip(clock());
        _engine.BeforeAccess = _ => Advance();
        _engine.AfterInput = () => Changed?.Invoke();
        return _engine;
    }

    private bool PinPathActive => _engine is not null && (_con & 1) != 0 && (_engine.BusBusy || _routed!());

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

    /// <summary>Cycles from <paramref name="now"/> (core 0's clock) to the master's next step that can move SDA or SCL
    /// (0 when it will look at a queued command next), long.MaxValue while idle or waiting on SCL held low by a slave.
    /// Reads no register and syncs nothing.</summary>
    internal long CyclesUntilPadEdge(long now)
    {
        var e = _engine;
        if (e is null) return long.MaxValue;
        long d = CyclesUntilEvent();
        return d == long.MaxValue ? d : d - (now - e.Now);
    }

    // ── IDeferrableTickable ───────────────────────────────────────────

    /// <summary>Only the pin-level engine has events: its timer between bus edges, a stretched SCL seen high, or the
    /// next sweep for a command it has yet to look at (a master waiting on the host re-polls on every tick).</summary>
    public long CyclesUntilEvent()
    {
        var e = _engine;
        if (e is null) return long.MaxValue;
        // A master waiting on the host re-polls it on its next tick: due now once what it waits for is there.
        if (e.WaitingOnHost)
            return _txFifo.Count != 0 || e.WaitingForRoom && _rxFifo.Count < FIFO_DEPTH ? 0 : long.MaxValue;
        return e.CyclesUntilEvent;
    }

    public void CatchUp(long cycles) => Advance();

    /// <summary>The engine runs on the clock, not on the sweep's cycles.</summary>
    public void Tick(long deltaCycles) => Advance();

    /// <summary>Raised when a byte lands in the RX FIFO, so a DMA channel stalled on the RX DREQ can re-arm.</summary>
    public Action? OnRxAvailable;

    private bool _inSlaveTransmit;
    private readonly Queue<byte> _slaveTxFifo = new(FIFO_DEPTH);

    /// <summary>Called on each byte write: (targetAddress, data).</summary>
    /// <summary>
    /// Whether a device answers at the addressed 7-bit address. When null the bus is taken to hold a
    /// device exactly when something is listening on <see cref="OnWrite"/> / <see cref="OnRead"/>.
    /// An unanswered address NACKs, as silicon does — see <see cref="HandleDataCmd"/>.
    /// </summary>
    public Func<byte, bool>? DeviceResponds;

    public Action<byte, byte>? OnWrite;

    /// <summary>Called on each byte read request: (targetAddress) → rx byte.</summary>
    public Func<byte, byte>? OnRead;

    /// <summary>Called when the STOP bit is set in IC_DATA_CMD, signalling end of transaction.</summary>
    public Action? OnStop;

    /// <summary>Raised when firmware writes IC_SAR (slave address register). Argument is the new 7-bit address (0 = slave disabled).</summary>
    public event Action<byte>? SlaveAddressChanged;

    /// <summary>The 7-bit slave address currently written in IC_SAR.</summary>
    public byte SlaveAddress => (byte)(_sar & 0x7F);

    /// <summary>Read-only snapshot of the I2C (DW_apb_i2c) configuration for external inspection.</summary>
    public readonly record struct I2cConfigSnapshot(
        bool Enabled, bool Master, int Speed, int TargetAddr, int SlaveAddr, bool TenBitAddr);
    public I2cConfigSnapshot GetConfig() => new(
        (_enable & 1u) != 0,
        (_con & 1u) != 0,                  // MASTER_MODE
        (int)((_con >> 1) & 0x3),          // SPEED: 1=std,2=fast,3=high
        (int)(_tar & 0x3FF),
        (int)(_sar & 0x3FF),
        (_con & (1u << 4)) != 0);          // IC_10BITADDR_MASTER

    public uint Size => 0x1000;

    public I2cPeripheral(CortexM0Plus? cpu = null, int irq = 0)
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
            IC_CON                => _con,
            IC_TAR                => _tar,
            IC_SAR                => _sar,
            IC_DATA_CMD           => PopRxFifo(),
            IC_SS_SCL_HCNT        => _ssSclHcnt,
            IC_SS_SCL_LCNT        => _ssSclLcnt,
            IC_FS_SCL_HCNT        => _fsSclHcnt,
            IC_FS_SCL_LCNT        => _fsSclLcnt,
            IC_INTR_STAT          => RawIntr & _intrMask,
            IC_INTR_MASK          => _intrMask,
            IC_RAW_INTR_STAT      => RawIntr,
            IC_RX_TL              => _rxTl,
            IC_TX_TL              => _txTl,
            IC_CLR_INTR           => ClearAllInterrupts(),
            IC_CLR_RX_UNDER       => ClearBit(0),
            IC_CLR_RX_OVER        => ClearBit(1),
            IC_CLR_TX_OVER        => ClearBit(3),
            IC_CLR_RD_REQ         => ClearBit(5),
            IC_CLR_TX_ABRT        => ClearTxAbrt(),
            IC_CLR_RX_DONE        => ClearBit(7),
            IC_CLR_ACTIVITY       => ClearBit(8),
            IC_CLR_STOP_DET       => ClearBit(9),
            IC_CLR_START_DET      => ClearBit(10),
            IC_CLR_GEN_CALL       => ClearBit(11),
            IC_ENABLE             => _enable,
            IC_STATUS             => BuildStatus(),
            IC_TXFLR              => (uint)_txFifo.Count,   // 0 in the transaction model: the TX FIFO drains instantly
            IC_RXFLR              => (uint)_rxFifo.Count,
            IC_SDA_HOLD           => _sdaHold,
            IC_TX_ABRT_SOURCE     => _txAbrtSource,
            IC_SLV_DATA_NACK_ONLY => _slvDataNackOnly,
            IC_DMA_CR             => _dmaCr,
            IC_DMA_TDLR           => _dmaTdlr,
            IC_DMA_RDLR           => _dmaRdlr,
            IC_SDA_SETUP          => _sdaSetup,
            IC_ACK_GENERAL_CALL   => _ackGeneralCall,
            IC_ENABLE_STATUS      => _enable & 1,
            IC_FS_SPKLEN          => _fsSpklen,
            IC_CLR_RESTART_DET    => ClearBit(12),
            IC_COMP_PARAM_1       => 0,
            IC_COMP_VERSION       => 0x3230312A,
            IC_COMP_TYPE          => 0x44570140,
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
            case IC_CON:              _con              = value & 0x7FF; break;
            case IC_TAR:              _tar              = value & 0x3FF; break;
            case IC_SAR:
                _sar = value & 0x3FF;
                SlaveAddressChanged?.Invoke((byte)(_sar & 0x7F));
                break;
            case IC_DATA_CMD:         HandleDataCmd(value); break;
            case IC_SS_SCL_HCNT:      _ssSclHcnt        = value & 0xFFFF; break;
            case IC_SS_SCL_LCNT:      _ssSclLcnt        = value & 0xFFFF; break;
            case IC_FS_SCL_HCNT:      _fsSclHcnt        = value & 0xFFFF; break;
            case IC_FS_SCL_LCNT:      _fsSclLcnt        = value & 0xFFFF; break;
            case IC_INTR_MASK:
                _intrMask = value & 0xFFF;
                CheckInterrupts();
                break;
            case IC_RX_TL:
                _rxTl = value & 0xFF;
                if (PinPathActive) { UpdateRxFull(); CheckInterrupts(); }   // a new threshold can change RX_FULL now
                break;
            case IC_TX_TL:
                _txTl = value & 0xFF;
                if (_engine is not null) CheckInterrupts();   // pin-level TX_EMPTY follows the threshold
                break;
            case IC_ENABLE:
                _enable = value & 3;
                // TX_EMPTY (bit 4): TX FIFO is at or below IC_TX_TL threshold.
                // In simulation TX is always drained immediately, so set it when enabled.
                if (IsEnabled) _rawIntr |= 1u << 4;
                else           _rawIntr &= ~(1u << 4);
                CheckInterrupts();
                break;
            case IC_SDA_HOLD:         _sdaHold          = value & 0xFFFFFF; break;
            case IC_SLV_DATA_NACK_ONLY: _slvDataNackOnly = value & 1; break;
            case IC_DMA_CR:           _dmaCr            = value & 3; break;
            case IC_DMA_TDLR:         _dmaTdlr          = value & 0xF; break;
            case IC_DMA_RDLR:         _dmaRdlr          = value & 0xF; break;
            case IC_SDA_SETUP:        _sdaSetup         = value & 0xFF; break;
            case IC_ACK_GENERAL_CALL: _ackGeneralCall   = value & 1; break;
            case IC_FS_SPKLEN:        _fsSpklen         = value & 0xFF; break;
        }
    }

    /// <remarks>A sub-word write to IC_DATA_CMD goes straight through. The generic
    /// read-modify-write path below must never touch it: reading IC_DATA_CMD pops the RX FIFO, so a
    /// 16-bit DMA beat (the width the CMD/RESTART/STOP bits force) would swallow a received
    /// byte.</remarks>
    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        if (aligned == IC_DATA_CMD) { WriteWord(IC_DATA_CMD, value); return; }
        var shift = (int)((address & 2) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    /// <inheritdoc cref="WriteHalfWord"/>
    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        if (aligned == IC_DATA_CMD) { WriteWord(IC_DATA_CMD, value); return; }
        var shift = (int)((address & 3) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Private ──────────────────────────────────────────────────────

    private bool IsEnabled => (_enable & 1) != 0;

    private bool DeviceAnswers(byte addr) =>
        DeviceResponds?.Invoke(addr) ?? (OnWrite is not null || OnRead is not null);

    private void HandleDataCmd(uint value)
    {
        if (!IsEnabled) return;

        if (PinPathActive)
        {
            PinDataCmd(value);
            return;
        }

        var isRead = (value & (1u << 8)) != 0;
        var addr   = (byte)(_tar & 0x7F);

        // Nothing on the bus at this address: the address phase goes unacknowledged. Silicon raises
        // TX_ABRT, and every driver waits for either that or a completed transfer — pico-sdk's
        // i2c_write_blocking (and so MicroPython's I2C.scan()/writeto()) spins forever without it,
        // which is what hung the i2c and sh1106 examples on an empty bus.
        if (!_inSlaveTransmit && !DeviceAnswers(addr))
        {
            SignalAddressNack();
            if ((value & (1u << 9)) != 0)
            {
                OnStop?.Invoke();
                _rawIntr |= 1u << 9;   // STOP_DET still follows the aborted transfer
                CheckInterrupts();
            }
            return;
        }

        if (isRead)
        {
            var rxByte = OnRead?.Invoke(addr) ?? 0;
            if (_rxFifo.Count < FIFO_DEPTH)
            {
                _rxFifo.Enqueue(rxByte);
                OnRxAvailable?.Invoke();
            }
            _rawIntr |= 1u << 2;  // RX_FULL
            CheckInterrupts();
        }
        else if (_inSlaveTransmit)
        {
            // Firmware is responding to RD_REQ in slave-transmit mode — capture the byte.
            // The master may clock out several bytes before issuing STOP, so accumulate
            // them rather than overwriting; the mode ends on SimulateStop().
            if (_slaveTxFifo.Count < FIFO_DEPTH)
                _slaveTxFifo.Enqueue((byte)(value & 0xFF));
            _rawIntr |= 1u << 4;  // TX_EMPTY
            CheckInterrupts();
        }
        else
        {
            OnWrite?.Invoke(addr, (byte)(value & 0xFF));
            // TX FIFO is always drained instantly in simulation
            _rawIntr |= 1u << 4;  // TX_EMPTY
            CheckInterrupts();
        }

        // Signal STOP_DET when STOP bit set
        if ((value & (1u << 9)) != 0)
        {
            OnStop?.Invoke();
            _rawIntr |= 1u << 9;
            CheckInterrupts();
        }
    }

    /// <summary>Queue an IC_DATA_CMD write for the pin-level engine.</summary>
    private void PinDataCmd(uint value)
    {
        // While TX_ABRT is pending the TX FIFO is held flushed: commands are dropped until firmware clears it.
        if ((_rawIntr & INTR_TX_ABRT) != 0) return;
        if (_txFifo.Count >= FIFO_DEPTH)
        {
            _rawIntr |= INT_TX_OVER;
            CheckInterrupts();
            return;
        }
        _txFifo.Enqueue(new I2cCommand((byte)value, (value & (1u << 8)) != 0, (value & (1u << 10)) != 0, (value & (1u << 9)) != 0));
        CheckInterrupts();
    }

    private const uint INT_TX_OVER = 1u << 3, INT_TX_EMPTY = 1u << 4;

    /// <summary>IC_RAW_INTR_STAT. In pin-level mode TX_EMPTY is a live level (TX FIFO at or below IC_TX_TL and, with
    /// IC_CON.TX_EMPTY_CTRL, the last byte fully shifted out, ACK slot included) instead of the transaction model's
    /// latched bit.</summary>
    private uint RawIntr => _engine is null || !PinPathActive ? _rawIntr
        : (_rawIntr & ~INT_TX_EMPTY) | (TxEmptyLevel() ? INT_TX_EMPTY : 0);

    private bool TxEmptyLevel() =>
        IsEnabled && _txFifo.Count <= _txTl && ((_con & (1u << 8)) == 0 || !_engine!.ByteInFlight);

    /// <summary>Pin-level RX_FULL (bit 2): asserted while the RX FIFO holds more than IC_RX_TL entries.</summary>
    private void UpdateRxFull()
    {
        if (_rxFifo.Count > _rxTl) _rawIntr |= 1u << 2;
        else                       _rawIntr &= ~(1u << 2);
    }

    private uint PopRxFifo()
    {
        if (PinPathActive)
        {
            if (!_rxFifo.TryDequeue(out var b)) return 0;
            UpdateRxFull();
            CheckInterrupts();
            return b;
        }
        if (_rxFifo.TryDequeue(out var data))
        {
            if (_rxFifo.Count == 0)
            {
                _rawIntr &= ~(1u << 2);
                CheckInterrupts();
            }
            return data;
        }
        return 0;
    }

    private uint BuildStatus()
    {
        uint st = 0;
        if (_txFifo.Count == 0)         st |= ST_TFE;    // always, in the transaction model
        if (_txFifo.Count < FIFO_DEPTH) st |= ST_TFNF;
        if (_engine is { BusBusy: true }) st |= ST_ACTIVITY | ST_MST_ACTV;
        if (_rxFifo.Count > 0)  st |= ST_RFNE;
        if (_rxFifo.Count >= FIFO_DEPTH) st |= ST_RFF;
        return st;
    }

    private uint ClearAllInterrupts()
    {
        _rawIntr = 0;
        CheckInterrupts();
        return 0;
    }

    private uint ClearBit(int bit)
    {
        _rawIntr &= ~(1u << bit);
        CheckInterrupts();
        return 0;
    }

    /// <summary>Reading IC_CLR_TX_ABRT clears the TX_ABRT interrupt AND the abort source register.</summary>
    private uint ClearTxAbrt()
    {
        _rawIntr      &= ~INTR_TX_ABRT;
        _txAbrtSource  = 0;
        CheckInterrupts();
        return 0;
    }

    /// <summary>
    /// Signal that the address phase of a master transfer was not acknowledged
    /// (no device at the target address). Raises TX_ABRT (IC_RAW_INTR_STAT bit 6)
    /// and sets ABRT_7B_ADDR_NOACK in IC_TX_ABRT_SOURCE, so firmware doing an I2C
    /// bus scan or checking <c>Wire.endTransmission()</c> correctly sees the device
    /// as absent. Mirrors the DW_apb_i2c behaviour for a 7-bit address NACK.
    /// </summary>
    public void SignalAddressNack()
    {
        _txAbrtSource |= ABRT_7B_ADDR_NOACK;
        _rawIntr      |= INTR_TX_ABRT;
        // The abort flushes the TX FIFO, which raises TX_EMPTY. Drivers wait on TX_EMPTY FIRST and
        // only then read IC_TX_ABRT_SOURCE (pico-sdk's i2c_write_blocking_internal does exactly
        // that), so raising TX_ABRT alone leaves them spinning on a flag that never comes.
        _rawIntr      |= 1u << 4;   // TX_EMPTY
        CheckInterrupts();
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (RawIntr & _intrMask) != 0);
    }

    // ── II2cMasterHost (pin-level engine) ───────────────────────────────────────────

    bool II2cMasterHost.Enabled => IsEnabled && (_con & 1) != 0;
    int II2cMasterHost.TargetAddress => (int)(_tar & 0x7F);
    bool II2cMasterHost.RxHasRoom => _rxFifo.Count < FIFO_DEPTH;

    /// <summary>SCL high/low periods from the speed-mode counters: IC_SS_SCL_* when IC_CON.SPEED = standard,
    /// IC_FS_SCL_* for fast (and high, which has no HS counters here), at HCNT+1 / LCNT+1 block clock cycles with the
    /// DW minimums (6 / 8) applied. SDA hold is IC_SDA_HOLD.IC_SDA_TX_HOLD. All scaled from the block clock to the
    /// core's 125 MHz (rounded).</summary>
    void II2cMasterHost.GetTiming(out int high, out int low, out int sdaHold)
    {
        var standard = ((_con >> 1) & 3) == 1;
        var hcnt = standard ? _ssSclHcnt : _fsSclHcnt;
        var lcnt = standard ? _ssSclLcnt : _fsSclLcnt;
        long ic = Math.Max(_icHz?.Invoke() ?? RP2040Machine.CLK_HZ, 1u);
        int Scale(long n) => (int)((n * RP2040Machine.CLK_HZ + ic / 2) / ic);
        high = Math.Max(Scale(Math.Max(hcnt, 6u) + 1), 1);
        low = Math.Max(Scale(Math.Max(lcnt, 8u) + 1), 2);
        sdaHold = Scale(_sdaHold & 0xFFFF);
    }

    bool II2cMasterHost.TryPeekCommand(out I2cCommand command) => _txFifo.TryPeek(out command);

    void II2cMasterHost.CommandTaken()
    {
        _txFifo.TryDequeue(out _);
        CheckInterrupts();
        OnTxSpace?.Invoke();
    }

    void II2cMasterHost.BusStart(bool repeated)
    {
        _rawIntr |= (1u << 10) | (1u << 8);   // START_DET, ACTIVITY
        CheckInterrupts();
    }

    void II2cMasterHost.BusStop()
    {
        _rawIntr |= 1u << 9;                  // STOP_DET
        OnStop?.Invoke();
        CheckInterrupts();
    }

    void II2cMasterHost.WriteDone() => CheckInterrupts();   // TX_EMPTY (with TX_EMPTY_CTRL) follows the shifter

    void II2cMasterHost.ReadDone(byte value)
    {
        _rxFifo.Enqueue(value);
        UpdateRxFull();
        CheckInterrupts();
        OnRxAvailable?.Invoke();
    }

    /// <summary>Abort: TX_ABRT with the source bit and, as on the DW with IC_AVOID_RX_FIFO_FLUSH_ON_TX_ABRT = 0, both
    /// FIFOs flushed. The engine then issues the STOP, which raises STOP_DET.</summary>
    void II2cMasterHost.Abort(uint source)
    {
        _txAbrtSource |= source;
        _rawIntr |= INTR_TX_ABRT;
        _txFifo.Clear();
        _rxFifo.Clear();
        UpdateRxFull();
        CheckInterrupts();
    }

    /// <summary>Inject a byte into the RX FIFO (simulates a slave device responding).</summary>
    public void InjectByte(byte value)
    {
        if (_rxFifo.Count < FIFO_DEPTH)
            _rxFifo.Enqueue(value);
    }

    // ── Slave simulation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Simulate an external I2C master addressing this device as a slave.
    /// Returns true when the address matches IC_SAR.
    /// When the master wants to read (<paramref name="isWrite"/> = false), raises RD_REQ (bit 5)
    /// so firmware can respond by writing to IC_DATA_CMD.
    /// </summary>
    public bool SimulateIncomingAddress(byte addr, bool isWrite)
    {
        if ((byte)(_sar & 0x7F) != addr)
            return false;

        if (!isWrite)
        {
            // Master wants to read from us — firmware must respond via IC_DATA_CMD.
            _inSlaveTransmit = true;
            _rawIntr        |= 1u << 5;  // RD_REQ
            CheckInterrupts();
        }

        return true;
    }

    /// <summary>
    /// Simulate a data byte delivered by an external master (slave-receive mode).
    /// Places the byte in the RX FIFO and raises RX_FULL.
    /// </summary>
    public void SimulateIncomingData(byte data)
    {
        if (_rxFifo.Count < FIFO_DEPTH)
            _rxFifo.Enqueue(data);
        _rawIntr |= 1u << 2;  // RX_FULL
        CheckInterrupts();
    }

    /// <summary>
    /// Simulate a STOP condition from an external master. Ends any slave-transmit phase
    /// and raises STOP_DET (bit 9).
    /// </summary>
    public void SimulateStop()
    {
        _inSlaveTransmit = false;
        _rawIntr |= 1u << 9;  // STOP_DET
        CheckInterrupts();
    }

    /// <summary>True while firmware still owes the master bytes captured for slave-transmit.</summary>
    public bool HasSlaveTransmitByte => _slaveTxFifo.Count > 0;

    /// <summary>
    /// Dequeue the next byte firmware placed in IC_DATA_CMD for slave-transmit mode.
    /// Returns 0 when no byte is pending.
    /// </summary>
    public byte ReadSlaveTransmitByte() => _slaveTxFifo.TryDequeue(out var b) ? b : (byte)0;
}

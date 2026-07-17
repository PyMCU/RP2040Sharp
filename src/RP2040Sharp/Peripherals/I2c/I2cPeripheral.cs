using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.I2c;

/// <summary>
/// RP2040 I2C peripheral (DesignWare DW_apb_i2c).
/// I2C0 base: 0x40044000, I2C1 base: 0x40048000.
/// Transfer simulation via injectable callbacks.
/// </summary>
public sealed class I2cPeripheral : IMemoryMappedDevice
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

    private bool _inSlaveTransmit;
    private readonly Queue<byte> _slaveTxFifo = new(FIFO_DEPTH);

    /// <summary>Called on each byte write: (targetAddress, data).</summary>
    public Action<byte, byte>? OnWrite;

    /// <summary>Called on each byte read request: (targetAddress) → rx byte.</summary>
    public Func<byte, byte>? OnRead;

    /// <summary>Called when the STOP bit is set in IC_DATA_CMD, signalling end of transaction.</summary>
    public Action? OnStop;

    /// <summary>Raised when firmware writes IC_SAR (slave address register). Argument is the new 7-bit address (0 = slave disabled).</summary>
    public event Action<byte>? SlaveAddressChanged;

    /// <summary>The 7-bit slave address currently written in IC_SAR.</summary>
    public byte SlaveAddress => (byte)(_sar & 0x7F);

    public uint Size => 0x1000;

    public I2cPeripheral(CortexM0Plus? cpu = null, int irq = 0)
    {
        _cpu = cpu;
        _irq = irq;
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
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
            IC_INTR_STAT          => _rawIntr & _intrMask,
            IC_INTR_MASK          => _intrMask,
            IC_RAW_INTR_STAT      => _rawIntr,
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
            IC_TXFLR              => 0,   // TX FIFO always drained in simulation
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
            case IC_RX_TL:            _rxTl             = value & 0xFF; break;
            case IC_TX_TL:            _txTl             = value & 0xFF; break;
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

    // ── Private ──────────────────────────────────────────────────────

    private bool IsEnabled => (_enable & 1) != 0;

    private void HandleDataCmd(uint value)
    {
        if (!IsEnabled) return;

        var isRead = (value & (1u << 8)) != 0;
        var addr   = (byte)(_tar & 0x7F);

        if (isRead)
        {
            var rxByte = OnRead?.Invoke(addr) ?? 0;
            if (_rxFifo.Count < FIFO_DEPTH)
                _rxFifo.Enqueue(rxByte);
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

    private uint PopRxFifo()
    {
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
        uint st = ST_TFE | ST_TFNF;  // TX always ready in simulation
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
        // The abort flushes the TX FIFO on real hardware; our TX is already drained.
        CheckInterrupts();
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (_rawIntr & _intrMask) != 0);
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

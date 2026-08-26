using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Uart;

/// <summary>
/// PL011 UART peripheral (UART0 base 0x40034000, UART1 base 0x40038000).
/// For simulation: TX bytes are immediately forwarded to <see cref="OnByteTransmit"/>;
/// RX bytes come from <see cref="InjectByte"/> via a 32-byte FIFO.
/// </summary>
public sealed class UartPeripheral : IMemoryMappedDevice
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

    private readonly CortexM0Plus? _cpu;
    private readonly int _irq;

    private readonly Queue<byte> _rxFifo = new(32);
    private uint _ibrd, _fbrd, _lcrH, _cr, _imsc, _ifls, _dmacr;
    private uint _ris;   // raw interrupt status

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
        if (_rxFifo.Count < 32)
        {
            _rxFifo.Enqueue(value);
            _ris |= (1u << 4);   // RXRIS — RX interrupt raw
            CheckInterrupts();
        }
    }

    /// <summary>DREQ source for DMA RX: true when RX FIFO has data to read.</summary>
    public bool RxDataAvailable => _rxFifo.Count > 0;

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

    public uint ReadWord(uint address)
    {
        return address switch
        {
            UARTDR           => ReadData(),
            UARTRSR          => 0,           // no errors
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
        switch (address)
        {
            case UARTDR:
                OnByteTransmit?.Invoke((byte)(value & 0xFF));
                _ris |= (1u << 5);   // TXRIS — TX interrupt (ready for more data)
                CheckInterrupts();
                break;
            case UARTRSR:
                // Write any value to clear error flags
                break;
            case UARTIBRD:  _ibrd  = value & 0xFFFF; break;
            case UARTFBRD:  _fbrd  = value & 0x3F;   break;
            case UARTLCR_H: _lcrH  = value & 0xFF;   break;
            case UARTCR:    _cr    = value & 0xFFFF; break;
            case UARTIFLS:  _ifls  = value & 0x3F;   break;
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

    private uint ReadData()
    {
        if (_rxFifo.Count == 0)
            return 0;
        var b = _rxFifo.Dequeue();
        if (_rxFifo.Count == 0)
            _ris &= ~(1u << 4);   // clear RXRIS when FIFO empties
        CheckInterrupts();
        return b;
    }

    private uint BuildFr()
    {
        var fr = FR_TXFE;   // TX always idle (immediate transmit in sim)
        if (_rxFifo.Count == 0) fr |= FR_RXFE;
        if (_rxFifo.Count >= 32) fr |= FR_RXFF;
        return fr;
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (_ris & _imsc) != 0);
    }
}

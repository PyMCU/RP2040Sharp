using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Spi;

/// <summary>
/// RP2040 SPI peripheral (PL022).
/// SPI0 base: 0x4003C000, SPI1 base: 0x40040000.
/// TX/RX FIFOs have capacity 8 each. Transfer simulation via injectable callback.
/// </summary>
public sealed class SpiPeripheral : IMemoryMappedDevice
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

    private const int FIFO_DEPTH = 8;

    private readonly CortexM0Plus? _cpu;
    private readonly int _irq;

    private uint _cr0;
    private uint _cr1;
    private uint _cpsr;
    private uint _imsc;
    private uint _ris;
    private uint _dmacr;

    private readonly Queue<ushort> _txFifo = new(FIFO_DEPTH);
    private readonly Queue<ushort> _rxFifo = new(FIFO_DEPTH);

    /// <summary>
    /// Transfer callback. Called with the TX byte/halfword; return value is the RX data.
    /// If null, RX data is 0.
    /// </summary>
    public Func<ushort, ushort>? OnTransfer;

    /// <summary>DREQ source for DMA RX: true when RX FIFO has data to read.</summary>
    public bool RxDataAvailable => _rxFifo.Count > 0;

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

    public uint Size => 0x1000;

    public SpiPeripheral(CortexM0Plus? cpu = null, int irq = 0)
    {
        _cpu = cpu;
        _irq = irq;
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
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
        switch (address)
        {
            case SSPCR0:  _cr0  = value; break;
            case SSPCR1:
                _cr1 = value & 0xF;
                // TXRIS (bit 3): TX FIFO is always ≤ half full in synchronous simulation.
                // Set when SSP is enabled; clear when disabled.
                if (IsEnabled) _ris |=  (1u << 3);
                else           _ris &= ~(1u << 3);
                CheckInterrupts();
                break;
            case SSPDR:   WriteData((ushort)value); break;
            case SSPCPSR: _cpsr = value & 0xFE; break;  // even values only, bits[7:0]
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

    private bool IsEnabled => (_cr1 & CR1_SSE) != 0;
    private bool IsLoopback => (_cr1 & CR1_LBM) != 0;

    private void WriteData(ushort txData)
    {
        if (!IsEnabled || _txFifo.Count >= FIFO_DEPTH)
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

        _ris |= 0x4;  // RXRIS — RX not empty
        _ris |= 0x8;  // TXRIS — TX FIFO ≤ half full (always true after immediate transfer)
        CheckInterrupts();
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
            return data;
        }
        return 0;
    }

    private uint BuildStatus()
    {
        uint sr = SR_TFE;  // TX FIFO always appears empty in simulation (immediate transfer)
        if (_txFifo.Count < FIFO_DEPTH) sr |= SR_TNF;
        if (_rxFifo.Count > 0)         sr |= SR_RNE;
        if (_rxFifo.Count >= FIFO_DEPTH) sr |= SR_RFF;
        return sr;
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        _cpu.SetInterrupt(_irq, (_ris & _imsc) != 0);
    }

    /// <summary>Inject a byte into the RX FIFO (simulates incoming data).</summary>
    public void InjectByte(byte value)
    {
        if (_rxFifo.Count < FIFO_DEPTH)
            _rxFifo.Enqueue(value);
    }
}

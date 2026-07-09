using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Ssi;

/// <summary>
/// XIP SSI peripheral (0x18000000) with QSPI flash command emulation.
///
/// Handles the W25Q/W25X flash command set used by pico-sdk's
/// <c>flash_range_erase()</c> / <c>flash_range_program()</c> routines:
/// <list type="table">
///   <item><term>0x06 WRITE_ENABLE</term></item>
///   <item><term>0x04 WRITE_DISABLE</term></item>
///   <item><term>0x05 READ_STATUS_1</term><description>→ returns 0x00 (WIP=0, always idle)</description></item>
///   <item><term>0x35 READ_STATUS_2</term><description>→ returns 0x00</description></item>
///   <item><term>0x20 SECTOR_ERASE  4 KB</term><description>fills target sector with 0xFF</description></item>
///   <item><term>0x52 BLOCK_ERASE  32 KB</term><description>fills target block with 0xFF</description></item>
///   <item><term>0xD8 BLOCK_ERASE  64 KB</term><description>fills target block with 0xFF</description></item>
///   <item><term>0xC7 / 0x60 CHIP_ERASE</term><description>fills entire flash with 0xFF</description></item>
///   <item><term>0x02 PAGE_PROGRAM</term><description>writes up to 256 bytes to flash</description></item>
///   <item><term>0x03 READ_DATA</term><description>streams flash bytes into the RX FIFO</description></item>
///   <item><term>0x0B FAST_READ</term><description>same with one dummy byte after address</description></item>
/// </list>
///
/// Transaction boundaries are signalled by <see cref="IoQspi.IoQspiPeripheral"/> via
/// <see cref="OnCsAssert"/> / <see cref="OnCsDeassert"/> when the SS OUTOVER
/// field in IO_QSPI SS CTRL changes.  The <c>SER</c> register is also monitored
/// as a fallback CS source for bootrom / stage-2 code.
///
/// The peripheral always reports SR.TFNF | SR.TFE | SR.RFNE so firmware
/// polling loops complete immediately without timing simulation.
/// </summary>
public sealed unsafe class SsiPeripheral : IMemoryMappedDevice
{
    // ── Register offsets ──────────────────────────────────────────────────────
    private const uint SSI_CTRLR0         = 0x000;
    private const uint SSI_CTRLR1         = 0x004;
    private const uint SSI_SSIENR         = 0x008;
    private const uint SSI_MWCR           = 0x00C;
    private const uint SSI_SER            = 0x010;
    private const uint SSI_BAUDR          = 0x014;
    private const uint SSI_TXFTLR         = 0x018;
    private const uint SSI_RXFTLR         = 0x01C;
    private const uint SSI_TXFLR          = 0x020;
    private const uint SSI_RXFLR          = 0x024;
    private const uint SSI_SR             = 0x028;
    private const uint SSI_IMR            = 0x02C;
    private const uint SSI_ISR            = 0x030;
    private const uint SSI_RISR           = 0x034;
    private const uint SSI_ICR            = 0x048;
    private const uint SSI_IDR            = 0x058;
    private const uint SSI_VERSION_ID     = 0x05C;
    private const uint SSI_DR0            = 0x060;
    private const uint SSI_RX_SAMPLE_DLY  = 0x0F0;
    private const uint SSI_SPI_CTRL_R0    = 0x0F4;
    private const uint SSI_TXD_DRIVE_EDGE = 0x0F8;

    // ── SR bits ───────────────────────────────────────────────────────────────
    private const uint SR_TFNF = 1u << 1;  // TX FIFO not full
    private const uint SR_TFE  = 1u << 2;  // TX FIFO empty
    private const uint SR_RFNE = 1u << 3;  // RX FIFO not empty

    // ── Flash command opcodes ─────────────────────────────────────────────────
    private const byte CMD_WRITE_ENABLE  = 0x06;
    private const byte CMD_WRITE_DISABLE = 0x04;
    private const byte CMD_READ_STATUS1  = 0x05;
    private const byte CMD_READ_STATUS2  = 0x35;
    private const byte CMD_SECTOR_ERASE  = 0x20;   // 4 KB
    private const byte CMD_BLOCK_ERASE32 = 0x52;   // 32 KB
    private const byte CMD_BLOCK_ERASE64 = 0xD8;   // 64 KB
    private const byte CMD_CHIP_ERASE    = 0xC7;
    private const byte CMD_CHIP_ERASE2   = 0x60;
    private const byte CMD_PAGE_PROGRAM  = 0x02;
    private const byte CMD_READ_DATA     = 0x03;
    private const byte CMD_FAST_READ     = 0x0B;
    private const byte CMD_QUAD_IO_READ  = 0xEB;
    private const byte CMD_READ_UNIQUE_ID = 0x4B;   // 4 dummy bytes, then 8 id bytes

    // ── Registers ─────────────────────────────────────────────────────────────
    private uint _ctrlr0;
    private uint _ctrlr1;
    private uint _ssienr;
    private uint _ser;
    private uint _baudr       = 2;
    private uint _spiCtrlr0;
    private uint _txDriveEdge;
    private uint _rxSampleDly;
    private uint _imr;

    // ── Flash reference ───────────────────────────────────────────────────────
    private byte* _flashPtr;
    private uint  _flashSize;

    // ── Transaction state ─────────────────────────────────────────────────────
    private bool          _csAsserted;
    private bool          _writeEnabled;
    private readonly List<byte>  _txBuf   = new(260);   // CS-delimited; drives write/erase (ProcessTransaction)
    private readonly Queue<byte> _rxQueue = new(260);

    // RX framing, independent of the CS model: every byte the firmware clocks out while the SSI is
    // enabled produces an RX byte. A frame (for choosing the RX value) begins after a CS-deassert or
    // when a write follows a read (the command/response boundary of bootrom-style auto-CS flash access),
    // so the command byte that selects the RX response is tracked even when it is clocked before the
    // IO_QSPI/SER chip-select edge that RP2040Sharp observes.
    private readonly List<byte> _rxFrame = new(260);
    private bool _lastOpWasRead;

    // 64-bit flash unique id served for the 0x4B (Read Unique ID) command — what machine.unique_id() reads.
    // MUST differ per board instance, else two identical boards share an identity (e.g. the same MQTT
    // client-id, so a broker drops the first when the second connects). Defaults to a random-looking
    // value generated per instance (like a real chip's factory-programmed id) so this holds even when
    // the host never touches it; still overridable for tests that need a reproducible id.
    public byte[] UniqueId = CreateDefaultUniqueId();

    private static byte[] CreateDefaultUniqueId()
    {
        var id = new byte[8];
        Array.Copy(Guid.NewGuid().ToByteArray(), id, 8);
        return id;
    }

    public uint Size => 0x1000;

    // ── Wiring API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Attach the flash memory so write/erase commands are applied in-place.
    /// Must be called after construction and before any firmware runs.
    /// </summary>
    public void AttachFlash(byte* flashPtr, uint flashSize)
    {
        _flashPtr = flashPtr;
        _flashSize = flashSize;
    }

    /// <summary>
    /// Called by <see cref="IoQspi.IoQspiPeripheral"/> when the SS OUTOVER field
    /// transitions to <c>DRIVE_LOW</c> (2), asserting the active-low chip select.
    /// </summary>
    public void OnCsAssert()
    {
        if (_csAsserted) return;    // guard against double-assert
        _csAsserted = true;
        _txBuf.Clear();
        // Each CS assert starts a fresh transaction: reset the RX frame so the command byte lands at index 0
        // (bytes clocked earlier with CS deasserted, e.g. flash_exit_xip dummies, must not shift the frame).
        _rxQueue.Clear();
        _rxFrame.Clear();
        _lastOpWasRead = false;
    }

    /// <summary>
    /// Called by <see cref="IoQspi.IoQspiPeripheral"/> when SS OUTOVER leaves
    /// <c>DRIVE_LOW</c>, deasserting the chip select and completing the transaction.
    /// </summary>
    public void OnCsDeassert()
    {
        if (!_csAsserted) return;
        _csAsserted = false;
        ProcessTransaction();
        _txBuf.Clear();
        // End of transaction: flush the RX FIFO and start a fresh RX frame (the SSI's RX FIFO is
        // drained/reset at CS deassert; leaving stale bytes desynchronises the next read).
        _rxQueue.Clear();
        _rxFrame.Clear();
        _lastOpWasRead = false;
    }

    // ── IMemoryMappedDevice ───────────────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        switch (address)
        {
            case SSI_CTRLR0:         return _ctrlr0;
            case SSI_CTRLR1:         return _ctrlr1;
            case SSI_SSIENR:         return _ssienr;
            case SSI_SER:            return _ser;
            case SSI_BAUDR:          return _baudr;
            case SSI_SR:             return SR_TFE | SR_RFNE | SR_TFNF;  // always ready
            case SSI_RXFLR:          return (uint)_rxQueue.Count;
            case SSI_IDR:            return 0x51535049u;    // "QSPI" identifier
            case SSI_VERSION_ID:     return 0x3430312Au;
            case SSI_DR0:
                _lastOpWasRead = true;
                return _rxQueue.Count > 0 ? _rxQueue.Dequeue() : 0u;
            case SSI_SPI_CTRL_R0:    return _spiCtrlr0;
            case SSI_TXD_DRIVE_EDGE: return _txDriveEdge;
            case SSI_RX_SAMPLE_DLY:  return _rxSampleDly;
            default:                 return 0u;
        }
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case SSI_CTRLR0:         _ctrlr0      = value; break;
            case SSI_CTRLR1:         _ctrlr1      = value; break;
            case SSI_SSIENR:         _ssienr      = value; break;
            case SSI_BAUDR:          _baudr        = value; break;
            case SSI_IMR:            _imr          = value; break;
            case SSI_SPI_CTRL_R0:    _spiCtrlr0   = value; break;
            case SSI_TXD_DRIVE_EDGE: _txDriveEdge = value; break;
            case SSI_RX_SAMPLE_DLY:  _rxSampleDly = value; break;

            case SSI_SER:
            {
                var prev = _ser;
                _ser = value;
                // Treat SER 0→non-0 as CS assert and non-0→0 as deassert.
                // Handles bootrom / stage-2 code that drives CS via SER rather
                // than IO_QSPI flash_cs_force.
                if (value != 0 && prev == 0)
                    OnCsAssert();
                else if (value == 0 && prev != 0)
                    OnCsDeassert();
                break;
            }

            case SSI_DR0:
                // The SSI clocks one byte per DR0 write while enabled, so the RX FIFO always fills —
                // independently of the chip-select model. This lets bootrom flash routines that clock
                // dummy bytes with the flash deselected (e.g. rp_flash_exit_xip) progress past their
                // RXFLR-polling loops, instead of spinning forever on a FIFO that never advances.
                if ((_ssienr & 1) != 0)
                {
                    // A write following a read begins a new command/response frame — but ONLY when the CS is
                    // deasserted. Inside a CS-held transaction, flash_do_cmd interleaves write/read per byte
                    // (0x4B Read Unique ID: cmd + 4 dummy + 8 data), so clearing the frame on every write-after-
                    // read would drop the command byte and every multi-byte read (unique id) would return zero.
                    if (_lastOpWasRead && !_csAsserted)
                    {
                        _rxFrame.Clear();
                        _lastOpWasRead = false;
                    }
                    _rxFrame.Add((byte)value);
                    _rxQueue.Enqueue(ComputeRxByte());

                    // Write/erase application still uses the CS-delimited buffer (unchanged path).
                    if (_csAsserted)
                    {
                        _txBuf.Add((byte)value);
                    }
                }
                break;
        }
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        var shift   = (int)((address & 2) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift   = (int)((address & 3) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Transaction helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Compute the RX byte corresponding to the most-recently-added TX byte.
    /// For read commands (READ_DATA, FAST_READ, READ_STATUS) this returns real data;
    /// for all other commands firmware ignores the RX so 0x00 is returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ComputeRxByte()
    {
        if (_rxFrame.Count == 0) return 0;

        var pos = _rxFrame.Count - 1;   // 0-based index of the byte just clocked
        var cmd = _rxFrame[0];

        switch (cmd)
        {
            case CMD_READ_STATUS1:
            case CMD_READ_STATUS2:
                // All positions: 0x00 → WIP=0, always idle
                return 0x00;

            case CMD_READ_DATA when pos >= 4 && _flashPtr != null:
                // Layout: [0x03][A2][A1][A0][D0][D1]…
                return ReadFlashByte(RxAddress24() + (uint)(pos - 4));

            case CMD_FAST_READ when pos >= 5 && _flashPtr != null:
                // Layout: [0x0B][A2][A1][A0][dummy][D0][D1]…
                return ReadFlashByte(RxAddress24() + (uint)(pos - 5));

            case CMD_QUAD_IO_READ when pos >= 6 && _flashPtr != null:
                // Layout: [0xEB][A2][A1][A0][M][dummy][D0][D1]… (single-lane model of the quad read)
                return ReadFlashByte(RxAddress24() + (uint)(pos - 6));

            case CMD_READ_UNIQUE_ID when pos >= 5:
                // Layout: [0x4B][dummy][dummy][dummy][dummy][D0]…[D7] — 64-bit flash unique id.
                return (pos - 5) < 8 ? UniqueId[pos - 5] : (byte)0x00;

            default:
                return 0x00;
        }
    }

    private uint RxAddress24() =>
        ((uint)_rxFrame[1] << 16) | ((uint)_rxFrame[2] << 8) | _rxFrame[3];

    private byte ReadFlashByte(uint addr) =>
        addr < _flashSize ? _flashPtr[addr] : (byte)0xFF;

    /// <summary>
    /// Apply write/erase operations accumulated in <see cref="_txBuf"/>.
    /// Called when CS is deasserted (end of transaction).
    /// Read commands have already enqueued their RX bytes via
    /// <see cref="ComputeRxByte"/>; no additional action is needed for them here.
    /// </summary>
    private void ProcessTransaction()
    {
        if (_txBuf.Count == 0 || _flashPtr == null) return;

        var cmd = _txBuf[0];
        switch (cmd)
        {
            case CMD_WRITE_ENABLE:
                _writeEnabled = true;
                break;

            case CMD_WRITE_DISABLE:
                _writeEnabled = false;
                break;

            case CMD_SECTOR_ERASE when _writeEnabled && _txBuf.Count >= 4:
                FlashErase(GetAddress24(), 4u * 1024);
                _writeEnabled = false;
                break;

            case CMD_BLOCK_ERASE32 when _writeEnabled && _txBuf.Count >= 4:
                FlashErase(GetAddress24(), 32u * 1024);
                _writeEnabled = false;
                break;

            case CMD_BLOCK_ERASE64 when _writeEnabled && _txBuf.Count >= 4:
                FlashErase(GetAddress24(), 64u * 1024);
                _writeEnabled = false;
                break;

            case CMD_CHIP_ERASE when _writeEnabled:
            case CMD_CHIP_ERASE2 when _writeEnabled:
                FlashErase(0, _flashSize);
                _writeEnabled = false;
                break;

            case CMD_PAGE_PROGRAM when _writeEnabled && _txBuf.Count >= 4:
            {
                var baseAddr = GetAddress24();
                for (var i = 4; i < _txBuf.Count; i++)
                {
                    var offset = baseAddr + (uint)(i - 4);
                    if (offset < _flashSize)
                        _flashPtr[offset] = _txBuf[i];
                }
                _writeEnabled = false;
                break;
            }
            // READ_DATA / FAST_READ / READ_STATUS: data already enqueued via ComputeRxByte.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetAddress24() =>
        ((uint)_txBuf[1] << 16) | ((uint)_txBuf[2] << 8) | _txBuf[3];

    private void FlashErase(uint addr, uint size)
    {
        // Align the start address down to the erase-unit boundary (size must be a power of 2)
        var start = addr & ~(size - 1);
        var end   = start + size;
        if (end > _flashSize) end = _flashSize;
        Unsafe.InitBlock(_flashPtr + start, 0xFF, end - start);
    }
}

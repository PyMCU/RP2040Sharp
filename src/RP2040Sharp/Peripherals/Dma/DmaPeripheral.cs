using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Dma;

/// <summary>
/// RP2040 DMA controller (base 0x50000000, region 0x5).
/// 12 DMA channels. Transfers execute synchronously when CTRL_TRIG is written
/// with EN=1, making simulation deterministic.
/// </summary>
public sealed class DmaPeripheral : IMemoryMappedDevice
{
    private const int CHANNEL_COUNT = 12;
    private const uint CHANNEL_SIZE  = 0x40;  // 64 bytes per channel

    // Channel register offsets within each 64-byte block
    private const uint OFF_READ_ADDR   = 0x00;
    private const uint OFF_WRITE_ADDR  = 0x04;
    private const uint OFF_TRANS_COUNT = 0x08;
    private const uint OFF_CTRL_TRIG   = 0x0C;

    // System registers (above channel space)
    private const uint REG_INTR        = 0x400;
    private const uint REG_INTE0       = 0x404;
    private const uint REG_INTF0       = 0x408;
    private const uint REG_INTS0       = 0x40C;
    private const uint REG_INTE1       = 0x414;
    private const uint REG_INTF1       = 0x418;
    private const uint REG_INTS1       = 0x41C;
    private const uint REG_TIMER0      = 0x420;
    private const uint REG_TIMER1      = 0x424;
    private const uint REG_TIMER2      = 0x428;
    private const uint REG_TIMER3      = 0x42C;
    private const uint REG_MULTI_CHAN  = 0x430;
    private const uint REG_SNIFF_CTRL = 0x434;
    private const uint REG_SNIFF_DATA = 0x438;
    private const uint REG_FIFO_LEVELS = 0x440;
    private const uint REG_CHAN_ABORT  = 0x444;
    private const uint REG_N_CHANNELS  = 0x448;

    // AL1 alias offsets within channel block (+0x10): CTRL, READ, WRITE, TRANS_TRIG
    private const uint AL1_OFF = 0x10;
    // AL2 alias offsets within channel block (+0x20): CTRL, TRANS, READ, WRITE_TRIG
    private const uint AL2_OFF = 0x20;
    // AL3 alias offsets within channel block (+0x30): CTRL, WRITE, TRANS, READ_TRIG
    private const uint AL3_OFF = 0x30;

    // CTRL bit masks
    private const uint CTRL_EN         = 1u << 0;
    private const uint CTRL_BUSY       = 1u << 24;
    private const uint CTRL_AHB_ERROR  = 1u << 31;
    private const uint CTRL_DATA_SIZE  = 3u << 2;   // bits 3:2
    private const uint CTRL_INCR_READ  = 1u << 4;
    private const uint CTRL_INCR_WRITE = 1u << 5;
    private const uint CTRL_BSWAP      = 1u << 22;
    private const uint CTRL_IRQ_QUIET  = 1u << 21;
    private const uint CTRL_CHAIN_TO   = 0xFu << 11;  // bits 14:11

    private readonly BusInterconnect _bus;
    private readonly CortexM0Plus   _cpu;

    // Per-channel state
    private readonly uint[] _readAddr   = new uint[CHANNEL_COUNT];
    private readonly uint[] _writeAddr  = new uint[CHANNEL_COUNT];
    private readonly uint[] _transCount = new uint[CHANNEL_COUNT];
    private readonly uint[] _ctrl       = new uint[CHANNEL_COUNT];

    // DREQ sources: 64 DREQ lines. Null = always ready (same as PERMANENT/TREQ=63).
    // Returns true when the peripheral is ready for one data beat.
    private readonly Func<bool>?[] _dreqSources = new Func<bool>?[64];

    private const int TREQ_PERMANENT = 0x3F;

    // System registers
    private uint _intr;   // pending channel complete flags
    private uint _inte0;  // IRQ0 enable mask
    private uint _intf0;  // IRQ0 force mask
    private uint _inte1;  // IRQ1 enable mask
    private uint _intf1;  // IRQ1 force mask
    private uint _timer0, _timer1, _timer2, _timer3;
    private uint _sniffCtrl;
    private uint _sniffData;

    public uint Size => 0x1000;

    /// <summary>
    /// Register a DREQ source for the given DREQ index (0–62).
    /// The delegate returns <c>true</c> when the peripheral is ready for one beat.
    /// DREQ 63 (PERMANENT) is always ready and cannot be overridden.
    /// </summary>
    public void RegisterDreq(int dreqIndex, Func<bool> ready)
    {
        if (dreqIndex is < 0 or >= TREQ_PERMANENT)
            throw new ArgumentOutOfRangeException(nameof(dreqIndex));
        _dreqSources[dreqIndex] = ready;
    }

    private bool _inResume;

    /// <summary>
    /// Re-arm any channel that stalled waiting on the given DREQ line, now that the source peripheral
    /// has signalled readiness for another beat (e.g. a PIO RX push, or a TX slot freed by the SM). A
    /// stalled channel keeps BUSY set with beats still pending; re-running it moves as many beats as the
    /// DREQ now allows, then re-stalls if the source drains again. This is what makes a DMA↔FIFO transfer
    /// self-paced instead of draining an empty FIFO greedily at trigger time, and without it a DREQ-paced
    /// transfer (e.g. a CYW43 firmware download over the PIO gSPI) stalls forever. Reentrancy-guarded.
    /// </summary>
    public void ResumeDreq(int dreqIndex)
    {
        if (_inResume) return;
        _inResume = true;
        try
        {
            for (var ch = 0; ch < CHANNEL_COUNT; ch++)
            {
                if ((_ctrl[ch] & CTRL_BUSY) == 0 || _transCount[ch] == 0) continue;
                // TREQ_SEL is CTRL bits [20:15] on the RP2040.
                if ((int)((_ctrl[ch] >> 15) & 0x3F) == dreqIndex)
                    ExecuteChannel(ch);
            }
        }
        finally { _inResume = false; }
    }

    public DmaPeripheral(BusInterconnect bus, CortexM0Plus cpu)
    {
        _bus = bus;
        _cpu = cpu;
        // Default CHAIN_TO: each channel chains to itself (no chaining)
        for (var i = 0; i < CHANNEL_COUNT; i++)
            _ctrl[i] = (uint)i << 11;
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        if (address < CHANNEL_COUNT * CHANNEL_SIZE)
        {
            var ch = (int)(address / CHANNEL_SIZE);
            var off = address % CHANNEL_SIZE;
            return off switch
            {
                OFF_READ_ADDR   => _readAddr[ch],
                OFF_WRITE_ADDR  => _writeAddr[ch],
                OFF_TRANS_COUNT => _transCount[ch],
                OFF_CTRL_TRIG   => _ctrl[ch],
                // AL1: CTRL, READ, WRITE, TRANS (trigger)
                0x10 => _ctrl[ch],
                0x14 => _readAddr[ch],
                0x18 => _writeAddr[ch],
                0x1C => _transCount[ch],
                // AL2: CTRL, TRANS, READ, WRITE (trigger)
                0x20 => _ctrl[ch],
                0x24 => _transCount[ch],
                0x28 => _readAddr[ch],
                0x2C => _writeAddr[ch],
                // AL3: CTRL, WRITE, TRANS, READ (trigger)
                0x30 => _ctrl[ch],
                0x34 => _writeAddr[ch],
                0x38 => _transCount[ch],
                0x3C => _readAddr[ch],
                _ => 0,
            };
        }

        return address switch
        {
            REG_INTR   => _intr,
            REG_INTE0  => _inte0,
            REG_INTF0  => _intf0,
            REG_INTS0  => (_intr | _intf0) & _inte0,
            REG_INTE1  => _inte1,
            REG_INTF1  => _intf1,
            REG_INTS1  => (_intr | _intf1) & _inte1,
            REG_TIMER0 => _timer0,
            REG_TIMER1 => _timer1,
            REG_TIMER2 => _timer2,
            REG_TIMER3 => _timer3,
            REG_SNIFF_CTRL  => _sniffCtrl,
            REG_SNIFF_DATA  => _sniffData,
            REG_FIFO_LEVELS => 0,
            REG_N_CHANNELS  => CHANNEL_COUNT,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (address < CHANNEL_COUNT * CHANNEL_SIZE)
        {
            WriteChannelWord(address, value);
            return;
        }

        switch (address)
        {
            case REG_INTR:  _intr &= ~value; break;     // write 1 to clear
            case REG_INTE0: _inte0 = value & 0xFFF; break;
            case REG_INTF0: _intf0 = value & 0xFFF; break;
            case REG_INTE1: _inte1 = value & 0xFFF; break;
            case REG_INTF1: _intf1 = value & 0xFFF; break;
            case REG_TIMER0: _timer0 = value; break;
            case REG_TIMER1: _timer1 = value; break;
            case REG_TIMER2: _timer2 = value; break;
            case REG_TIMER3: _timer3 = value; break;
            case REG_SNIFF_CTRL: _sniffCtrl = value; break;
            case REG_SNIFF_DATA: _sniffData = value; break;
            case REG_CHAN_ABORT:
                // Abort in-flight channels — since transfers are synchronous they're
                // already done, so just clear BUSY on matching channels
                for (var i = 0; i < CHANNEL_COUNT; i++)
                    if ((value & (1u << i)) != 0)
                        _ctrl[i] &= ~CTRL_BUSY;
                break;
            case REG_MULTI_CHAN:
                // Trigger multiple channels simultaneously
                for (var i = 0; i < CHANNEL_COUNT; i++)
                    if ((value & (1u << i)) != 0 && (_ctrl[i] & CTRL_EN) != 0)
                        ExecuteChannel(i);
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

    // ── Private ──────────────────────────────────────────────────────

    private void WriteChannelWord(uint address, uint value)
    {
        var ch = (int)(address / CHANNEL_SIZE);
        var off = address % CHANNEL_SIZE;

        switch (off)
        {
            case OFF_READ_ADDR:
                _readAddr[ch] = value; break;
            case OFF_WRITE_ADDR:
                _writeAddr[ch] = value; break;
            case OFF_TRANS_COUNT:
                _transCount[ch] = value; break;
            case OFF_CTRL_TRIG:
                if (value == 0)
                {
                    // Null trigger: signal completion without starting a transfer
                    NullTrigger(ch);
                    break;
                }
                _ctrl[ch] = value & ~CTRL_BUSY;  // BUSY is HW-driven
                if ((value & CTRL_EN) != 0 && _transCount[ch] > 0)
                    ExecuteChannel(ch);
                break;
            // AL1: CTRL, READ, WRITE, TRANS_TRIG (last triggers)
            case 0x10: _ctrl[ch] = value & ~CTRL_BUSY; break;
            case 0x14: _readAddr[ch] = value; break;
            case 0x18: _writeAddr[ch] = value; break;
            case 0x1C:
                _transCount[ch] = value;
                if ((_ctrl[ch] & CTRL_EN) != 0 && _transCount[ch] > 0) ExecuteChannel(ch);
                else if (_transCount[ch] == 0) NullTrigger(ch);
                break;
            // AL2: CTRL, TRANS, READ, WRITE_TRIG (last triggers)
            case 0x20: _ctrl[ch] = value & ~CTRL_BUSY; break;
            case 0x24: _transCount[ch] = value; break;
            case 0x28: _readAddr[ch] = value; break;
            case 0x2C:
                _writeAddr[ch] = value;
                if ((_ctrl[ch] & CTRL_EN) != 0 && _transCount[ch] > 0) ExecuteChannel(ch);
                else if (_transCount[ch] == 0) NullTrigger(ch);
                break;
            // AL3: CTRL, WRITE, TRANS, READ_TRIG (last triggers)
            case 0x30: _ctrl[ch] = value & ~CTRL_BUSY; break;
            case 0x34: _writeAddr[ch] = value; break;
            case 0x38: _transCount[ch] = value; break;
            case 0x3C:
                _readAddr[ch] = value;
                if ((_ctrl[ch] & CTRL_EN) != 0 && _transCount[ch] > 0) ExecuteChannel(ch);
                else if (_transCount[ch] == 0) NullTrigger(ch);
                break;
        }
    }

    private void NullTrigger(int ch)
    {
        // Null trigger: signal completion without performing any transfer.
        // Per RP2040 TRM §2.5.2 and rp2040js: fires when IRQ_QUIET IS SET.
        // IRQ_QUIET suppresses normal end-of-transfer IRQs to allow chained sub-transfers;
        // a null write (value=0) to the final trigger alias signals the logical transfer is done.
        if ((_ctrl[ch] & CTRL_IRQ_QUIET) != 0)
        {
            _intr |= 1u << ch;
            if ((_inte0 & (1u << ch)) != 0) _cpu.SetInterrupt(11, true);
            if ((_inte1 & (1u << ch)) != 0) _cpu.SetInterrupt(12, true);
        }
    }

    private void ExecuteChannel(int ch)
    {
        _ctrl[ch] |= CTRL_BUSY;

        var dataSize  = (int)((_ctrl[ch] & CTRL_DATA_SIZE) >> 2);  // 0=byte, 1=half, 2=word
        var incrRead  = (_ctrl[ch] & CTRL_INCR_READ)  != 0;
        var incrWrite = (_ctrl[ch] & CTRL_INCR_WRITE) != 0;
        var bswap     = (_ctrl[ch] & CTRL_BSWAP) != 0;
        var count  = _transCount[ch];
        var rAddr  = _readAddr[ch];
        var wAddr  = _writeAddr[ch];
        var stride = 1u << dataSize;

        // DREQ: bits [20:15] of CTRL
        var treqSel = (int)((_ctrl[ch] >> 15) & 0x3F);
        var dreqSource = treqSel == TREQ_PERMANENT ? null : _dreqSources[treqSel];

        // Ring buffer: RING_SIZE bits [9:6], RING_SEL bit 10
        var ringSize = (int)((_ctrl[ch] >> 6) & 0xF);
        var ringSel  = ((_ctrl[ch] >> 10) & 1) != 0;  // false=read ring, true=write ring
        var ringMask = ringSize > 0 ? (1u << ringSize) - 1 : 0u;

        var beatsExecuted = 0u;
        for (var i = 0u; i < count; i++)
        {
            // Check DREQ: if source is registered and says not ready, stop
            if (dreqSource != null && !dreqSource())
                break;

            uint data = dataSize switch
            {
                0 => _bus.ReadByte(rAddr),
                1 => _bus.ReadHalfWord(rAddr),
                _ => _bus.ReadWord(rAddr),
            };

            if (bswap)
                data = dataSize switch
                {
                    0 => data,
                    1 => ((data & 0xFF) << 8) | (data >> 8),
                    _ => ((data & 0xFF) << 24) | ((data & 0xFF00) << 8)
                       | ((data >> 8) & 0xFF00) | (data >> 24),
                };

            switch (dataSize)
            {
                case 0: _bus.WriteByte(wAddr, (byte)data); break;
                case 1: _bus.WriteHalfWord(wAddr, (ushort)data); break;
                default: _bus.WriteWord(wAddr, data); break;
            }

            if (incrRead)
            {
                if (ringSize > 0 && !ringSel)
                    rAddr = (rAddr & ~ringMask) | ((rAddr + stride) & ringMask);
                else
                    rAddr += stride;
            }
            if (incrWrite)
            {
                if (ringSize > 0 && ringSel)
                    wAddr = (wAddr & ~ringMask) | ((wAddr + stride) & ringMask);
                else
                    wAddr += stride;
            }
            beatsExecuted++;
        }

        _readAddr[ch]   = rAddr;
        _writeAddr[ch]  = wAddr;
        _transCount[ch] = count - beatsExecuted;

        // If not all beats completed (DREQ not ready), stay BUSY
        if (_transCount[ch] == 0)
        {
            // Hardware keeps EN=1 after transfer completes; only BUSY is cleared
            _ctrl[ch] &= ~CTRL_BUSY;

            // Signal completion
            _intr |= 1u << ch;

            // Fire CPU interrupt if unmasked — DMA_IRQ0=11, DMA_IRQ1=12
            if ((_inte0 & (1u << ch)) != 0)
                _cpu.SetInterrupt(11, true);
            if ((_inte1 & (1u << ch)) != 0)
                _cpu.SetInterrupt(12, true);

            // Chain to another channel if configured
            var chainTo = (int)((_ctrl[ch] & CTRL_CHAIN_TO) >> 11);
            if (chainTo != ch && (_ctrl[chainTo] & CTRL_EN) != 0)
                ExecuteChannel(chainTo);
        }
    }
}

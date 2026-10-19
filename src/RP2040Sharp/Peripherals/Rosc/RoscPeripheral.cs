using RP2040.Core.Memory;

namespace RP2040.Peripherals.Rosc;

/// <summary>
/// Ring Oscillator peripheral stub (0x40060000).
/// Reports STATUS.STABLE=1 (bit 31) always so firmware ROSC checks pass.
/// </summary>
public sealed class RoscPeripheral : IMemoryMappedDevice
{
    private const uint CTRL    = 0x00;
    private const uint FREQA   = 0x04;
    private const uint FREQB   = 0x08;
    private const uint DORMANT = 0x0C;
    private const uint DIV     = 0x10;
    private const uint PHASE   = 0x14;
    private const uint STATUS  = 0x18;
    private const uint RANDOMBIT = 0x1C;
    private const uint COUNT   = 0x20;

    private const uint STATUS_STABLE   = 1u << 31;
    private const uint STATUS_ENABLED  = 1u << 12;
    private const uint STATUS_BADWRITE = 1u << 24;

    private uint _ctrl   = 0xFAB;   // enabled (ENABLE field = 0xFAB)
    private uint _freqa;
    private uint _freqb;
    private uint _div    = 0xAA0;   // default divisor
    private uint _phase;

    // Per instance: a static counter made each machine's RANDOMBIT stream depend on every other machine in the
    // process (pico_rand seeds from it), so two runs of the same firmware in one process diverged.
    private uint _randomBit;  // simple pseudo-random bit

    public uint Size => 0x1000;

    public uint ReadWord(uint address) => address switch
    {
        CTRL      => _ctrl,
        FREQA     => _freqa,
        FREQB     => _freqb,
        DORMANT   => 0,
        DIV       => _div,
        PHASE     => _phase,
        STATUS    => STATUS_STABLE | STATUS_ENABLED,
        RANDOMBIT => (++_randomBit) & 1,  // alternate 0/1 as pseudo-random bit
        COUNT     => 0,
        _         => 0,
    };

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case CTRL:    _ctrl  = value & 0xFFF; break;
            case FREQA:   _freqa = value; break;
            case FREQB:   _freqb = value; break;
            case DIV:     _div   = value; break;
            case PHASE:   _phase = value; break;
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
}

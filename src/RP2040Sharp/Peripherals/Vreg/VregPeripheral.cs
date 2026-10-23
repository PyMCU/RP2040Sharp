using RP2040.Core.Memory;

namespace RP2040.Peripherals.Vreg;

/// <summary>
/// Voltage Regulator and Chip Reset peripheral stub (0x40064000).
/// Reports VREG voltage at default 1.1V. Chip reset reason returns 0.
/// </summary>
public sealed class VregPeripheral : IMemoryMappedDevice
{
    private const uint VREG        = 0x00;  // voltage selection
    private const uint BOD         = 0x04;  // brownout detection
    private const uint CHIP_RESET  = 0x08;  // chip reset reason

    // VREG default: 1.1V (VSEL=0b1011)
    private uint _vreg = 0xB1;
    private uint _bod  = 0x0091;   // brownout enabled at ~1.0V

    public uint Size => 0x1000;

    public uint ReadWord(uint address) => address switch
    {
        VREG       => _vreg,
        BOD        => _bod,
        CHIP_RESET => 0,  // no reset reason
        _          => 0,
    };

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case VREG: _vreg = value; break;
            case BOD:  _bod  = value; break;
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

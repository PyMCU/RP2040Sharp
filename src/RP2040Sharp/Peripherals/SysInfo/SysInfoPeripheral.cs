using RP2040.Core.Memory;

namespace RP2040.Peripherals.SysInfo;

/// <summary>
/// SysInfo peripheral (0x40000000).
/// Read-only chip identification registers.
/// </summary>
public sealed class SysInfoPeripheral : IMemoryMappedDevice
{
    private const uint CHIP_ID       = 0x00;   // RP2040 chip ID
    private const uint PLATFORM      = 0x04;
    private const uint PLATFORM_ASIC = 1u << 1;
    private const uint GITREF_RP2040 = 0x40;   // ROM git ref

    // CHIP_ID: REVISION[31:28] PART[27:12]=0x0002 MANUFACTURER[11:0]=0x927 (RP2040 datasheet 2.20 SYSINFO / SVD).
    // B0 and B1 silicon report REVISION 1 (the default bootrom model), B2 reports REVISION 2 (0x20002927, which
    // is also the SVD reset value).
    private readonly uint _chipId;

    public SysInfoPeripheral(RP2040BootromRevision revision = RP2040BootromRevision.B1) =>
        _chipId = revision == RP2040BootromRevision.B2 ? 0x20002927u : 0x10002927u;

    public uint Size => 0x1000;

    public uint ReadWord(uint address) => address switch
    {
        CHIP_ID       => _chipId,
        PLATFORM      => PLATFORM_ASIC,
        GITREF_RP2040 => 0,
        _             => 0,
    };

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value) { }
    public void WriteHalfWord(uint address, ushort value) { }
    public void WriteByte(uint address, byte value) { }
}

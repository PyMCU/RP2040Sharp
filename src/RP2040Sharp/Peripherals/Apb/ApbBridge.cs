using RP2040.Core;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Apb;

/// <summary>
/// APB bridge for the 0x40xxxxxx peripheral bus (region 0x4).
/// Routes using bits [21:14] of the local address (after &amp; 0x0FFFFFFF),
/// which groups each peripheral's four atomic-mirror windows (base, XOR, SET, CLR)
/// into the same 16 KiB slot.
/// The local address passed to each device is address &amp; 0xFFF (4 KiB window).
/// </summary>
public sealed class ApbBridge : IMemoryMappedDevice
{
    // 256 slots, each covering 16 KiB of the APB space
    private readonly IMemoryMappedDevice?[] _devices = new IMemoryMappedDevice?[256];

    public uint Size => 0x10000000;

    /// <summary>
    /// Register a device at its APB base address (full 32-bit address, e.g. 0x40034000).
    /// </summary>
    public void Register(uint baseAddress, IMemoryMappedDevice device)
    {
        // Mask off region nibble → local address, then extract 16 KiB slot index
        var localBase = baseAddress & 0x0FFFFFFF;
        _devices[(localBase >> 14) & 0xFF] = device;
    }

    public uint ReadWord(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        return device.ReadWord(address & 0xFFF);
    }

    public ushort ReadHalfWord(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        return device.ReadHalfWord(address & 0xFFF);
    }

    public byte ReadByte(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        return device.ReadByte(address & 0xFFF);
    }

    public void WriteWord(uint address, uint value)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.write.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return; }

        var atomicType = (address >> 12) & 0x3;
        var offset = address & 0xFFF;

        if (atomicType == 0)
        {
            device.WriteWord(offset, value);
            return;
        }

        var current = device.ReadWord(offset);
        device.WriteWord(offset, atomicType switch
        {
            1 => current ^ value,    // XOR  (+0x1000)
            2 => current | value,    // SET  (+0x2000)
            3 => current & ~value,   // CLR  (+0x3000)
            _ => value,
        });
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.write.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return; }

        var atomicType = (address >> 12) & 0x3;
        var offset = address & 0xFFF;

        if (atomicType == 0) { device.WriteHalfWord(offset, value); return; }

        var aligned = offset & ~3u;
        var shift = (int)((offset & 2) << 3);
        var current = device.ReadWord(aligned);
        uint expanded = (uint)value << shift;
        uint mask = 0xFFFFu << shift;
        device.WriteWord(aligned, atomicType switch
        {
            1 => (current & ~mask) | ((current ^ expanded) & mask),
            2 => current | expanded,
            3 => current & ~expanded,
            _ => (current & ~mask) | expanded,
        });
    }

    public void WriteByte(uint address, byte value)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.write.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return; }

        var atomicType = (address >> 12) & 0x3;
        var offset = address & 0xFFF;

        if (atomicType == 0) { device.WriteByte(offset, value); return; }

        var aligned = offset & ~3u;
        var shift = (int)((offset & 3) << 3);
        var current = device.ReadWord(aligned);
        uint expanded = (uint)value << shift;
        uint mask = 0xFFu << shift;
        device.WriteWord(aligned, atomicType switch
        {
            1 => (current & ~mask) | ((current ^ expanded) & mask),
            2 => current | expanded,
            3 => current & ~expanded,
            _ => (current & ~mask) | expanded,
        });
    }
}

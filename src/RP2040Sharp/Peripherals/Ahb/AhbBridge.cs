using RP2040.Core.Memory;

namespace RP2040.Peripherals.Ahb;

/// <summary>
/// Sub-router for a 256 MB address region (e.g., region 0x5).
/// Dispatches by 1 MB blocks: index = (address >> 20) &amp; 0xFF.
/// Devices receive the address unchanged.
/// </summary>
public sealed class AhbBridge : IMemoryMappedDevice, IAddressDecoder
{
    private readonly IMemoryMappedDevice?[] _devices = new IMemoryMappedDevice?[256];
    private readonly bool[] _present = new bool[256];

    public uint Size => 0x1000_0000;  // full region

    public uint DecodeLimit => uint.MaxValue;
    public bool[]? SlotPresent => _present;
    public int SlotShift => 20;

    /// <summary>Register a device. baseAddress bits [27:20] determine the slot.</summary>
    public void Register(uint baseAddress, IMemoryMappedDevice device)
    {
        var idx = (baseAddress >> 20) & 0xFF;
        _devices[idx] = device;
        _present[idx] = true;
    }

    public uint ReadWord(uint address)
        => _devices[(address >> 20) & 0xFF]?.ReadWord(address) ?? 0;

    public ushort ReadHalfWord(uint address)
        => _devices[(address >> 20) & 0xFF]?.ReadHalfWord(address) ?? 0;

    public byte ReadByte(uint address)
        => _devices[(address >> 20) & 0xFF]?.ReadByte(address) ?? 0;

    public void WriteWord(uint address, uint value)
    {
        var device = _devices[(address >> 20) & 0xFF];
        if (device == null) return;

        var atomicType = (address >> 12) & 0x3;
        // Devices that handle atomic aliases themselves receive the raw address+value.
        if (atomicType == 0 || device is IHandlesAtomicAliases) { device.WriteWord(address, value); return; }

        var baseAddr = address & ~0x3000u;
        var current = device.ReadWord(baseAddr);
        device.WriteWord(baseAddr, atomicType switch
        {
            1 => current ^ value,
            2 => current | value,
            3 => current & ~value,
            _ => value,
        });
    }

    // Datasheet 2.1.4: IO registers sample the whole 32-bit write bus and the Cortex-M0+ and DMA replicate narrow
    // data across it, so a byte or halfword write is a word write of the replicated value. A device that handles
    // its own aliases (the USB block, whose DPRAM is genuine byte-writable memory) decides for itself.
    public void WriteHalfWord(uint address, ushort value)
    {
        var device = _devices[(address >> 20) & 0xFF];
        if (device == null) return;
        if (device is IHandlesAtomicAliases) { device.WriteHalfWord(address, value); return; }
        WriteWord(address & ~3u, value * 0x00010001u);
    }

    public void WriteByte(uint address, byte value)
    {
        var device = _devices[(address >> 20) & 0xFF];
        if (device == null) return;
        if (device is IHandlesAtomicAliases) { device.WriteByte(address, value); return; }
        WriteWord(address & ~3u, value * 0x01010101u);
    }
}

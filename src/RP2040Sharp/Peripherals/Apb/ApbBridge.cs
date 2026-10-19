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
public sealed class ApbBridge : IMemoryMappedDevice, IAddressDecoder
{
    // 256 slots, each covering 16 KiB of the APB space
    private readonly IMemoryMappedDevice?[] _devices = new IMemoryMappedDevice?[256];
    private readonly bool[] _present = new bool[256];

    // Deferred ticks of the peripherals behind each slot (docs/peripheral-deadlines.md): synced before every
    // access, asked to plan again after every write (and after reads where reading changes the state).
    private readonly DeferredTick?[] _schedule = new DeferredTick?[256];
    private readonly bool[] _readsMutate = new bool[256];

    /// <summary>Make every access to the device at <paramref name="baseAddress"/> sync <paramref name="schedule"/> first.</summary>
    public void RegisterSchedule(uint baseAddress, DeferredTick schedule, bool readsMutate = false)
    {
        var slot = ((baseAddress & 0x0FFFFFFF) >> 14) & 0xFF;
        _schedule[slot] = schedule;
        _readsMutate[slot] = readsMutate;
    }

    private void BeforeRead(uint slot) => _schedule[slot]?.Sync();
    private void AfterRead(uint slot) { if (_readsMutate[slot]) _schedule[slot]?.Touch(); }

    public uint Size => 0x10000000;

    // 256 slots of 16 KiB: the bridge decodes 0x40000000-0x403FFFFF, and only where a peripheral sits
    public uint DecodeLimit => 0x400000;
    public bool[]? SlotPresent => _present;
    public int SlotShift => 14;

    /// <summary>
    /// Register a device at its APB base address (full 32-bit address, e.g. 0x40034000).
    /// </summary>
    public void Register(uint baseAddress, IMemoryMappedDevice device)
    {
        // Mask off region nibble → local address, then extract 16 KiB slot index
        var localBase = baseAddress & 0x0FFFFFFF;
        _devices[(localBase >> 14) & 0xFF] = device;
        _present[(localBase >> 14) & 0xFF] = true;
    }

    public uint ReadWord(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        var slot = (address >> 14) & 0xFF;
        if (_schedule[slot] == null) return device.ReadWord(address & 0xFFF);
        BeforeRead(slot);
        var v = device.ReadWord(address & 0xFFF);
        AfterRead(slot);
        return v;
    }

    public ushort ReadHalfWord(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        var slot = (address >> 14) & 0xFF;
        if (_schedule[slot] == null) return device.ReadHalfWord(address & 0xFFF);
        BeforeRead(slot);
        var v = device.ReadHalfWord(address & 0xFFF);
        AfterRead(slot);
        return v;
    }

    public byte ReadByte(uint address)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.read.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return 0; }
        var slot = (address >> 14) & 0xFF;
        if (_schedule[slot] == null) return device.ReadByte(address & 0xFFF);
        BeforeRead(slot);
        var v = device.ReadByte(address & 0xFFF);
        AfterRead(slot);
        return v;
    }

    public void WriteWord(uint address, uint value)
    {
        var device = _devices[(address >> 14) & 0xFF];
        if (device == null) { EmuStrict.Note("apb.write.unmapped-peripheral", $"0x{0x40000000u | (address & 0x0FFFFFFF):X8}"); return; }

        var schedule = _schedule[(address >> 14) & 0xFF];
        if (schedule == null) { WriteDevice(device, address, value); return; }
        schedule.Sync();
        WriteDevice(device, address, value);
        schedule.Touch();
    }

    private static void WriteDevice(IMemoryMappedDevice device, uint address, uint value)
    {
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

    // Datasheet 2.1.4: IO registers ignore the width of a write and sample the whole 32-bit data bus, and the
    // Cortex-M0+ and the DMA replicate narrow data across all lanes. So a byte or halfword write is a word write
    // of the replicated value to the word the address falls in, the atomic aliases included (a narrow SET ORs the
    // replicated value into the register).
    public void WriteHalfWord(uint address, ushort value) =>
        WriteWord(address & ~3u, value * 0x00010001u);

    public void WriteByte(uint address, byte value) =>
        WriteWord(address & ~3u, value * 0x01010101u);
}

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

    // Deferred ticks of the peripherals behind each slot (docs/peripheral-deadlines.md): synced before every
    // access, asked to plan again after every write (and after reads where reading changes the state).
    private readonly DeferredTick?[] _schedule = new DeferredTick?[256];
    private readonly bool[] _readsMutate = new bool[256];

    /// <summary>Make every access to the device at <paramref name="baseAddress"/> sync <paramref name="schedule"/> first.</summary>
    public void RegisterSchedule(uint baseAddress, DeferredTick schedule, bool readsMutate = false)
    {
        var slot = (baseAddress >> 20) & 0xFF;
        _schedule[slot] = schedule;
        _readsMutate[slot] = readsMutate;
    }

    private uint Read(uint address, int width)
    {
        var slot = (address >> 20) & 0xFF;
        var device = _devices[slot];
        if (device == null) return 0;
        var schedule = _schedule[slot];
        schedule?.Sync();
        uint v = width switch
        {
            4 => device.ReadWord(address),
            2 => device.ReadHalfWord(address),
            _ => device.ReadByte(address),
        };
        if (schedule != null && _readsMutate[slot]) schedule.Touch();
        return v;
    }

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

    public uint ReadWord(uint address) => Read(address, 4);

    public ushort ReadHalfWord(uint address) => (ushort)Read(address, 2);

    public byte ReadByte(uint address) => (byte)Read(address, 1);

    public void WriteWord(uint address, uint value)
    {
        var schedule = _schedule[(address >> 20) & 0xFF];
        if (schedule == null) { WriteWordCore(address, value); return; }
        schedule.Sync();
        WriteWordCore(address, value);
        schedule.Touch();
    }

    private void WriteWordCore(uint address, uint value)
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
        if (device is IHandlesAtomicAliases)
        {
            var schedule = _schedule[(address >> 20) & 0xFF];
            schedule?.Sync();
            device.WriteHalfWord(address, value);
            schedule?.Touch();
            return;
        }
        WriteWord(address & ~3u, value * 0x00010001u);
    }

    public void WriteByte(uint address, byte value)
    {
        var device = _devices[(address >> 20) & 0xFF];
        if (device == null) return;
        if (device is IHandlesAtomicAliases)
        {
            var schedule = _schedule[(address >> 20) & 0xFF];
            schedule?.Sync();
            device.WriteByte(address, value);
            schedule?.Touch();
            return;
        }
        WriteWord(address & ~3u, value * 0x01010101u);
    }
}

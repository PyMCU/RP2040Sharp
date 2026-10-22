using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RP2040.Core.Memory;

public unsafe class BusInterconnect : IMemoryBus, IDisposable
{
    public const uint REGION_BOOTROM = 0x0;
    public const uint REGION_FLASH = 0x1;
    public const uint REGION_SRAM = 0x2;

    public const uint MASK_SRAM = 0x7FFFF;    // size of the backing buffer (512KB); SRAM_SIZE of it is addressable
    public const uint MASK_BOOTROM = 0x3FFF;  // 16KB

    /// <summary>Addressable SRAM: 0x20000000-0x20041FFF, 264 KiB (datasheet 2.2.2). Anything above it is not mapped.</summary>
    public const uint SRAM_SIZE = 0x42000;

    /// <summary>Boot ROM size: 0x00000000-0x00003FFF. The rest of region 0 is not mapped (datasheet 2.2.1).</summary>
    public const uint BOOTROM_SIZE = 0x4000;

    // 0x10000000-0x17FFFFFF: the flash array through its XIP aliases. XIP_CTRL (0x14000000) and XIP_SRAM
    // (0x15000000) sit inside it and are not modelled: they read the array like the rest of the window.
    public const uint XIP_ARRAY_SPAN = 0x08000000;

    // The non-striped aliases of SRAM0-3 (datasheet 2.6.1): 0x21000000-0x2103FFFF, one 64 KiB bank each.
    private const uint SRAM_ALIAS_START = 0x21000000;
    private const uint SRAM_ALIAS_SIZE = 0x40000;

    public uint FlashSize { get; }
    public uint MaskFlash { get; }

    public const uint SRAM_START_ADDRESS = 0x20000000;
    public const uint FLASH_START_ADDRESS = 0x10000000;

    public readonly byte* PtrSram;
    public readonly byte* PtrFlash;
    public readonly byte* PtrBootRom;

    private struct Region
    {
        public IMemoryMappedDevice? Device;
        public bool[]? SlotPresent;
        public uint DecodeLimit;
        public int SlotShift;
    }

    private readonly Region[] _regions = new Region[16];

    // What a bridged region decodes, cached from the device's IAddressDecoder when it is mapped, so a
    // peripheral access checks it with two array loads instead of a type test and a call.

    // SSI peripheral lives inside the Flash region (0x18000000) — handled as a sub-device
    // so the flash fast path continues to serve 0x10000000–0x17FFFFFF unchanged.
    private IMemoryMappedDevice? _ssiDevice;
    private const uint SSI_BASE_ADDRESS = 0x18000000;
    private const uint SSI_SPAN = 0x01000000;

    private readonly RandomAccessMemory _sram;
    private readonly RandomAccessMemory _bootRom;
    private readonly RandomAccessMemory _flash;

    private bool _disposed;

    public BusInterconnect(uint flashSizeBytes = 2 * 1024 * 1024)
    {
        FlashSize = flashSizeBytes;
        MaskFlash = flashSizeBytes - 1;

        _sram = new RandomAccessMemory(512 * 1024);
        _flash = new RandomAccessMemory((int)flashSizeBytes);
        _bootRom = new RandomAccessMemory(16 * 1024);

        PtrSram = _sram.BasePtr;
        PtrFlash = _flash.BasePtr;
        PtrBootRom = _bootRom.BasePtr;

        MapDevice((int)REGION_BOOTROM, _bootRom);
        MapDevice((int)REGION_FLASH, _flash);
        MapDevice((int)REGION_SRAM, _sram);
    }

    public void MapDevice(int regionIndex, IMemoryMappedDevice device)
    {
        if (regionIndex is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));
        ref var entry = ref _regions[regionIndex];
        entry.Device = device;
        if (device is IAddressDecoder decoder)
        {
            entry.DecodeLimit = decoder.DecodeLimit;
            entry.SlotPresent = decoder.SlotPresent;
            entry.SlotShift = decoder.SlotShift;
        }
        else
        {
            entry.DecodeLimit = uint.MaxValue;
            entry.SlotPresent = null;
        }
    }

    /// <summary>
    /// Register the SSI peripheral at 0x18000000 (within the XIP flash region).
    /// Accesses to [0x18000000, 0x18FFFFFF] are forwarded to <paramref name="ssi"/>;
    /// the rest of the flash region continues to use the fast pointer path.
    /// </summary>
    public void RegisterSsi(IMemoryMappedDevice ssi) => _ssiDevice = ssi;

    // Datasheet 2.2: "Unmapped address ranges raise a bus error when accessed." The Try* accessors report it
    // (false) so the CPU can take a HardFault; the plain accessors below keep their lenient contract for the DMA
    // front end, the debugger and tests, and read 0 / drop the write at an unmapped address.

    // --- READS ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadByte(uint address, out byte value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { value = PtrSram[sram]; return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN) { value = PtrFlash[address & MaskFlash]; return true; }
        if (address < BOOTROM_SIZE) { value = PtrBootRom[address]; return true; }
        var ok = TryReadByteSlow(address, out var slow);
        value = slow;
        return ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadHalfWord(uint address, out ushort value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { value = Unsafe.ReadUnaligned<ushort>(PtrSram + sram); return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN)
        {
            value = Unsafe.ReadUnaligned<ushort>(PtrFlash + (address & MaskFlash));
            return true;
        }
        if (address < BOOTROM_SIZE) { value = Unsafe.ReadUnaligned<ushort>(PtrBootRom + address); return true; }
        var ok = TryReadHalfWordSlow(address, out var slow);
        value = slow;
        return ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadWord(uint address, out uint value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { value = Unsafe.ReadUnaligned<uint>(PtrSram + sram); return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN)
        {
            value = Unsafe.ReadUnaligned<uint>(PtrFlash + (address & MaskFlash));
            return true;
        }
        if (address < BOOTROM_SIZE) { value = Unsafe.ReadUnaligned<uint>(PtrBootRom + address); return true; }
        var ok = TryReadWordSlow(address, out var slow);
        value = slow;
        return ok;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        TryReadByte(address, out var value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadHalfWord(uint address)
    {
        TryReadHalfWord(address, out var value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadWord(uint address)
    {
        TryReadWord(address, out var value);
        return value;
    }

    // --- SLOW PATH: the SSI window, the SRAM aliases, the bridged peripheral regions, and unmapped space ---

    /// <summary>Byte offset into the striped SRAM array for an address in a non-striped alias bank, or false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryMapSramAlias(uint address, out uint offset)
    {
        var a = address - SRAM_ALIAS_START;
        if (a >= SRAM_ALIAS_SIZE) { offset = 0; return false; }
        // Bank n is the 64 KiB at +n*64K; striped, word w of bank n is word w*4+n.
        var bank = a >> 16;
        var word = (a >> 2) & 0x3FFF;
        offset = ((word << 2 | bank) << 2) | (a & 3);
        return true;
    }

    /// <summary>The device for a bridged region if it decodes this address, else null (unmapped).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IMemoryMappedDevice? Decode(uint address)
    {
        var region = address >> 28;
        if (region <= REGION_SRAM) return null;
        ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_regions), (nint)region);
        var device = entry.Device;
        if (device is null) return null;
        var local = address & 0x0FFFFFFF;
        if (local >= entry.DecodeLimit) return null;
        var slots = entry.SlotPresent;
        if (slots is not null && !slots[(local >> entry.SlotShift) & 0xFF]) return null;
        return device;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadByteSlow(uint address, out byte value)
    {
        if (Decode(address) is { } d) { value = d.ReadByte(address & 0x0FFFFFFF); return true; }
        return TryReadByteRare(address, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadByteRare(uint address, out byte value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            value = _ssiDevice.ReadByte(address - SSI_BASE_ADDRESS);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { value = PtrSram[offset]; return true; }
        value = EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", (byte)0);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadHalfWordSlow(uint address, out ushort value)
    {
        if (Decode(address) is { } d) { value = d.ReadHalfWord(address & 0x0FFFFFFF); return true; }
        return TryReadHalfWordRare(address, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadHalfWordRare(uint address, out ushort value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            value = _ssiDevice.ReadHalfWord(address - SSI_BASE_ADDRESS);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { value = Unsafe.ReadUnaligned<ushort>(PtrSram + offset); return true; }
        value = EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", (ushort)0);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadWordSlow(uint address, out uint value) => TryReadWordIO(address, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadWordIO(uint address, out uint value)
    {
        if (Decode(address) is { } d) { value = d.ReadWord(address & 0x0FFFFFFF); return true; }
        return TryReadWordRare(address, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadWordRare(uint address, out uint value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            value = _ssiDevice.ReadWord(address - SSI_BASE_ADDRESS);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { value = Unsafe.ReadUnaligned<uint>(PtrSram + offset); return true; }
        value = EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", 0u);
        return false;
    }

    // --- WRITES ---
    // ROM and the flash/XIP window take writes without effect (flash is programmed through the ROM routines),
    // which is not a fault.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteWord(uint address, uint value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { Unsafe.WriteUnaligned(PtrSram + sram, value); return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN || address < BOOTROM_SIZE) return true;
        return TryWriteWordSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteByte(uint address, byte value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { PtrSram[sram] = value; return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN || address < BOOTROM_SIZE) return true;
        return TryWriteByteSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteHalfWord(uint address, ushort value)
    {
        var sram = address - SRAM_START_ADDRESS;
        if (sram < SRAM_SIZE) { Unsafe.WriteUnaligned(PtrSram + sram, value); return true; }
        if (address - FLASH_START_ADDRESS < XIP_ARRAY_SPAN || address < BOOTROM_SIZE) return true;
        return TryWriteHalfWordSlow(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteWord(uint address, uint value) => TryWriteWord(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value) => TryWriteByte(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHalfWord(uint address, ushort value) => TryWriteHalfWord(address, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteWordSlow(uint address, uint value) => TryWriteWordIO(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWriteWordIO(uint address, uint value)
    {
        if (Decode(address) is { } d) { d.WriteWord(address & 0x0FFFFFFF, value); return true; }
        return TryWriteWordRare(address, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteWordRare(uint address, uint value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            _ssiDevice.WriteWord(address - SSI_BASE_ADDRESS, value);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { Unsafe.WriteUnaligned(PtrSram + offset, value); return true; }
        EmuStrict.Note("mmio.write.unmapped-region", $"0x{address:X8}");
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteByteSlow(uint address, byte value)
    {
        if (Decode(address) is { } d) { d.WriteByte(address & 0x0FFFFFFF, value); return true; }
        return TryWriteByteRare(address, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteByteRare(uint address, byte value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            _ssiDevice.WriteByte(address - SSI_BASE_ADDRESS, value);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { PtrSram[offset] = value; return true; }
        EmuStrict.Note("mmio.write.unmapped-region", $"0x{address:X8}");
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteHalfWordSlow(uint address, ushort value)
    {
        if (Decode(address) is { } d) { d.WriteHalfWord(address & 0x0FFFFFFF, value); return true; }
        return TryWriteHalfWordRare(address, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWriteHalfWordRare(uint address, ushort value)
    {
        if (address - SSI_BASE_ADDRESS < SSI_SPAN && _ssiDevice != null)
        {
            _ssiDevice.WriteHalfWord(address - SSI_BASE_ADDRESS, value);
            return true;
        }
        if (TryMapSramAlias(address, out var offset)) { Unsafe.WriteUnaligned(PtrSram + offset, value); return true; }
        EmuStrict.Note("mmio.write.unmapped-region", $"0x{address:X8}");
        return false;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _sram?.Dispose();
            _flash?.Dispose();
            _bootRom?.Dispose();
        }

        _disposed = true;
    }
}

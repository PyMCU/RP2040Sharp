using System.Runtime.CompilerServices;

namespace RP2040.Core.Memory;

public unsafe class BusInterconnect : IMemoryBus, IDisposable
{
    public const uint REGION_BOOTROM = 0x0;
    public const uint REGION_FLASH = 0x1;
    public const uint REGION_SRAM = 0x2;

    public const uint MASK_SRAM = 0x7FFFF;    // 512KB (covers 264KB + mirrors)
    public const uint MASK_BOOTROM = 0x3FFF;  // 16KB

    public uint FlashSize { get; }
    public uint MaskFlash { get; }

    public const uint SRAM_START_ADDRESS = 0x20000000;
    public const uint FLASH_START_ADDRESS = 0x10000000;

    public readonly byte* PtrSram;
    public readonly byte* PtrFlash;
    public readonly byte* PtrBootRom;

    private readonly IMemoryMappedDevice[] _memoryMap = new IMemoryMappedDevice[16];

    // SSI peripheral lives inside the Flash region (0x18000000) — handled as a sub-device
    // so the flash fast path continues to serve 0x10000000–0x17FFFFFF unchanged.
    private IMemoryMappedDevice? _ssiDevice;
    private const uint SSI_BASE_ADDRESS = 0x18000000;

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
        _memoryMap[regionIndex] = device;
    }

    /// <summary>
    /// Register the SSI peripheral at 0x18000000 (within the XIP flash region).
    /// Accesses to [0x18000000, 0x18FFFFFF] are forwarded to <paramref name="ssi"/>;
    /// the rest of the flash region continues to use the fast pointer path.
    /// </summary>
    public void RegisterSsi(IMemoryMappedDevice ssi) => _ssiDevice = ssi;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
            return PtrSram[address & MASK_SRAM];
        if (region == REGION_FLASH)
        {
            if (_ssiDevice != null && address >= SSI_BASE_ADDRESS)
                return _ssiDevice.ReadByte(address - SSI_BASE_ADDRESS);
            return PtrFlash[address & MaskFlash];
        }
        if (region == REGION_BOOTROM)
            return PtrBootRom[address & MASK_BOOTROM];
        return ReadByteDispatch(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadHalfWord(uint address)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
            return Unsafe.ReadUnaligned<ushort>(PtrSram + (address & MASK_SRAM));
        if (region == REGION_FLASH)
        {
            if (_ssiDevice != null && address >= SSI_BASE_ADDRESS)
                return _ssiDevice.ReadHalfWord(address - SSI_BASE_ADDRESS);
            return Unsafe.ReadUnaligned<ushort>(PtrFlash + (address & MaskFlash));
        }
        if (region == REGION_BOOTROM)
            return Unsafe.ReadUnaligned<ushort>(PtrBootRom + (address & MASK_BOOTROM));
        return ReadHalfWordDispatch(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadWord(uint address)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
            return Unsafe.ReadUnaligned<uint>(PtrSram + (address & MASK_SRAM));
        if (region == REGION_FLASH)
        {
            if (_ssiDevice != null && address >= SSI_BASE_ADDRESS)
                return _ssiDevice.ReadWord(address - SSI_BASE_ADDRESS);
            return Unsafe.ReadUnaligned<uint>(PtrFlash + (address & MaskFlash));
        }
        if (region == REGION_BOOTROM)
            return Unsafe.ReadUnaligned<uint>(PtrBootRom + (address & MASK_BOOTROM));
        return ReadWordDispatch(address);
    }

    // --- SLOW PATH DISPATCHERS ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte ReadByteDispatch(uint address) =>
        _memoryMap[address >> 28] is { } d ? d.ReadByte(address & 0x0FFFFFFF)
            : EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", (byte)0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ushort ReadHalfWordDispatch(uint address) =>
        _memoryMap[address >> 28] is { } d ? d.ReadHalfWord(address & 0x0FFFFFFF)
            : EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", (ushort)0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint ReadWordDispatch(uint address) =>
        _memoryMap[address >> 28] is { } d ? d.ReadWord(address & 0x0FFFFFFF)
            : EmuStrict.NoteRet("mmio.read.unmapped-region", $"0x{address:X8}", 0u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteWord(uint address, uint value)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
        {
            Unsafe.WriteUnaligned(PtrSram + (address & MASK_SRAM), value);
            return;
        }
        if (_ssiDevice != null && region == REGION_FLASH && address >= SSI_BASE_ADDRESS)
        {
            _ssiDevice.WriteWord(address - SSI_BASE_ADDRESS, value);
            return;
        }
        if (region == REGION_FLASH || region == REGION_BOOTROM)
            return;

        WriteWordDispatch(region, address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
        {
            PtrSram[address & MASK_SRAM] = value;
            return;
        }
        if (_ssiDevice != null && region == REGION_FLASH && address >= SSI_BASE_ADDRESS)
        {
            _ssiDevice.WriteByte(address - SSI_BASE_ADDRESS, value);
            return;
        }
        if (region <= REGION_FLASH)
            return; // ROM(0) o FLASH(1)
        _memoryMap[region]?.WriteByte(address & 0x0FFFFFFF, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHalfWord(uint address, ushort value)
    {
        var region = address >> 28;
        if (region == REGION_SRAM)
        {
            Unsafe.WriteUnaligned(PtrSram + (address & MASK_SRAM), value);
            return;
        }
        if (_ssiDevice != null && region == REGION_FLASH && address >= SSI_BASE_ADDRESS)
        {
            _ssiDevice.WriteHalfWord(address - SSI_BASE_ADDRESS, value);
            return;
        }
        if (region <= REGION_FLASH)
            return;
        _memoryMap[region]?.WriteHalfWord(address & 0x0FFFFFFF, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteWordDispatch(uint region, uint address, uint value)
    {
        if (_memoryMap[region] is { } d) d.WriteWord(address & 0x0FFFFFFF, value);
        else EmuStrict.Note("mmio.write.unmapped-region", $"0x{address:X8}");
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

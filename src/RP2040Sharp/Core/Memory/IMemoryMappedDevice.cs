namespace RP2040.Core.Memory;

public interface IMemoryMappedDevice
{
    uint Size { get; }

    byte ReadByte(uint address);
    ushort ReadHalfWord(uint address);
    uint ReadWord(uint address);

    void WriteByte(uint address, byte value);
    void WriteHalfWord(uint address, ushort value);
    void WriteWord(uint address, uint value);
}

/// <summary>
/// A device that covers a sparse part of its region (a bridge with empty slots, the 1 MiB PPB window inside
/// 256 MiB). The bus asks it whether an address is decoded; one that is not raises a bus error
/// (datasheet 2.2: "Unmapped address ranges raise a bus error when accessed").
/// </summary>
public interface IAddressDecoder
{
    /// <summary>Local addresses (within the 256 MiB region) at or above this are not decoded.</summary>
    uint DecodeLimit { get; }

    /// <summary>
    /// When non-null, the region is cut into slots of <c>1 &lt;&lt; <see cref="SlotShift"/></c> bytes and an address is
    /// decoded only if its slot, <c>(local &gt;&gt; SlotShift) &amp; 0xFF</c>, is flagged. The array is live: the device
    /// keeps flagging slots as peripherals are registered after the bus has been wired.
    /// </summary>
    bool[]? SlotPresent { get; }

    int SlotShift { get; }
}

/// <summary>
/// Marker interface: the device processes RP2040 atomic-alias addresses
/// (bits 12–13 = XOR/SET/CLR) internally. The AHB bridge will pass the
/// full, unmodified address and the raw firmware write value so the device
/// can apply the correct per-register semantics (e.g. W1C vs R/W).
/// </summary>
public interface IHandlesAtomicAliases { }

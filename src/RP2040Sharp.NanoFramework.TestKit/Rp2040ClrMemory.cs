using NfClr;
using RP2040.Peripherals;

namespace RP2040Sharp.NanoFramework.TestKit;

/// <summary>
/// Adapts an RP2040Sharp machine's bus to the chip-agnostic <see cref="IClrMemory"/> so the shared
/// <see cref="ClrInspector"/> can read this emulator's RAM. This is the one RP2040-specific seam the
/// CLR walker needs; the rest of the introspection lives in NfClr.Core.
/// </summary>
public sealed class Rp2040ClrMemory : IClrMemory
{
    private readonly RP2040Machine _machine;

    public Rp2040ClrMemory(RP2040Machine machine) => _machine = machine;

    public uint ReadWord(uint address) => _machine.Bus.ReadWord(address);

    public ushort ReadHalfWord(uint address) => _machine.Bus.ReadHalfWord(address);

    public byte ReadByte(uint address) => _machine.Bus.ReadByte(address);

    // RP2040 SRAM: 264 KB at 0x20000000 (the striped main banks + scratch).
    public uint RamStart => 0x20000000;

    public uint RamEnd => 0x20042000;
}

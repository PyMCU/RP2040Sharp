using RP2040.Core.Memory;

namespace RP2040.Peripherals.Resets;

/// <summary>
/// RESETS peripheral (0x4000C000).
/// Controls reset state of each RP2040 subsystem.
/// Firmware writes RESET bits to hold subsystems in reset, then clears bits
/// to bring them out. RESET_DONE returns the complement — polled by SDK init.
/// </summary>
public sealed class ResetsPeripheral : IMemoryMappedDevice
{
    private const uint RESET      = 0x00;
    private const uint WDSEL      = 0x04;
    private const uint RESET_DONE = 0x08;

    // 25 subsystem bits
    private const uint ALL_BITS = 0x01FFFFFF;
    private const uint USBCTRL_BIT = 1u << 24;

    // PIO blocks (bits 10=PIO0, 11=PIO1) power up HELD IN RESET on real silicon — firmware must clear
    // their bit (and poll RESET_DONE) before the block responds. Modelling this lets the emulator catch
    // firmware that drives PIO registers without first un-resetting it (a silent no-op on hardware).
    // Other subsystems stay out of reset from power-on so unrelated firmware isn't forced to un-reset
    // them (lenient, matching prior behaviour).
    private const uint PIO_RESET_BITS = (1u << 10) | (1u << 11);
    private uint _reset = PIO_RESET_BITS;
    private uint _wdsel;

    /// <summary>Fired when subsystems are released from reset (their bit went set → clear).</summary>
    public Action<uint>? OnUnreset;

    /// <summary>Fired when subsystems are put into reset (their bit went clear → set).</summary>
    public Action<uint>? OnReset;

    public uint Size => 0x1000;

    public uint ReadWord(uint address) => address switch
    {
        RESET      => _reset,
        WDSEL      => _wdsel,
        RESET_DONE => (~_reset) & ALL_BITS,
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
            case RESET:
                var prev = _reset;
                _reset = value & ALL_BITS;
                // Fire OnUnreset for any bits that transitioned from set to clear (and OnReset for the reverse).
                var released = prev & ~_reset;
                if (released != 0) OnUnreset?.Invoke(released);
                var asserted = ~prev & _reset;
                if (asserted != 0) OnReset?.Invoke(asserted);
                break;
            case WDSEL: _wdsel = value & ALL_BITS; break;
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

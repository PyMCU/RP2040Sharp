using RP2040.Core.Memory;

namespace RP2040.Peripherals.Psm;

/// <summary>
/// Power-on State Machine peripheral (0x40010000).
/// In simulation all subsystems are always ready (DONE = all bits set).
/// </summary>
public sealed class PsmPeripheral : IMemoryMappedDevice
{
    private const uint FRCE_ON  = 0x00;
    private const uint FRCE_OFF = 0x04;
    private const uint WDSEL    = 0x08;
    private const uint DONE     = 0x0C;

    // All 17 subsystem bits (proc0..spi1)
    private const uint ALL_BITS = 0x0001FFFF;

    // FRCE_OFF bit 16 = PROC1 (force Core 1 into its power-on reset state).
    private const uint PROC1_BIT = 1u << 16;

    private uint _frceOn;
    private uint _frceOff;
    private uint _wdsel;

    /// <summary>
    /// Raised when firmware releases Core 1 from forced-off state, i.e. clears the PROC1
    /// bit of FRCE_OFF after having set it.  On real silicon this brings Core 1 out of
    /// reset so it re-runs its bootrom.  pico-sdk's <c>multicore_reset_core1()</c> — called
    /// by MicroPython before every <c>_thread.start_new_thread</c> — does exactly this and
    /// then blocks on the FIFO for Core 1's "ready" word, so <see cref="RP2040Machine"/>
    /// hooks this to reset Core 1 and inject that word.
    /// </summary>
    public Action? OnProc1Released;

    public uint Size => 0x1000;

    public uint ReadWord(uint address) => address switch
    {
        FRCE_ON  => _frceOn,
        FRCE_OFF => _frceOff,
        WDSEL    => _wdsel,
        DONE     => ALL_BITS,   // all subsystems always done in simulation
        _        => 0,
    };

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case FRCE_ON:  _frceOn  = value & ALL_BITS; break;
            case FRCE_OFF:
            {
                var previous = _frceOff;
                _frceOff = value & ALL_BITS;
                // PROC1 going set → clear releases Core 1 from reset (multicore_reset_core1).
                if ((previous & PROC1_BIT) != 0 && (_frceOff & PROC1_BIT) == 0)
                    OnProc1Released?.Invoke();
                break;
            }
            case WDSEL:    _wdsel   = value & ALL_BITS; break;
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

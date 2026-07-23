using RP2040.Core.Memory;

namespace RP2040.Peripherals.Pll;

/// <summary>
/// PLL peripheral stub (PLL_SYS at 0x40028000, PLL_USB at 0x4002C000).
/// Reports CS.LOCK=1 (bit 31) always so firmware PLL lock-wait loops complete.
/// </summary>
public sealed class PllPeripheral : IMemoryMappedDevice
{
    private const uint CS   = 0x00;  // bit31=LOCK, bit0=BYPASS
    private const uint PWR  = 0x04;  // power control
    private const uint FBDIV_INT = 0x08;  // feedback divisor (integer)
    private const uint PRIM = 0x0C;  // post dividers

    private const uint CS_LOCK = 1u << 31;

    private uint _pwr  = 0x2D;   // default: VCOPD=0, POSTDIVPD=0, DSMPD=1, PD=1 (powered)
    private uint _fbdiv = 100;
    private uint _prim = 0x00062000;  // POSTDIV1=6, POSTDIV2=2 — gives 125 MHz from 12 MHz XOSC

    public uint Size => 0x1000;

    /// <summary>Output frequency (Hz) from FBDIV/POSTDIV for the clock tree / inspection.
    /// RP2040 REFDIV is not modelled here (stub), so assume REFDIV = 1.</summary>
    public uint OutputHz(uint refHz)
    {
        var postdiv1 = (_prim >> 16) & 0x7u;
        var postdiv2 = (_prim >> 12) & 0x7u;
        if (postdiv1 == 0) postdiv1 = 1;
        if (postdiv2 == 0) postdiv2 = 1;
        var fbdiv = _fbdiv == 0 ? 1u : _fbdiv;
        return (uint)((ulong)refHz * fbdiv / (postdiv1 * postdiv2));
    }

    public uint ReadWord(uint address) => address switch
    {
        CS       => CS_LOCK,   // always locked in simulation
        PWR      => _pwr,
        FBDIV_INT => _fbdiv,
        PRIM     => _prim,
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
            case PWR:       _pwr   = value; break;
            case FBDIV_INT: _fbdiv = value & 0xFFF; break;
            case PRIM:      _prim  = value; break;
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

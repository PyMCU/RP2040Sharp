using RP2040.Core.Memory;

namespace RP2040.Peripherals.Pads;

/// <summary>
/// PADS_BANK0 peripheral (0x4001C000) and PADS_QSPI (0x40020000).
/// Controls pad electrical characteristics: input enable (IE), output disable (OD), pull-up/pull-down, drive
/// strength, schmitt trigger and slew rate. IE, OD and the pulls change what the rest of the chip sees
/// (<see cref="InputEnableMask"/>, <see cref="OutputDisableMask"/>, <see cref="PullUpMask"/>,
/// <see cref="PullDownMask"/>); drive strength, slew rate and schmitt are stored only — GPIO function is
/// handled by IO_BANK0.
/// </summary>
public sealed class PadsPeripheral : IMemoryMappedDevice
{
    private const uint VOLTAGE_SELECT = 0x000;    // 0=3.3V, 1=1.8V
    private const uint GPIO_FIRST     = 0x004;    // GPIO0
    // GPIO_LAST = GPIO_FIRST + 29*4 = 0x078

    // Store voltage select + up to 32 GPIO pads
    private uint _voltageSelect;
    private readonly uint[] _gpioRegs = new uint[32];

    public uint Size => 0x1000;

    // Reset values (SVD PADS_BANK0 / PADS_QSPI): GPIO0-29 0x56 = IE, DRIVE 4 mA, PDE, SCHMITT; SWCLK 0xDA (OD, IE,
    // 4 mA, PUE, SCHMITT); SWD 0x5A; in the QSPI bank SCLK 0x56, SD0-SD3 0x52 and SS 0x5A.
    private const uint DEFAULT_PAD = 0x56;

    // PADS_BANK0_GPIOn: OD(7) IE(6) DRIVE(5:4) PUE(3) PDE(2) SCHMITT(1) SLEWFAST(0).
    private const int PUE_BIT = 3, PDE_BIT = 2, IE_BIT = 6, OD_BIT = 7;

    /// <summary>Pads whose pull-up is enabled — the level a floating input settles at.</summary>
    public uint PullUpMask { get; private set; }

    /// <summary>Pads whose pull-down is enabled.</summary>
    public uint PullDownMask { get; private set; }

    /// <summary>Pads whose input buffer is enabled (IE). A disabled pad reads as 0 to everything on the chip.</summary>
    public uint InputEnableMask { get; private set; }

    /// <summary>Pads whose output driver is forced off (OD), which wins over any output enable.</summary>
    public uint OutputDisableMask { get; private set; }

    /// <summary>Raised with a mask of the pads whose IE, OD or pull configuration changed.</summary>
    public event Action<uint>? PadsChanged;

    public PadsPeripheral(bool qspi = false)
    {
        for (int i = 0; i < _gpioRegs.Length; i++)
            _gpioRegs[i] = DEFAULT_PAD;
        if (qspi)
        {
            // GPIO_QSPI_SCLK, SD0-SD3, SS are indices 0-5
            for (var i = 1; i <= 4; i++) _gpioRegs[i] = 0x52;
            _gpioRegs[5] = 0x5A;
        }
        else
        {
            _gpioRegs[30] = 0xDA;   // SWCLK
            _gpioRegs[31] = 0x5A;   // SWD
        }
        for (uint i = 0; i < _gpioRegs.Length; i++) RefreshPulls(i);
    }

    private void RefreshPulls(uint idx)
    {
        if (idx >= 32) return;
        var bit = 1u << (int)idx;
        PullUpMask   = (_gpioRegs[idx] & (1u << PUE_BIT)) != 0 ? PullUpMask   | bit : PullUpMask   & ~bit;
        PullDownMask = (_gpioRegs[idx] & (1u << PDE_BIT)) != 0 ? PullDownMask | bit : PullDownMask & ~bit;
        InputEnableMask   = (_gpioRegs[idx] & (1u << IE_BIT)) != 0 ? InputEnableMask   | bit : InputEnableMask   & ~bit;
        OutputDisableMask = (_gpioRegs[idx] & (1u << OD_BIT)) != 0 ? OutputDisableMask | bit : OutputDisableMask & ~bit;
    }

    public uint ReadWord(uint address)
    {
        if (address == VOLTAGE_SELECT) return _voltageSelect;
        if (address >= GPIO_FIRST && address < GPIO_FIRST + 32 * 4)
        {
            var idx = (address - GPIO_FIRST) >> 2;
            return _gpioRegs[idx];
        }
        return 0;
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (address == VOLTAGE_SELECT) { _voltageSelect = value & 1; return; }
        if (address >= GPIO_FIRST && address < GPIO_FIRST + 32 * 4)
        {
            var idx = (address - GPIO_FIRST) >> 2;
            var changed = (_gpioRegs[idx] ^ value) & (1u << PUE_BIT | 1u << PDE_BIT | 1u << IE_BIT | 1u << OD_BIT);
            _gpioRegs[idx] = value & 0xFF;
            RefreshPulls(idx);
            if (changed != 0) PadsChanged?.Invoke(1u << (int)idx);
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

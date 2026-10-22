namespace RP2040.Peripherals.PinLevel;

/// <summary>
/// A serial peripheral's pad-facing side, in pin-level mode. IO_BANK0 owns the pin routing: when
/// firmware selects the SPI (1), UART (2) or I2C (3) FUNCSEL on a GPIO, that GPIO's pad output
/// (level + output-enable) comes from the matching <see cref="IPadFunction"/> role and its pad input is
/// delivered back through <see cref="SetInput"/>. Roles are the peripheral's own signal indices:
/// <list type="bullet">
/// <item>I2C: 0 = SDA, 1 = SCL (both open-drain: output-enable on with level 0 pulls low, off releases).</item>
/// <item>SPI: 0 = RX (MISO), 1 = CSn, 2 = SCK, 3 = TX (MOSI).</item>
/// <item>UART: 0 = TX, 1 = RX, 2 = CTS, 3 = RTS.</item>
/// </list>
/// </summary>
public interface IPadFunction
{
    /// <summary>True while the function actively drives the pad for <paramref name="role"/>.</summary>
    bool GetOutputEnable(int role);

    /// <summary>Driven level for <paramref name="role"/> (only meaningful while output-enabled).</summary>
    bool GetOutputLevel(int role);

    /// <summary>Pad input for <paramref name="role"/> (after IO_BANK0's INOVER). Called on every input edge
    /// and whenever the function is newly routed to a pad, so it always reflects the current level.</summary>
    void SetInput(int role, bool level);

    /// <summary>Set by IO_BANK0 on registration. The function invokes it with (role, clk_sys cycle)
    /// right after its drive for that role changed, so IO_BANK0 can refresh the pad's read-back and raise
    /// <c>PadChanged</c> for an attached external device. Must be called after the function's state is
    /// consistent: the external device may answer synchronously through <c>SetExternalInput</c>.</summary>
    Action<int, long>? DriveChanged { get; set; }
}

/// <summary>The fixed FUNCSEL/pad mapping of the RP2040's serial functions (RP2040 datasheet 2.19.2; the same
/// pattern as the RP2350's).</summary>
public static class PadFunctions
{
    public const int FuncSpi = 1, FuncUart = 2, FuncI2c = 3;

    public const int I2cSda = 0, I2cScl = 1;
    public const int SpiRx = 0, SpiCsn = 1, SpiSck = 2, SpiTx = 3;
    public const int UartTx = 0, UartRx = 1, UartCts = 2, UartRts = 3;

    /// <summary>Maps <paramref name="pin"/> under <paramref name="funcsel"/> to the (block, role) it carries:
    /// SPI block = (pin/8)%2, role = pin%4; UART block = ((pin+4)/8)%2, role = pin%4 (GPIO 0-3 UART0, 4-11 UART1, 12-19 UART0, 20-27 UART1, 28-29 UART0); I2C block = (pin/2)%2,
    /// role = pin%2. False for any other FUNCSEL.</summary>
    public static bool TryMap(int funcsel, int pin, out int block, out int role)
    {
        switch (funcsel)
        {
            case FuncSpi:  block = (pin >> 3) & 1; role = pin & 3; return true;
            case FuncUart: block = ((pin + 4) >> 3) & 1; role = pin & 3; return true;   // UART0 on 0-3, 12-19, 28-29; UART1 on 4-11, 20-27
            case FuncI2c:  block = (pin >> 1) & 1; role = pin & 1; return true;
            default:       block = role = 0; return false;
        }
    }
}

/// <summary>
/// The serial blocks that can drive and sample the GPIO pads themselves (pin-level mode) instead of exchanging
/// whole transactions through callbacks. Same bit values as RP2350Sharp's <c>PinLevelBus</c> and the
/// <c>ST_RP2040_BUS_*</c> flags of the C ABI.
/// </summary>
[Flags]
public enum PinLevelBus
{
    None = 0,
    /// <summary>I2C0/I2C1 as DW_apb_i2c masters on SDA/SCL (open-drain).</summary>
    I2c = 1,
    /// <summary>SPI0/SPI1 as PL022 masters on SCK/MOSI/CSn, sampling MISO.</summary>
    Spi = 2,
    /// <summary>UART0/UART1 on TX/RX.</summary>
    Uart = 4,
    All = I2c | Spi | Uart,
}

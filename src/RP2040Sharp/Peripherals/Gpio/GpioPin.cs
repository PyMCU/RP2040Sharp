namespace RP2040.Peripherals.Gpio;

/// <summary>
/// Represents a single GPIO pin on the RP2040.
/// Direction and output value are driven by the SIO peripheral;
/// input value is exposed here for external connection.
/// </summary>
public sealed class GpioPin
{
    private readonly int _pinIndex;
    private readonly Sio.SioPeripheral _sio;
    private readonly IoBank0Peripheral? _ioBank0;

    internal GpioPin(int pinIndex, Sio.SioPeripheral sio, IoBank0Peripheral? ioBank0 = null)
    {
        _pinIndex = pinIndex;
        _sio = sio;
        _ioBank0 = ioBank0;
    }

    /// <summary>Pin is configured as output (SIO GPIO_OE bit is set).</summary>
    public bool IsOutput => (_sio.GpioOe & (1u << _pinIndex)) != 0;

    /// <summary>
    /// Pin is assigned to a PIO state machine (FUNCSEL = 6 for PIO0 or 7 for PIO1).
    /// PIO-driven pins are configured via IO_BANK0 FUNCSEL, not SIO GPIO_OE, so
    /// <see cref="IsOutput"/> is <c>false</c> for PIO pins even when the SM drives them.
    /// </summary>
    public bool IsPioOutput => _ioBank0 is not null && (_ioBank0.GetFuncSel(_pinIndex) is 6 or 7);

    /// <summary>Current output level driven by software (SIO GPIO_OUT).</summary>
    public bool OutputValue => (_sio.GpioOut & (1u << _pinIndex)) != 0;

    /// <summary>
    /// Digital level seen by the processor (combines output + external input).
    /// When the pin is an output this matches <see cref="OutputValue"/>;
    /// when it is an input it reflects the value injected via <see cref="ForceInput"/>.
    /// </summary>
    public bool DigitalValue => IsOutput
        ? OutputValue
        : ((_sio.GpioIn) & (1u << _pinIndex)) != 0;

    /// <summary>
    /// Inject an external signal level into this pin (simulates a physical connection).
    /// Only effective when the pin is configured as an input.
    /// Notifies IoBank0 to trigger edge/level GPIO interrupts.
    /// </summary>
    public void ForceInput(bool high)
    {
        var mask = 1u << _pinIndex;
        _sio.DriveGpioIn(high ? mask : 0u, mask);
        _ioBank0?.UpdatePinInput(_pinIndex, high);
    }
}

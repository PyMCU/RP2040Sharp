using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Sio;

namespace RP2040.Peripherals.Gpio;

/// <summary>
/// IO_BANK0 peripheral (base 0x40014000).
/// Each GPIO pin has a STATUS (RO) and CTRL (RW) register pair at offsets n*8 and n*8+4.
/// FUNCSEL bits [4:0] of CTRL select the peripheral that drives/reads the pin.
/// Supports IRQ edge/level detection and PROC0_INTE/INTF/INTS interrupt bank.
/// </summary>
public sealed class IoBank0Peripheral : IMemoryMappedDevice
{
    private const int GPIO_COUNT = 30;

    /// <summary>Raised on each pad input transition (pin, new level), after the edge is latched.
    /// The machine routes these to peripherals that consume pin inputs (e.g. the PWM B pins).</summary>
    public Action<int, bool>? OnInputChanged;

    // Register layout offsets
    private const uint GPIO_CTRL_LAST  = 0x0EC;  // last byte of GPIO pair area
    private const uint INTR_BASE       = 0x0F0;  // INTR0-3 raw interrupt (write 1 to clear edge)
    private const uint PROC0_INTE_BASE = 0x100;  // PROC0_INTE0-3
    private const uint PROC0_INTF_BASE = 0x110;  // PROC0_INTF0-3
    private const uint PROC0_INTS_BASE = 0x120;  // PROC0_INTS0-3 (RO)
    private const uint PROC1_INTE_BASE = 0x130;  // PROC1 (single-core: store only)
    private const uint PROC1_INTF_BASE = 0x140;
    private const uint PROC1_INTS_BASE = 0x150;

    // IRQ event bits per pin (4 bits per pin in INTR registers)
    private const uint IRQ_LEVEL_LOW  = 1u << 0;
    private const uint IRQ_LEVEL_HIGH = 1u << 1;
    private const uint IRQ_EDGE_LOW   = 1u << 2;
    private const uint IRQ_EDGE_HIGH  = 1u << 3;

    // CTRL field masks
    private const uint FUNCSEL_MASK = 0x1F;
    private const uint FUNCSEL_SIO  = 5;
    private const uint FUNCSEL_PIO0 = 6;
    private const uint FUNCSEL_PIO1 = 7; // RP2040 has two PIO blocks: PIO0=6, PIO1=7
    private const uint FUNCSEL_PWM  = 4;

    /// <summary>
    /// The PWM block, so a pad muxed to FUNCSEL 4 reports the channel level the slice is driving.
    /// Without it a running PWM was invisible at the pin: pwm/pwm_fade.py faded an LED that never
    /// changed state as far as any pad observer could tell.
    /// </summary>
    public Pwm.PwmPeripheral? Pwm { get; set; }

    /// <summary>GPIO n drives slice (n >> 1) & 7, channel B on odd pins (datasheet §4.5.2).</summary>
    private static (int slice, bool channelB) PwmChannelFor(int pin) => ((pin >> 1) & 7, (pin & 1) != 0);

    // IO_IRQ_BANK0 = hardware IRQ 13
    private const int IO_IRQ_BANK0 = 13;

    private readonly CortexM0Plus? _cpu;
    private readonly SioPeripheral _sio;

    private readonly uint[] _ctrl      = new uint[GPIO_COUNT];
    private readonly bool[] _gpioInput = new bool[GPIO_COUNT];  // current input state
    private readonly uint[] _intrEdge  = new uint[GPIO_COUNT];  // edge IRQ bits per pin (bits 2-3)

    private readonly uint[] _proc0Inte = new uint[4];
    private readonly uint[] _proc0Intf = new uint[4];
    private readonly uint[] _proc1Inte = new uint[4];
    private readonly uint[] _proc1Intf = new uint[4];

    // Per-PIO-block pad output (level) and direction (output-enable), fed by the machine
    // from each PioPeripheral's WriteGpioPins/WriteGpioDirs callbacks via SetPioOut/SetPioDirs.
    // Lets a GPIO muxed to PIO0/1 reflect the state machine's driven level on the pad —
    // the function-mux level a circuit host reads via GetPadOutputLevel.
    private readonly uint[] _pioOut = new uint[2];
    private readonly uint[] _pioOe  = new uint[2];

    /// <summary>Updates the pad output level driven by PIO <paramref name="block"/> (0-1).
    /// <paramref name="value"/> carries the intended pin levels; <paramref name="mask"/>
    /// the pins this block drives.</summary>
    private void InvalidateInputWord() => _inputWordValid = false;

    public void SetPioOut (int block, uint value, uint mask)
    {
        _pioOut[block] = (_pioOut[block] & ~mask) | (value & mask);
        NotifyPads(mask);
    }

    /// <summary>Updates the output-enable (pin direction) driven by PIO <paramref name="block"/>.</summary>
    public void SetPioDirs (int block, uint value, uint mask)
    {
        _pioOe[block] = (_pioOe[block] & ~mask) | (value & mask);
        NotifyPads(mask);
    }

    // ── External-device pin seam (used by board add-ons such as the Pico W's CYW43439) ──
    // An off-chip device wired to GPIO pads observes the effective pad output through PadChanged + the
    // public GetPadOutputLevel/Enable, and drives a pad's input back through SetExternalInput. Generic
    // (any external peripheral can use it) and costs nothing unless something subscribes.

    /// <summary>Raised with a pin index whose effective pad output (level or output-enable) changed.
    /// Lets an attached external device sample edges (e.g. a bit-banged SPI clock) without polling.</summary>
    public event Action<int>? PadChanged;

    private readonly bool[] _lastPadLevel = new bool[GPIO_COUNT];
    private readonly bool[] _lastPadOe    = new bool[GPIO_COUNT];

    /// <summary>Re-evaluate the effective pad output for the pins in <paramref name="mask"/> and raise
    /// <see cref="PadChanged"/> for any that changed. A board calls this to forward SIO-driven pad
    /// changes (which originate outside this peripheral) to an attached external device. Cheap no-op
    /// when nothing subscribes to <see cref="PadChanged"/>.</summary>
    public void NotifyPads(uint mask)
    {
        _inputWordValid = false;
        if (PadChanged is null) return;
        while (mask != 0)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (pin >= GPIO_COUNT) continue;
            var lvl = GetPadOutputLevel(pin);
            var oe  = GetPadOutputEnable(pin);
            if (lvl == _lastPadLevel[pin] && oe == _lastPadOe[pin]) continue;
            _lastPadLevel[pin] = lvl;
            _lastPadOe[pin]    = oe;
            PadChanged(pin);
        }
    }

    /// <summary>The 32-bit word of effective pad input levels (GPIO 0-31): an output-enabled pad reads
    /// back its driven level; a non-driven pad reads the externally injected input. A board wires this
    /// into each <c>PioPeripheral.ReadGpioIn</c> so a half-duplex bus (PIO drives, then samples an
    /// off-chip device's reply on the same pin) reads the device, not the SM's stale output.</summary>
    public uint GetInputWord()
    {
        // Cached: the PIO asks for this on every instruction that reads pins, and the Pico W's gSPI is
        // bit-banged PIO, so rebuilding all 30 pads each time dominated the run. The cache is dropped
        // whenever anything that feeds it moves — and skipped entirely while a pad is muxed to PWM,
        // whose level changes with the counter rather than with a register write.
        if (_inputWordValid && _pwmMuxedPins == 0) return _inputWord;
        var word = BuildInputWord();
        _inputWord = word;
        _inputWordValid = true;
        return word;
    }

    private uint _inputWord;
    private bool _inputWordValid;

    /// <summary>Pads currently muxed to PWM (FUNCSEL 4), whose level is time-varying, not write-driven.</summary>
    private uint _pwmMuxedPins;

    /// <summary>Pads muxed to each function, kept in step with FUNCSEL writes.</summary>
    private uint _sioPins, _pio0Pins, _pio1Pins;

    /// <summary>Levels driven onto pads from outside the chip, as a bitmask of <see cref="_gpioInput"/>.</summary>
    private uint _externalInput;

    /// <summary>
    /// The pad input word, built from whole-word masks rather than a per-pin walk. The PIO asks for
    /// this constantly while bit-banging, and every clock edge invalidates the cache, so the rebuild
    /// itself has to be cheap: looping 30 pins through the function-select switch made this 16% of a
    /// WiFi transfer on its own.
    /// </summary>
    private uint BuildInputWord()
    {
        var sioDriven  = _sio.GpioOe   & _sioPins;
        var pio0Driven = _pioOe[0]     & _pio0Pins;
        var pio1Driven = _pioOe[1]     & _pio1Pins;
        var w = (_sio.GpioOut & sioDriven) | (_pioOut[0] & pio0Driven) | (_pioOut[1] & pio1Driven);
        var driven = sioDriven | pio0Driven | pio1Driven;

        // PWM pads are level-by-counter, so they keep the slow path — and there are rarely any.
        if (_pwmMuxedPins != 0)
        {
            var rest = _pwmMuxedPins;
            while (rest != 0)
            {
                var pin = System.Numerics.BitOperations.TrailingZeroCount(rest);
                rest &= rest - 1;
                if (!GetPadOutputEnable(pin)) continue;
                driven |= 1u << pin;
                if (GetPadOutputLevel(pin)) w |= 1u << pin;
            }
        }

        // Undriven pads take their level from SIO's view, which is the one that knows about the pad's
        // pull resistor — _externalInput alone only holds what something outside actually drives, so a
        // floating input with a pull-up read 0. Two sources of truth for the same pin is how the PIO
        // came to see pulled-up pins as low the moment it started reading pads through here.
        return w | (_sio.GpioIn & ~driven & GpioMask);
    }

    private const uint GpioMask = (1u << GPIO_COUNT) - 1;

    /// <summary>Drive a pad's input from an off-chip device (the pad's external connection). Identical to
    /// <see cref="UpdatePinInput"/> but named for the external-device direction.</summary>
    public void SetExternalInput(int pin, bool level) => UpdatePinInput(pin, level);

    /// <summary>
    /// Returns the FUNCSEL value [4:0] for <paramref name="pin"/>.
    /// Key values: 5 = SIO, 6 = PIO0, 7 = PIO1, 31 = NULL (hi-Z / default).
    /// </summary>
    public uint GetFuncSel(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return 31u;
        return _ctrl[pin] & FUNCSEL_MASK;
    }

    /// <summary>The level the pad is driven to after the GPIO function mux. SIO, PWM and the
    /// two PIO functions are modelled; other peripheral functions report <c>false</c>. Pairs with
    /// <see cref="GetPadOutputEnable"/>.</summary>
    public bool GetPadOutputLevel(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return false;
        var funcsel = _ctrl[pin] & FUNCSEL_MASK;
        if (funcsel == FUNCSEL_SIO)
            return (_sio.GpioOut & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PIO0 || funcsel == FUNCSEL_PIO1)
            return (_pioOut[funcsel - FUNCSEL_PIO0] & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PWM && Pwm is { } pwm)
        {
            var (slice, channelB) = PwmChannelFor(pin);
            return pwm.GetChannelOutput(slice, channelB);
        }
        return false;
    }

    /// <summary>True if the pad is actively driven (output-enabled) through the function mux.</summary>
    public bool GetPadOutputEnable(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return false;
        var funcsel = _ctrl[pin] & FUNCSEL_MASK;
        if (funcsel == FUNCSEL_SIO)
            return (_sio.GpioOe & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PIO0 || funcsel == FUNCSEL_PIO1)
            return (_pioOe[funcsel - FUNCSEL_PIO0] & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PWM && Pwm is { } pwm)
            return pwm.IsSliceEnabled(PwmChannelFor(pin).slice);
        return false;
    }

    public uint Size => 0x160;

    public IoBank0Peripheral(SioPeripheral sio, CortexM0Plus? cpu = null)
    {
        _sio = sio;
        _cpu = cpu;
        // Default FUNCSEL=31 (NULL / hi-Z) for all pins
        Array.Fill(_ctrl, 0x1Fu);
    }

    // ── GPIO input update ────────────────────────────────────────────

    /// <summary>
    /// Notify that a GPIO input pin changed value. This detects edges and
    /// updates INTR edge bits, then fires the NVIC interrupt if enabled.
    /// </summary>
    public void UpdatePinInput(int pin, bool value)
    {
        if (pin < 0 || pin >= GPIO_COUNT) return;

        var old = _gpioInput[pin];
        _gpioInput[pin] = value;
        if (old != value)
        {
            var bit = 1u << pin;
            _externalInput = value ? _externalInput | bit : _externalInput & ~bit;
            InvalidateInputWord();
        }

        // The pad input buffer feeds SIO GPIO_IN regardless of the pin's function select, so a value an
        // off-chip device drives onto a pad (e.g. the CYW43439 raising its DATA-line host-wake on GPIO24,
        // which is muxed to PIO) is readable by the CPU through gpio_get. Mirror it into SIO.
        _sio.SetGpioExternalIn(pin, value);

        if (old == value) return;  // no edge → no IRQ to (re-)evaluate
        if (value) _intrEdge[pin] |= IRQ_EDGE_HIGH;
        else       _intrEdge[pin] |= IRQ_EDGE_LOW;

        // Fan out the edge to peripherals with pin inputs (PWM B-pin gating/edge-count DIVMODEs).
        OnInputChanged?.Invoke(pin, value);

        // Only run the (whole-bank) interrupt scan when this pin actually has an interrupt enabled or
        // forced. A bit-banged input with no GPIO IRQ — e.g. the CYW43 gSPI DATA line toggling per bit —
        // would otherwise pay a full bank scan on every edge; skipping it is the single biggest saving
        // on the WiFi/BLE data path. (The edge is still latched above for INTR.)
        var reg = pin >> 3;
        var nibble = 0xFu << ((pin & 7) * 4);
        if (((_proc0Inte[reg] | _proc0Intf[reg] | _proc1Inte[reg] | _proc1Intf[reg]) & nibble) != 0)
            CheckInterrupts();
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        if (address <= GPIO_CTRL_LAST)
        {
            var pinPair = address >> 3;
            if (pinPair >= GPIO_COUNT) return 0;
            return (address & 4) != 0 ? _ctrl[pinPair] : ReadStatus((int)pinPair);
        }

        if (address >= INTR_BASE && address < PROC0_INTE_BASE)
            return BuildIntr((int)((address - INTR_BASE) >> 2));

        if (address >= PROC0_INTE_BASE && address < PROC0_INTF_BASE)
            return _proc0Inte[(address - PROC0_INTE_BASE) >> 2];

        if (address >= PROC0_INTF_BASE && address < PROC0_INTS_BASE)
            return _proc0Intf[(address - PROC0_INTF_BASE) >> 2];

        if (address >= PROC0_INTS_BASE && address < PROC1_INTE_BASE)
        {
            var reg = (int)((address - PROC0_INTS_BASE) >> 2);
            return (BuildIntr(reg) | _proc0Intf[reg]) & _proc0Inte[reg];
        }

        if (address >= PROC1_INTE_BASE && address < PROC1_INTF_BASE)
            return _proc1Inte[(address - PROC1_INTE_BASE) >> 2];

        if (address >= PROC1_INTF_BASE && address < PROC1_INTS_BASE)
            return _proc1Intf[(address - PROC1_INTF_BASE) >> 2];

        if (address >= PROC1_INTS_BASE && address < PROC1_INTS_BASE + 0x10)
        {
            var reg = (int)((address - PROC1_INTS_BASE) >> 2);
            return (BuildIntr(reg) | _proc1Intf[reg]) & _proc1Inte[reg];
        }

        return 0;
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (address <= GPIO_CTRL_LAST)
        {
            var pinPair = address >> 3;
            if (pinPair >= GPIO_COUNT) return;
            if ((address & 4) != 0)
            {
                _ctrl[pinPair] = value;
                var bit = 1u << (int)pinPair;
                var fn = value & FUNCSEL_MASK;
                _pwmMuxedPins = fn == FUNCSEL_PWM  ? _pwmMuxedPins | bit : _pwmMuxedPins & ~bit;
                _sioPins      = fn == FUNCSEL_SIO  ? _sioPins      | bit : _sioPins      & ~bit;
                _pio0Pins     = fn == FUNCSEL_PIO0 ? _pio0Pins     | bit : _pio0Pins     & ~bit;
                _pio1Pins     = fn == FUNCSEL_PIO1 ? _pio1Pins     | bit : _pio1Pins     & ~bit;
                // Re-muxing a pad changes what it drives without SIO or the PIO moving, so the change
                // has to be announced here. It used to be caught incidentally, because every GPIO write
                // re-evaluated all 30 pads; with only the changed pins re-evaluated, nothing else would
                // ever notice a pin being switched to (or away from) its peripheral function.
                NotifyPads(bit);
            }
            // STATUS is read-only
            return;
        }

        if (address >= INTR_BASE && address < PROC0_INTE_BASE)
        {
            // Write 1 to clear edge IRQ bits
            var reg = (int)((address - INTR_BASE) >> 2);
            ClearEdgeBits(reg, value);
            return;
        }

        if (address >= PROC0_INTE_BASE && address < PROC0_INTF_BASE)
        {
            _proc0Inte[(address - PROC0_INTE_BASE) >> 2] = value;
            CheckInterrupts();
            return;
        }

        if (address >= PROC0_INTF_BASE && address < PROC0_INTS_BASE)
        {
            _proc0Intf[(address - PROC0_INTF_BASE) >> 2] = value;
            CheckInterrupts();
            return;
        }

        if (address >= PROC1_INTE_BASE && address < PROC1_INTF_BASE)
        {
            _proc1Inte[(address - PROC1_INTE_BASE) >> 2] = value;
            return;
        }

        if (address >= PROC1_INTF_BASE && address < PROC1_INTS_BASE)
        {
            _proc1Intf[(address - PROC1_INTF_BASE) >> 2] = value;
            return;
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

    // ── Private helpers ──────────────────────────────────────────────

    private uint ReadStatus(int pin)
    {
        var status = 0u;
        var funcsel = _ctrl[pin] & FUNCSEL_MASK;
        if (funcsel == FUNCSEL_SIO)
        {
            if ((_sio.GpioOe & (1u << pin)) != 0) status |= 1u << 13;  // OETOPAD
            if ((_sio.GpioOut & (1u << pin)) != 0) status |= 1u << 9;  // OUTTOPAD
        }
        if (_gpioInput[pin]) status |= (1u << 17) | (1u << 19);  // INFROMPAD + INTOPERI
        return status;
    }

    /// <summary>
    /// Build INTR register N (8 GPIOs per register, 4 bits each).
    /// LEVEL bits computed from current input; EDGE bits from stored state.
    /// </summary>
    private uint BuildIntr(int reg)
    {
        var result = 0u;
        for (var i = 0; i < 8; i++)
        {
            var pin = reg * 8 + i;
            if (pin >= GPIO_COUNT) break;

            uint bits = 0;
            bits |= !_gpioInput[pin] ? IRQ_LEVEL_LOW  : 0u;
            bits |= _gpioInput[pin]  ? IRQ_LEVEL_HIGH : 0u;
            bits |= _intrEdge[pin] & (IRQ_EDGE_LOW | IRQ_EDGE_HIGH);
            result |= bits << (i * 4);
        }
        return result;
    }

    private void ClearEdgeBits(int reg, uint mask)
    {
        for (var i = 0; i < 8; i++)
        {
            var pin = reg * 8 + i;
            if (pin >= GPIO_COUNT) break;
            var bits = (mask >> (i * 4)) & 0xF;
            _intrEdge[pin] &= ~(bits & (IRQ_EDGE_LOW | IRQ_EDGE_HIGH));
        }
        CheckInterrupts();
    }

    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        var active = false;
        for (var reg = 0; reg < 4 && !active; reg++)
            active = ((BuildIntr(reg) | _proc0Intf[reg]) & _proc0Inte[reg]) != 0;
        _cpu.SetInterrupt(IO_IRQ_BANK0, active);
    }
}

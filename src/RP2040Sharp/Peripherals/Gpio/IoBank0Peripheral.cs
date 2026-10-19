using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.PinLevel;
using RP2040.Peripherals.Sio;

namespace RP2040.Peripherals.Gpio;

/// <summary>
/// IO_BANK0 peripheral (base 0x40014000).
/// Each GPIO pin has a STATUS (RO) and CTRL (RW) register pair at offsets n*8 and n*8+4.
/// FUNCSEL bits [4:0] of CTRL select the peripheral that drives/reads the pin; OUTOVER, OEOVER, INOVER and
/// IRQOVER override the output, output enable, peripheral input and interrupt signals (datasheet 2.19.6).
/// Supports IRQ edge/level detection and the PROC0 / PROC1 INTE/INTF/INTS interrupt banks.
/// </summary>
public sealed class IoBank0Peripheral : IMemoryMappedDevice
{
    private const int GPIO_COUNT = 30;

    /// <summary>Raised on each peripheral-side input transition (pin, new level, after INOVER), once the pad edge is
    /// latched. The machine routes these to peripherals that consume pin inputs (e.g. the PWM B pins).</summary>
    public Action<int, bool>? OnInputChanged;

    // Register layout offsets
    private const uint GPIO_CTRL_LAST  = 0x0EC;  // last byte of GPIO pair area
    private const uint INTR_BASE       = 0x0F0;  // INTR0-3 raw interrupt (write 1 to clear edge)
    private const uint PROC0_INTE_BASE = 0x100;  // PROC0_INTE0-3
    private const uint PROC0_INTF_BASE = 0x110;  // PROC0_INTF0-3
    private const uint PROC0_INTS_BASE = 0x120;  // PROC0_INTS0-3 (RO)
    private const uint PROC1_INTE_BASE = 0x130;  // PROC1_INTE0-3
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

    // ── Pin-level serial functions (opt-in) ──────────────────────────────────────
    // With pin-level buses enabled the machine registers the I2C/SPI/UART pad functions here. A GPIO whose FUNCSEL
    // selects one of them then takes its pad output from that function and feeds its pad input back to it; with nothing
    // registered (the default) these functions drive nothing and none of the paths below run.

    private readonly IPadFunction?[] _padFns = new IPadFunction?[6];   // [(funcsel-1)*2 + block]
    private int _padFnCount;

    /// <summary>Pads whose FUNCSEL selects a registered pad function (1 SPI, 2 UART, 3 I2C).</summary>
    private uint _fnPins;

    private readonly uint[] _fnPads = new uint[6];   // [(funcsel-1)*2 + block]: the pads routed to it, any role
    private readonly uint[] _fnRolePads = new uint[24];   // [((funcsel-1)*2 + block)*4 + role]

    /// <summary>
    /// Core-0 cycle (the pin-level engines' time base) of the most recent pad drive change made by a serial function.
    /// Valid inside <see cref="PadChanged"/> / <see cref="PadsChanged"/> while <see cref="FunctionDriving"/> is set: the
    /// handler then sees the edge at the engine's own cycle, not at the access or sweep that brought the engine there.
    /// </summary>
    public long LastPadEdgeCycle { get; private set; }

    /// <summary>True while IO_BANK0 reports a pad change made by a pin-level serial function (see <see cref="LastPadEdgeCycle"/>).</summary>
    public bool FunctionDriving { get; private set; }

    /// <summary>Wire a serial peripheral's pad side to the pins that select <paramref name="funcsel"/> (1 = SPI,
    /// 2 = UART, 3 = I2C) for peripheral <paramref name="block"/> (0 or 1).</summary>
    public void RegisterPadFunction(int funcsel, int block, IPadFunction fn)
    {
        if (funcsel is < 1 or > 3 || block is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(funcsel));
        var idx = (funcsel - 1) * 2 + block;
        if (_padFns[idx] is null) _padFnCount++;
        _padFns[idx] = fn;
        fn.DriveChanged = (role, cycle) => OnFunctionDrive(idx, role, cycle);
        RebuildFunctionPins();
        var pins = _fnPads[idx];
        if (pins != 0) { NotifyPads(pins); DeliverFunctionInputs(pins); }
    }

    /// <summary>Bit n set: GPIO n is routed (FUNCSEL = <paramref name="funcsel"/>, 1 = SPI, 2 = UART, 3 = I2C) to
    /// peripheral <paramref name="block"/>, in any role.</summary>
    public uint FunctionPads(int funcsel, int block) =>
        funcsel is < 1 or > 3 || block is < 0 or > 1 ? 0 : _fnPads[(funcsel - 1) * 2 + block];

    /// <summary>True when a GPIO is routed to the given function / block / role (e.g. SPI0's SCK).</summary>
    public bool IsFunctionRouted(int funcsel, int block, int role) =>
        funcsel is >= 1 and <= 3 && block is 0 or 1 && role is >= 0 and <= 3 &&
        _fnRolePads[((funcsel - 1) * 2 + block) * 4 + role] != 0;

    private void RebuildFunctionPins()
    {
        Array.Clear(_fnPads);
        Array.Clear(_fnRolePads);
        _fnPins = 0;
        if (_padFnCount == 0) return;
        for (var pin = 0; pin < GPIO_COUNT; pin++)
        {
            var funcsel = (int)(_ctrl[pin] & FUNCSEL_MASK);
            if (!PadFunctions.TryMap(funcsel, pin, out var b, out var r)) continue;
            var idx = (funcsel - 1) * 2 + b;
            _fnPads[idx] |= 1u << pin;
            _fnRolePads[idx * 4 + r] |= 1u << pin;
            if (_padFns[idx] is not null) _fnPins |= 1u << pin;
        }
    }

    private IPadFunction? FunctionAt(int pin, out int role)
    {
        role = 0;
        if (((_fnPins >> pin) & 1) == 0) return null;
        var funcsel = (int)(_ctrl[pin] & FUNCSEL_MASK);
        PadFunctions.TryMap(funcsel, pin, out var block, out role);
        return _padFns[(funcsel - 1) * 2 + block];
    }

    private void OnFunctionDrive(int idx, int role, long cycle)
    {
        var mask = _fnRolePads[idx * 4 + role] & _fnPins;
        if (mask == 0) return;
        var outer = FunctionDriving;
        var outerCycle = LastPadEdgeCycle;
        LastPadEdgeCycle = cycle;
        FunctionDriving = true;
        try { NotifyPads(mask); }   // the pad reads back what the function drives (before the external device reacts)
        finally { FunctionDriving = outer; LastPadEdgeCycle = outer ? outerCycle : cycle; }
    }

    /// <summary>Hand the functions on <paramref name="mask"/> their pad input as it is now (after INOVER).</summary>
    private void DeliverFunctionInputs(uint mask)
    {
        mask &= _fnPins;
        if (mask == 0) return;
        var peri = GetInputWord();
        while (mask != 0)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            FunctionAt(pin, out var role)?.SetInput(role, (peri & (1u << pin)) != 0);
        }
    }

    /// <summary>GPIO n drives slice (n >> 1) & 7, channel B on odd pins (datasheet §4.5.2).</summary>
    private static (int slice, bool channelB) PwmChannelFor(int pin) => ((pin >> 1) & 7, (pin & 1) != 0);

    // IO_IRQ_BANK0 = hardware IRQ 13
    private const int IO_IRQ_BANK0 = 13;

    private readonly CortexM0Plus? _cpu;
    private readonly SioPeripheral _sio;

    /// <summary>
    /// Core 1. IO_IRQ_BANK0 is wired to each core's own NVIC from its own PROCn_INTS (datasheet 2.19.3: six GPIO
    /// interrupts, "IO bank 0 to proc 0" and "IO bank 0 to proc 1" among them), so with this set the line of each
    /// core follows its own interrupt bank instead of both following PROC0.
    /// </summary>
    public CortexM0Plus? Cpu1 { get; set; }

    private Pads.PadsPeripheral? _pads;

    /// <summary>
    /// PADS_BANK0. Its input enable (IE) gates what every consumer sees on a pad, its output disable (OD)
    /// forces the driver off, and its pulls set the level of a pad nothing drives.
    /// </summary>
    public Pads.PadsPeripheral? Pads
    {
        get => _pads;
        set
        {
            _pads = value;
            if (value is not null) value.PadsChanged += NotifyPads;
        }
    }

    private readonly uint[] _ctrl      = new uint[GPIO_COUNT];
    private readonly uint[] _intrEdge  = new uint[GPIO_COUNT];  // edge IRQ bits per pin (bits 2-3)

    // GPIOn_CTRL overrides, one bit per pin, kept in step with CTRL writes. Each field is encoded
    // 0 normal, 1 invert, 2 force low (OEOVER: disable), 3 force high (OEOVER: enable).
    private uint _outInvert, _outLow, _outHigh;   // OUTOVER [9:8]
    private uint _oeInvert,  _oeLow,  _oeHigh;    // OEOVER [13:12]
    private uint _inInvert,  _inLow,  _inHigh;    // INOVER [17:16]
    private uint _irqInvert, _irqLow, _irqHigh;   // IRQOVER [29:28]
    private bool _outOverrides, _irqOverrides;

    // The pad level, and the level the peripherals see (after INOVER), as of the last evaluation: the
    // reference the next evaluation diffs against to find edges.
    private uint _padLevel, _periLevel;

    private static uint Override(uint value, uint invert, uint low, uint high) =>
        (value & ~(invert | low | high)) | (~value & invert) | high;

    private static bool Override(bool value, uint invert, uint low, uint high, uint bit) =>
        (high & bit) != 0 || ((low & bit) == 0 && ((invert & bit) != 0 ? !value : value));

    private static void SetOverride(uint bit, uint code, ref uint invert, ref uint low, ref uint high)
    {
        invert = code == 1 ? invert | bit : invert & ~bit;
        low    = code == 2 ? low    | bit : low    & ~bit;
        high   = code == 3 ? high   | bit : high   & ~bit;
    }

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

    /// <summary>Re-evaluate the pads in <paramref name="mask"/>: latch the GPIO interrupt edges and drive the
    /// peripheral inputs for any pad level that moved, and raise <see cref="PadChanged"/> for any pad whose
    /// effective output changed. SIO and the pads call it themselves; a board calls it to forward changes that
    /// originate outside this peripheral to an attached external device. The output half is a cheap no-op
    /// when nothing subscribes to <see cref="PadChanged"/>.</summary>
    public void NotifyPads(uint mask)
    {
        _inputWordValid = false;
        // Edge latching and peripheral inputs happen whoever is listening; only the output-change
        // notification below is skippable when nobody subscribes.
        EvaluatePads(mask);
        if (PadChanged is null && PadsChanged is null) return;
        uint changed = 0;
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
            changed |= 1u << pin;
            PadChanged?.Invoke(pin);
        }
        if (changed != 0) PadsChanged?.Invoke(changed);
    }

    /// <summary>Raised once per <see cref="NotifyPads"/> sweep with the mask of every pad whose effective
    /// output (level or output-enable) changed in it, after <see cref="PadChanged"/> has fired for each.
    /// Lets a host see a multi-pin write (a SIO set, a PIO <c>out pins</c>) as one batch.</summary>
    public event Action<uint>? PadsChanged;

    /// <summary>Re-reads the effective pad outputs as the baseline <see cref="NotifyPads"/> compares
    /// against. The baseline only advances while someone listens, so a listener that attaches mid-run
    /// calls this first to avoid being told about changes that happened before it was there.</summary>
    internal void SyncPadBaseline()
    {
        for (var pin = 0; pin < GPIO_COUNT; pin++)
        {
            _lastPadLevel[pin] = GetPadOutputLevel(pin);
            _lastPadOe[pin]    = GetPadOutputEnable(pin);
        }
    }

    /// <summary>
    /// The effective pad outputs of GPIO 0-29 as whole-word masks: which pads the chip drives and the
    /// level on each driven one, through the function mux (SIO, PIO0/1, PWM). Same answer as walking
    /// <see cref="GetPadOutputEnable"/> and <see cref="GetPadOutputLevel"/> pin by pin, without the walk.
    /// <paramref name="levels"/> is only meaningful where <paramref name="outputEnable"/> is set.
    /// </summary>
    public void GetPadOutputState(out uint outputEnable, out uint levels)
    {
        var sioDriven  = _sio.GpioOe & _sioPins;
        var pio0Driven = _pioOe[0]   & _pio0Pins;
        var pio1Driven = _pioOe[1]   & _pio1Pins;
        var w = (_sio.GpioOut & sioDriven) | (_pioOut[0] & pio0Driven) | (_pioOut[1] & pio1Driven);
        var driven = sioDriven | pio0Driven | pio1Driven;

        var rest = _pwmMuxedPins | _fnPins;
        while (rest != 0)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(rest);
            rest &= rest - 1;
            if (!GetPadOutputEnable(pin)) continue;
            driven |= 1u << pin;
            if (GetPadOutputLevel(pin)) w |= 1u << pin;
        }

        outputEnable = driven & GpioMask;
        levels       = w & GpioMask;
    }

    /// <summary>The 32-bit word of input levels the peripherals see (GPIO 0-31, INTOPERI: the pad after INOVER): an
    /// output-enabled pad reads back its driven level; a non-driven pad reads the externally injected input. A
    /// board wires this into each <c>PioPeripheral.ReadGpioIn</c> so a half-duplex bus (PIO drives, then samples an
    /// off-chip device's reply on the same pin) reads the device, not the SM's stale output.</summary>
    public uint GetInputWord()
    {
        var pad = GetPadWord();
        return _inInvert == 0 && _inLow == 0 && _inHigh == 0
            ? pad
            : Override(pad, _inInvert, _inLow, _inHigh) & GpioMask;
    }

    /// <summary>
    /// The pads themselves (INFROMPAD): what each pad's input buffer presents, before INOVER. SIO's GPIO_IN and
    /// the GPIO interrupt detectors read this; the peripherals read <see cref="GetInputWord"/>.
    /// </summary>
    internal uint GetPadWord()
    {
        // Cached: the PIO asks for this on every instruction that reads pins, and the Pico W's gSPI is
        // bit-banged PIO, so rebuilding all 30 pads each time dominated the run. The cache is dropped
        // whenever anything that feeds it moves — and skipped entirely while a pad is muxed to PWM,
        // whose level changes with the counter rather than with a register write.
        if (_inputWordValid && _pwmMuxedPins == 0) return _inputWord;
        var word = BuildPadWord();
        _inputWord = word;
        _inputWordValid = true;
        return word;
    }

    private uint _inputWord;
    private bool _inputWordValid;

    /// <summary>Pads currently muxed to PWM (FUNCSEL 4), whose level is time-varying, not write-driven.</summary>
    private uint _pwmMuxedPins;

    /// <summary>
    /// Pads currently muxed to PWM (FUNCSEL 4), one bit per GPIO. Their level follows the PWM counter, which
    /// never goes through <see cref="NotifyPads"/>, so neither <see cref="PadChanged"/> nor
    /// <see cref="PadsChanged"/> reports their edges: a host that watches such a pad polls it.
    /// </summary>
    public uint PwmMuxedPins => _pwmMuxedPins;

    /// <summary>Pads muxed to each function, kept in step with FUNCSEL writes.</summary>
    private uint _sioPins, _pio0Pins, _pio1Pins;

    /// <summary>
    /// The pad word, built from whole-word masks rather than a per-pin walk. The PIO asks for this
    /// constantly while bit-banging, and every clock edge invalidates the cache, so the rebuild
    /// itself has to be cheap: looping 30 pins through the function-select switch made this 16% of a
    /// WiFi transfer on its own.
    /// </summary>
    private uint BuildPadWord()
    {
        var oe   = (_sio.GpioOe  & _sioPins) | (_pioOe[0]  & _pio0Pins) | (_pioOe[1]  & _pio1Pins);
        var outv = (_sio.GpioOut & _sioPins) | (_pioOut[0] & _pio0Pins) | (_pioOut[1] & _pio1Pins);

        // PWM pads are level-by-counter, so they keep the slow path — and there are rarely any. Pads muxed to a
        // pin-level serial function ask it (a handful at most).
        if ((_pwmMuxedPins | _fnPins) != 0)
        {
            var rest = _pwmMuxedPins | _fnPins;
            while (rest != 0)
            {
                var pin = System.Numerics.BitOperations.TrailingZeroCount(rest);
                rest &= rest - 1;
                if (!PeripheralOutputEnable(pin)) continue;
                oe |= 1u << pin;
                if (PeripheralOutputLevel(pin)) outv |= 1u << pin;
            }
        }

        // OUTOVER / OEOVER act on the signal from whichever function owns the pad (or none), and the pad's own
        // OD then wins over any output enable.
        if (_outOverrides)
        {
            oe   = Override(oe,   _oeInvert,  _oeLow,  _oeHigh);
            outv = Override(outv, _outInvert, _outLow, _outHigh);
        }
        if (_pads is { } pads) oe &= ~pads.OutputDisableMask;
        oe &= GpioMask;

        // Undriven pads take the level of whatever the chip does not hold: a signal wired in from outside, or
        // the pad's pull resistor. Two sources of truth for the same pin is how the PIO came to see pulled-up
        // pins as low the moment it started reading pads through here.
        var pad = (outv & oe) | (_sio.UndrivenLevels & ~oe & GpioMask);

        // IE gates the input buffer: a disabled pad reads 0 to everything on the chip, its own output included.
        return _pads is { } p ? pad & p.InputEnableMask : pad;
    }

    /// <summary>The signal the pad's selected function drives out, before any register override (OUTFROMPERI).</summary>
    private bool PeripheralOutputLevel(int pin)
    {
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
        if (_fnPins != 0 && FunctionAt(pin, out var role) is { } fn) return fn.GetOutputLevel(role);
        return false;
    }

    /// <summary>The output enable the pad's selected function asserts, before any register override (OEFROMPERI).</summary>
    private bool PeripheralOutputEnable(int pin)
    {
        var funcsel = _ctrl[pin] & FUNCSEL_MASK;
        if (funcsel == FUNCSEL_SIO)
            return (_sio.GpioOe & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PIO0 || funcsel == FUNCSEL_PIO1)
            return (_pioOe[funcsel - FUNCSEL_PIO0] & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PWM && Pwm is { } pwm)
            return pwm.IsSliceEnabled(PwmChannelFor(pin).slice);
        if (_fnPins != 0 && FunctionAt(pin, out var role) is { } fn) return fn.GetOutputEnable(role);
        return false;
    }

    // Signals at the pad after the OUTOVER / OEOVER register overrides (STATUS.OUTTOPAD / OETOPAD).
    private bool OutToPad(int pin) =>
        Override(PeripheralOutputLevel(pin), _outInvert, _outLow, _outHigh, 1u << pin);

    private bool OeToPad(int pin) =>
        Override(PeripheralOutputEnable(pin), _oeInvert, _oeLow, _oeHigh, 1u << pin);

    private const uint GpioMask = (1u << GPIO_COUNT) - 1;

    /// <summary>Drive a pad's input from an off-chip device (the pad's external connection). Identical to
    /// <see cref="UpdatePinInput"/> but named for the external-device direction.</summary>
    public void SetExternalInput(int pin, bool level)
    {
        UpdatePinInput(pin, level);
        if ((uint)pin < GPIO_COUNT) _externalHeld |= 1u << pin;
    }

    /// <summary>Pads an off-chip device currently drives through <see cref="SetExternalInput"/>.</summary>
    private uint _externalHeld;

    /// <summary>
    /// Stops driving the pads in <paramref name="mask"/> from outside, as <see cref="SetExternalInput"/>
    /// started: the pad goes back to being undriven, so its pull resistor (or nothing) sets the level it
    /// reads, and the change is latched as an edge like any other input transition. Pads that were not
    /// held externally are left alone, and so are pads muxed to a PIO, which owns their level.
    /// </summary>
    public void ReleaseExternalInput(uint mask)
    {
        mask &= _externalHeld & GpioMask;
        _externalHeld &= ~mask;
        while (mask != 0)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            var fn = _ctrl[pin] & FUNCSEL_MASK;
            if (fn == FUNCSEL_PIO0 || fn == FUNCSEL_PIO1) continue;
            var bit = 1u << pin;
            _sio.ReleaseGpioExternal(bit);
            InvalidateInputWord();

            // Re-read the pad now that nothing holds it: EvaluatePads compares against _padLevel and
            // latches the edge like any other input transition. (This replaced the per-pin _gpioInput
            // shadow this branch was written against.)
            EvaluatePads(bit);
        }
    }

    /// <summary>
    /// Returns the FUNCSEL value [4:0] for <paramref name="pin"/>.
    /// Key values: 5 = SIO, 6 = PIO0, 7 = PIO1, 31 = NULL (hi-Z / default).
    /// </summary>
    public uint GetFuncSel(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return 31u;
        return _ctrl[pin] & FUNCSEL_MASK;
    }

    /// <summary>The level the pad is driven to after the GPIO function mux and the OUTOVER override. SIO, PWM
    /// and the two PIO functions are modelled as sources; other peripheral functions report <c>false</c> unless an
    /// override forces the level. Pairs with <see cref="GetPadOutputEnable"/>.</summary>
    public bool GetPadOutputLevel(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return false;
        return OutToPad(pin);
    }

    /// <summary>True if the pad is actively driven (output-enabled) through the function mux, the OEOVER override
    /// and the pad's own output disable.</summary>
    public bool GetPadOutputEnable(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return false;
        return OeToPad(pin) && (_pads is null || (_pads.OutputDisableMask & (1u << pin)) == 0);
    }

    public uint Size => 0x160;

    public IoBank0Peripheral(SioPeripheral sio, CortexM0Plus? cpu = null)
    {
        _sio = sio;
        _cpu = cpu;
        // Default FUNCSEL=31 (NULL / hi-Z) for all pins
        Array.Fill(_ctrl, 0x1Fu);
        // GPIO_IN is the pad, and SIO changing GPIO_OUT / GPIO_OE moves a pad, so edges on pins SIO drives
        // (a loop from an output back to the same pin's input) are seen here.
        sio.PadInputProvider = GetPadWord;
        // Only the pads muxed to SIO follow its output registers; a write to any other pin's bits moves nothing.
        sio.PadSink = mask =>
        {
            mask &= _sioPins;
            if (mask != 0) NotifyPads(mask);
        };
    }

    // ── GPIO input update ────────────────────────────────────────────

    /// <summary>
    /// Notify that a GPIO input pin changed value. The pad level is re-evaluated, which detects edges and
    /// updates INTR edge bits, then fires the NVIC interrupt if enabled.
    /// </summary>
    public void UpdatePinInput(int pin, bool value)
    {
        if (pin < 0 || pin >= GPIO_COUNT) return;

        // The pad input buffer feeds SIO GPIO_IN regardless of the pin's function select, so a value an
        // off-chip device drives onto a pad (e.g. the CYW43439 raising its DATA-line host-wake on GPIO24,
        // which is muxed to PIO) is readable by the CPU through gpio_get. Mirror it into SIO.
        _sio.SetGpioExternalIn(pin, value);
        InvalidateInputWord();
        EvaluatePads(1u << pin);
    }

    /// <summary>
    /// Latch the edges and notify the peripherals for every pad in <paramref name="mask"/> whose level moved since
    /// the last evaluation. Edges are detected on the pad itself, whoever moved it: SIO driving an output, a PIO
    /// state machine, a pull resistor, or an off-chip device.
    /// </summary>
    private void EvaluatePads(uint mask)
    {
        mask &= GpioMask;
        var pad = GetPadWord();
        var diff = (pad ^ _padLevel) & mask;
        if (diff != 0)
        {
            _padLevel ^= diff;
            var interesting = false;
            var rest = diff;
            while (rest != 0)
            {
                var pin = System.Numerics.BitOperations.TrailingZeroCount(rest);
                rest &= rest - 1;
                _intrEdge[pin] |= (pad & (1u << pin)) != 0 ? IRQ_EDGE_HIGH : IRQ_EDGE_LOW;

                // Only run the (whole-bank) interrupt scan when this pin actually has an interrupt enabled or
                // forced. A bit-banged input with no GPIO IRQ — e.g. the CYW43 gSPI DATA line toggling per bit —
                // would otherwise pay a full bank scan on every edge; skipping it is the single biggest saving
                // on the WiFi/BLE data path. (The edge is still latched above for INTR.)
                var reg = pin >> 3;
                var nibble = 0xFu << ((pin & 7) * 4);
                if (((_proc0Inte[reg] | _proc0Intf[reg] | _proc1Inte[reg] | _proc1Intf[reg]) & nibble) != 0)
                    interesting = true;
            }
            if (interesting || _irqOverrides) CheckInterrupts();
        }

        // What the peripherals see: the pad after INOVER. An INOVER change moves it with no pad edge at all.
        var peri = _inInvert == 0 && _inLow == 0 && _inHigh == 0
            ? pad
            : Override(pad, _inInvert, _inLow, _inHigh) & GpioMask;
        var pdiff = (peri ^ _periLevel) & mask;
        if (pdiff == 0) return;
        _periLevel ^= pdiff;
        if ((pdiff & _fnPins) != 0)
        {
            var fnDiff = pdiff & _fnPins;
            while (fnDiff != 0)
            {
                var pin = System.Numerics.BitOperations.TrailingZeroCount(fnDiff);
                fnDiff &= fnDiff - 1;
                FunctionAt(pin, out var role)?.SetInput(role, (peri & (1u << pin)) != 0);
            }
        }
        if (OnInputChanged is null) return;
        while (pdiff != 0)
        {
            var pin = System.Numerics.BitOperations.TrailingZeroCount(pdiff);
            pdiff &= pdiff - 1;
            OnInputChanged(pin, (peri & (1u << pin)) != 0);
        }
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
                // CTRL: IRQOVER [29:28], INOVER [17:16], OEOVER [13:12], OUTOVER [9:8], FUNCSEL [4:0]
                value &= 0x3003331F;
                var irqOverChanged = ((_ctrl[pinPair] ^ value) & 0x30000000) != 0;
                _ctrl[pinPair] = value;
                var bit = 1u << (int)pinPair;
                var fn = value & FUNCSEL_MASK;
                _pwmMuxedPins = fn == FUNCSEL_PWM  ? _pwmMuxedPins | bit : _pwmMuxedPins & ~bit;
                _sioPins      = fn == FUNCSEL_SIO  ? _sioPins      | bit : _sioPins      & ~bit;
                _pio0Pins     = fn == FUNCSEL_PIO0 ? _pio0Pins     | bit : _pio0Pins     & ~bit;
                _pio1Pins     = fn == FUNCSEL_PIO1 ? _pio1Pins     | bit : _pio1Pins     & ~bit;
                SetOverride(bit, (value >> 8)  & 3, ref _outInvert, ref _outLow, ref _outHigh);
                SetOverride(bit, (value >> 12) & 3, ref _oeInvert,  ref _oeLow,  ref _oeHigh);
                SetOverride(bit, (value >> 16) & 3, ref _inInvert,  ref _inLow,  ref _inHigh);
                SetOverride(bit, (value >> 28) & 3, ref _irqInvert, ref _irqLow, ref _irqHigh);
                _outOverrides = (_outInvert | _outLow | _outHigh | _oeInvert | _oeLow | _oeHigh) != 0;
                _irqOverrides = (_irqInvert | _irqLow | _irqHigh) != 0;
                var wasFn = (_fnPins & bit) != 0;
                if (_padFnCount != 0) RebuildFunctionPins();
                // Re-muxing a pad (or overriding it) changes what it drives and what it reads without SIO or
                // the PIO moving, so the change has to be announced here.
                NotifyPads(bit);
                // A function newly routed to the pad (or re-routed) learns the level it is on now.
                if ((_fnPins & bit) != 0 || wasFn) DeliverFunctionInputs(bit);
                // IRQOVER changes the interrupt line with no pad movement at all.
                if (irqOverChanged) CheckInterrupts();
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
            CheckInterrupts();
            return;
        }

        if (address >= PROC1_INTF_BASE && address < PROC1_INTS_BASE)
        {
            _proc1Intf[(address - PROC1_INTF_BASE) >> 2] = value;
            CheckInterrupts();
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

    /// <summary>GPIOn_STATUS (datasheet 2.19.6): the signals on both sides of each override.</summary>
    private uint ReadStatus(int pin)
    {
        var bit = 1u << pin;
        var status = 0u;
        if (PeripheralOutputLevel(pin)) status |= 1u << 8;    // OUTFROMPERI
        if (OutToPad(pin))              status |= 1u << 9;    // OUTTOPAD
        if (PeripheralOutputEnable(pin)) status |= 1u << 12;  // OEFROMPERI
        if (OeToPad(pin))               status |= 1u << 13;   // OETOPAD

        var pad = GetPadWord();
        if ((pad & bit) != 0) status |= 1u << 17;              // INFROMPAD
        var peri = Override(pad, _inInvert, _inLow, _inHigh);
        if ((peri & bit) != 0) status |= 1u << 19;             // INTOPERI

        // The pin's interrupt as PROC0 sees it, before and after IRQOVER
        var fromPad = (IrqPinMask(_proc0Inte, _proc0Intf, pad) & bit) != 0;
        if (fromPad) status |= 1u << 24;                       // IRQFROMPAD
        if (Override(fromPad, _irqInvert, _irqLow, _irqHigh, bit)) status |= 1u << 26;   // IRQTOPROC
        return status;
    }

    /// <summary>
    /// Build INTR register N (8 GPIOs per register, 4 bits each).
    /// LEVEL bits computed from the current pad level; EDGE bits from stored state.
    /// </summary>
    private uint BuildIntr(int reg) => BuildIntr(reg, GetPadWord());

    private uint BuildIntr(int reg, uint pad)
    {
        var result = 0u;
        for (var i = 0; i < 8; i++)
        {
            var pin = reg * 8 + i;
            if (pin >= GPIO_COUNT) break;

            var high = (pad & (1u << pin)) != 0;
            uint bits = 0;
            bits |= !high ? IRQ_LEVEL_LOW  : 0u;
            bits |= high  ? IRQ_LEVEL_HIGH : 0u;
            bits |= _intrEdge[pin] & (IRQ_EDGE_LOW | IRQ_EDGE_HIGH);
            result |= bits << (i * 4);
        }
        return result;
    }

    /// <summary>The pins that have at least one active, enabled (or forced) event for one processor: that
    /// processor's per-pin interrupt before IRQOVER.</summary>
    private uint IrqPinMask(uint[] inte, uint[] intf, uint pad)
    {
        var pins = 0u;
        for (var reg = 0; reg < 4; reg++)
        {
            var ints = (BuildIntr(reg, pad) | intf[reg]) & inte[reg];
            if (ints == 0) continue;
            for (var i = 0; i < 8; i++)
                if (((ints >> (i * 4)) & 0xF) != 0)
                    pins |= 1u << (reg * 8 + i);
        }
        return pins & GpioMask;
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

    private bool ProcInterruptActive(uint[] inte, uint[] intf)
    {
        var pad = GetPadWord();
        if (!_irqOverrides)
        {
            for (var reg = 0; reg < 4; reg++)
                if (((BuildIntr(reg, pad) | intf[reg]) & inte[reg]) != 0)
                    return true;
            return false;
        }
        // IRQOVER acts on each pin's interrupt signal before it is ORed into the processor's line
        return (Override(IrqPinMask(inte, intf, pad), _irqInvert, _irqLow, _irqHigh) & GpioMask) != 0;
    }

    /// <summary>
    /// Drives IO_IRQ_BANK0. With Core 1 attached each core's own NVIC line follows its own PROCn_INTS; without it
    /// the single line follows PROC0 and reaches both cores, as it did before the second core had an interrupt bank.
    /// </summary>
    private void CheckInterrupts()
    {
        if (_cpu is null) return;
        var proc0 = ProcInterruptActive(_proc0Inte, _proc0Intf);
        if (Cpu1 is null)
        {
            _cpu.SetInterrupt(IO_IRQ_BANK0, proc0);
            return;
        }
        _cpu.SetInterruptLocal(IO_IRQ_BANK0, proc0);
        Cpu1.SetInterruptLocal(IO_IRQ_BANK0, ProcInterruptActive(_proc1Inte, _proc1Intf));
    }
}

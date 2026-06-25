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
        uint w = 0;
        for (var p = 0; p < GPIO_COUNT; p++)   // RP2040 has 30 GPIOs; bits 30/31 don't exist
        {
            var level = GetPadOutputEnable(p) ? GetPadOutputLevel(p) : _gpioInput[p];
            if (level) w |= 1u << p;
        }
        return w;
    }

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

    /// <summary>The level the pad is driven to after the GPIO function mux. SIO and the
    /// two PIO functions are modelled; other peripheral functions report <c>false</c>
    /// (PWM is reproduced element-side via the PWM registers). Pairs with
    /// <see cref="GetPadOutputEnable"/>.</summary>
    public bool GetPadOutputLevel(int pin)
    {
        if ((uint)pin >= GPIO_COUNT) return false;
        var funcsel = _ctrl[pin] & FUNCSEL_MASK;
        if (funcsel == FUNCSEL_SIO)
            return (_sio.GpioOut & (1u << pin)) != 0;
        if (funcsel == FUNCSEL_PIO0 || funcsel == FUNCSEL_PIO1)
            return (_pioOut[funcsel - FUNCSEL_PIO0] & (1u << pin)) != 0;
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

        // The pad input buffer feeds SIO GPIO_IN regardless of the pin's function select, so a value an
        // off-chip device drives onto a pad (e.g. the CYW43439 raising its DATA-line host-wake on GPIO24,
        // which is muxed to PIO) is readable by the CPU through gpio_get. Mirror it into SIO.
        _sio.SetGpioExternalIn(pin, value);

        if (old == value) return;  // no edge → no IRQ to (re-)evaluate
        if (value) _intrEdge[pin] |= IRQ_EDGE_HIGH;
        else       _intrEdge[pin] |= IRQ_EDGE_LOW;

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
            if ((address & 4) != 0) _ctrl[pinPair] = value;
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

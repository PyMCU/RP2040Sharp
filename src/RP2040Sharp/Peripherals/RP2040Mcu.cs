using RP2040.Peripherals.I2c;
using RP2040.Peripherals.Spi;
using RP2040.Peripherals.Uart;
using SiliconTwin.Abstractions;

namespace RP2040.Peripherals;

/// <summary>
/// Adapter that presents an <see cref="RP2040Machine"/> through the shared SiliconTwin.Abstractions
/// contracts: <see cref="IMcu"/> plus the <see cref="IGpio"/>, <see cref="IPwmSource"/>,
/// <see cref="IAdcInput"/> and <see cref="IPeripheralMap"/> capabilities. A host written against those
/// interfaces drives a Pico exactly as it drives an AVR or any other emulated MCU.
/// <code>
/// var mcu = new RP2040Mcu();
/// mcu.Load(FirmwareImage.FromUf2(File.ReadAllBytes("blink.uf2")));
/// mcu.Reset();
/// long used = mcu.Run(125_000);   // 1 ms at 125 MHz
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose: the machine keeps doing the work and <see cref="Machine"/> stays available for
/// anything the contracts do not cover (USB, PIO, DMA, the CYW43 add-on).
/// </para>
/// <para>
/// Time. <see cref="Cycles"/> is Core 0's cycle count and keeps counting across <see cref="Reset"/> and
/// <see cref="Load"/> (which restart the core's own counter); only a <see cref="ResetKind.Power"/> reset
/// zeroes it. The clock is fixed at <see cref="RP2040Machine.CLK_HZ"/> because the machine does not model
/// clock reconfiguration, so <see cref="ClockChanged"/> is never raised.
/// </para>
/// </remarks>
public sealed class RP2040Mcu : IMcu, IGpio, IPwmSource, IAdcInput, IPeripheralMap
{
    /// <summary>ADC reference of the RP2040: ADC_AVDD, 3.3 V on a Pico.</summary>
    public const double AdcReferenceVoltage = 3.3;

    private const int PinCountValue = 30;
    private const int AdcMaxCode = 4095;

    private readonly RP2040Machine _machine;
    private readonly bool _ownsMachine;
    private readonly PinBank _bank;
    private readonly IReadOnlyList<IPinBank> _banks;
    private readonly IReadOnlyList<IUartPort> _uarts;
    private readonly IReadOnlyList<ISpiPort> _spis;
    private readonly IReadOnlyList<II2cPort> _i2cs;

    private long _cycleBase;
    private int _flashLength;
    private Func<int, double>? _readChannelVolts;

    /// <summary>
    /// Creates an adapter over a new <see cref="RP2040Machine"/> that it disposes with itself, with deferred peripheral
    /// ticks on (<see cref="RP2040Machine.DeferPeripheralTicks"/>: the same results as ticking every peripheral after
    /// every slice, faster).
    /// </summary>
    public RP2040Mcu() : this(new RP2040McuOptions()) { }

    /// <summary>Creates an adapter over a new machine built from <paramref name="options"/>, disposed with the adapter.</summary>
    public RP2040Mcu(RP2040McuOptions options)
        : this(new RP2040Machine(options.FlashSizeBytes, options.Bootrom, options.PinLevelBuses)
               { DeferPeripheralTicks = options.DeferPeripheralTicks }, ownsMachine: true) { }

    /// <summary>Creates an adapter over an existing machine, which the caller keeps owning.</summary>
    /// <param name="machine">The machine to drive.</param>
    public RP2040Mcu(RP2040Machine machine) : this(machine, ownsMachine: false) { }

    private RP2040Mcu(RP2040Machine machine, bool ownsMachine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _machine = machine;
        _ownsMachine = ownsMachine;
        _bank = new PinBank(this);
        _banks = [_bank];
        _uarts = [new UartPort(0, machine.Uart0), new UartPort(1, machine.Uart1)];
        _spis = [new SpiPort(0, machine.Spi0), new SpiPort(1, machine.Spi1)];
        _i2cs = [new I2cPort(0, machine.I2c0), new I2cPort(1, machine.I2c1)];
    }

    /// <summary>The wrapped machine, for everything the shared contracts do not cover.</summary>
    public RP2040Machine Machine => _machine;

    /// <summary>
    /// Upper bound, in instructions, on one slice of <see cref="Run"/>. Peripherals that advance on a tick
    /// (PIO, PWM, timers) move at slice boundaries, so lower it to interleave a circuit more finely and
    /// raise it for throughput. Defaults to 16384.
    /// </summary>
    public int MaxSliceInstructions { get; set; } = 16_384;

    // ── IMcu ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "rp2040";

    /// <inheritdoc />
    public long Cycles => _cycleBase + _machine.Cpu.Cycles;

    /// <inheritdoc />
    public uint ClockHz => RP2040Machine.CLK_HZ;

    /// <summary>Never raised: the machine runs at a fixed <see cref="RP2040Machine.CLK_HZ"/>.</summary>
    public event Action? ClockChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the wall-clock cycles that elapsed. With Core 1 running that is the longer of the two cores,
    /// while <see cref="Cycles"/> follows Core 0 alone, so the two can differ by the cores' skew.
    /// </remarks>
    public long Run(long cycleBudget)
    {
        var used = _machine.RunCycles(cycleBudget, MaxSliceInstructions);
        // Leave nothing owed: whatever the host reads or drives next sees the peripherals at the end of the run.
        if (used > 0 && _machine.DeferPeripheralTicks) _machine.SyncPeripherals();
        return used;
    }

    /// <inheritdoc />
    public long RunUntil(long absoluteCycle)
    {
        var remaining = absoluteCycle - Cycles;
        return remaining <= 0 ? 0 : Run(remaining);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Restarts both cores. With firmware loaded, Core 0 starts where <see cref="RP2040Machine.LoadFlash"/>
    /// starts it, bypassing the BootROM reset handler. Peripheral registers are not reset by the machine,
    /// so a <see cref="ResetKind.Power"/> reset differs from a soft one only in zeroing <see cref="Cycles"/>.
    /// </remarks>
    public void Reset(ResetKind kind = ResetKind.Soft)
    {
        var now = Cycles;
        if (_flashLength > 0) _machine.ResetToFlash(_flashLength);
        else _machine.Reset();
        _cycleBase = kind == ResetKind.Power ? 0 : now - _machine.Cpu.Cycles;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Supports <see cref="FirmwareFormat.Uf2"/> and <see cref="FirmwareFormat.Bin"/> (offset from the
    /// start of flash, 0x10000000); <see cref="FirmwareFormat.Auto"/> recognises a UF2 by its magic and
    /// otherwise takes the data as a raw binary. ELF and Intel HEX throw <see cref="NotSupportedException"/>.
    /// Like <see cref="RP2040Machine.LoadFlash"/> it points Core 0 at the image, so <see cref="Cycles"/>
    /// keeps counting across it.
    /// </remarks>
    /// <exception cref="InvalidDataException">The data is not a valid UF2 file for the RP2040.</exception>
    public void Load(in FirmwareImage image)
    {
        var format = image.Format;
        var data = image.Data.Span;
        if (format == FirmwareFormat.Auto)
            format = DetectFormat(data);

        switch (format)
        {
            case FirmwareFormat.Uf2:
            {
                var flash = RP2040Machine.Uf2ToFlash(data)
                            ?? throw new InvalidDataException("Not a valid RP2040 UF2 image.");
                LoadFlashImage(flash, 0);
                break;
            }
            case FirmwareFormat.Bin:
                LoadFlashImage(data, image.Offset);
                break;
            case FirmwareFormat.Elf:
                throw new NotSupportedException("ELF images are not supported; convert to UF2 or BIN first.");
            case FirmwareFormat.IntelHex:
                throw new NotSupportedException("Intel HEX images are not supported by the RP2040; use UF2 or BIN.");
            default:
                throw new NotSupportedException($"Unknown firmware format {format}.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _bank.Disarm();
        if (_ownsMachine) _machine.Dispose();
    }

    private static FirmwareFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
            return FirmwareFormat.Elf;
        if (data.Length >= 512 && data.Length % 512 == 0
            && data[0] == (byte)'U' && data[1] == (byte)'F' && data[2] == (byte)'2' && data[3] == (byte)'\n')
            return FirmwareFormat.Uf2;
        return FirmwareFormat.Bin;
    }

    private unsafe void LoadFlashImage(ReadOnlySpan<byte> data, uint offset)
    {
        var size = (long)_machine.Bus.FlashSize;
        if (offset + (long)data.Length > size)
            throw new ArgumentException($"Image does not fit in flash ({size / 1024} KB).");

        var now = Cycles;
        if (offset == 0)
        {
            _machine.LoadFlash(data);
        }
        else
        {
            // LoadFlash starts the core at the image, so hand it the whole image: what is already in flash
            // below the offset, then the new bytes.
            var combined = new byte[offset + data.Length];
            new ReadOnlySpan<byte>(_machine.Bus.PtrFlash, (int)offset).CopyTo(combined);
            data.CopyTo(combined.AsSpan((int)offset));
            _machine.LoadFlash(combined);
        }

        _flashLength = Math.Max(_flashLength, (int)(offset + data.Length));
        _cycleBase = now - _machine.Cpu.Cycles;
    }

    // ── IGpio ─────────────────────────────────────────────────────────

    /// <summary>One bank, <c>"GPIO"</c>, with the 30 user pins of IO_BANK0 (the QSPI pads are not exposed).</summary>
    public IReadOnlyList<IPinBank> Banks => _banks;

    // ── IPwmSource ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// A pin is routed to PWM when its FUNCSEL is 4. The frequency follows the slice's divider and TOP
    /// (halved in phase-correct mode, which counts up and down). The B pin of a slice in a gated or
    /// edge-count DIVMODE is an input, so it reports as routed but not enabled.
    /// </remarks>
    public bool TryGetPwm(int bank, int pin, out PwmInfo info)
    {
        info = default;
        if (bank != 0 || (uint)pin >= PinCountValue) return false;
        if (_machine.IoBank0.GetFuncSel(pin) != 4) return false;

        const uint SliceBytes = 0x14, OffCsr = 0x00, OffDiv = 0x04, OffCc = 0x0C, OffTop = 0x10;
        const uint CsrEn = 1u << 0, CsrPhCorrect = 1u << 1, CsrAInv = 1u << 2, CsrBInv = 1u << 3, CsrDivMode = 3u << 4;

        var pwm = _machine.Pwm;
        var slice = (pin >> 1) & 7;
        var isB = (pin & 1) == 1;
        var baseOffset = (uint)slice * SliceBytes;

        var csr = pwm.ReadWord(baseOffset + OffCsr);
        if ((csr & CsrEn) == 0 || (isB && (csr & CsrDivMode) != 0)) return true;

        var div = pwm.ReadWord(baseOffset + OffDiv);
        var top = pwm.ReadWord(baseOffset + OffTop);
        var cc = pwm.ReadWord(baseOffset + OffCc);
        var compare = isB ? cc >> 16 : cc & 0xFFFF;
        var invert = (csr & (isB ? CsrBInv : CsrAInv)) != 0;

        // DIV is 8.4 fixed point; an integer part of 0 means 1.
        var divInt = (int)((div >> 4) & 0xFF);
        if (divInt == 0) divInt = 1;
        var divisor = divInt + (div & 0xF) / 16.0;
        var period = top + 1.0;
        if ((csr & CsrPhCorrect) != 0) period *= 2;

        var raw = top > 0 ? Math.Clamp(compare / (top + 1.0), 0.0, 1.0) : 0.0;
        info = new PwmInfo(true, RP2040Machine.CLK_HZ / divisor / period, invert ? 1.0 - raw : raw);
        return true;
    }

    // ── IAdcInput ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public double ReferenceVolts => AdcReferenceVoltage;

    /// <inheritdoc />
    /// <remarks>
    /// Volts are converted to the 12-bit code (clamped to 0 to <see cref="ReferenceVolts"/>) when the ADC
    /// samples. Channels 0 to 3 are GPIO26 to GPIO29 and channel 4 is the temperature sensor; with no
    /// callback the machine's own defaults apply (0 V pins, the sensor reading 27 degrees C).
    /// </remarks>
    public Func<int, double>? ReadChannelVolts
    {
        get => _readChannelVolts;
        set
        {
            _readChannelVolts = value;
            _machine.Adc.ReadChannel = value is null ? null : channel => ToCode(value(channel));
        }
    }

    private static ushort ToCode(double volts)
    {
        // NaN fails both comparisons below, so it reads as 0 V.
        if (!(volts > 0)) return 0;
        var ratio = volts / AdcReferenceVoltage;
        return ratio >= 1.0 ? (ushort)AdcMaxCode : (ushort)Math.Round(ratio * AdcMaxCode);
    }

    // ── IPeripheralMap ────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<IUartPort> Uarts => _uarts;

    /// <inheritdoc />
    public IReadOnlyList<ISpiPort> Spis => _spis;

    /// <inheritdoc />
    public IReadOnlyList<II2cPort> I2cs => _i2cs;

    // ── GPIO bank ─────────────────────────────────────────────────────

    private sealed class PinBank : IPinBank
    {
        private const uint AllPins = (1u << PinCountValue) - 1;

        private readonly RP2040Mcu _owner;
        private readonly RP2040Machine _m;
        private readonly Action<uint> _sioHook;
        private readonly PadEventRecorder _recorder;

        private PinChangeHandler? _changed;
        private ulong _watchMask = AllPins;
        private bool _armed;

        public PinBank(RP2040Mcu owner)
        {
            _owner = owner;
            _m = owner._machine;
            _sioHook = OnSioChanged;
            // Every pad change with its own cycle (PadEventRecorder), handed over while it happens so a handler can
            // answer at once; a SIO output register that moves under a pin nobody drives is no change.
            _recorder = new PadEventRecorder(_m)
            {
                Delivery = PadDelivery.Immediate,
                LevelsOnlyWhenDriven = true,
                Immediate = OnPadsChanged,
            };
        }

        public string Name => "GPIO";

        public int PinCount => PinCountValue;

        public ulong OutputEnableMask
        {
            get
            {
                if (_m.DeferPeripheralTicks) _m.SyncForHostRead();
                _m.IoBank0.GetPadOutputState(out var oe, out _);
                return oe;
            }
        }

        public ulong OutputLevels
        {
            get
            {
                if (_m.DeferPeripheralTicks) _m.SyncForHostRead();
                _m.IoBank0.GetPadOutputState(out var oe, out var levels);
                return levels & oe;
            }
        }

        public ulong PullUpMask => _m.PadsBank0.PullUpMask & AllPins;

        public ulong PullDownMask => _m.PadsBank0.PullDownMask & AllPins;

        public void SetInputs(ulong mask, ulong levels)
        {
            var io = _m.IoBank0;
            var bits = (uint)mask & AllPins;
            // A circuit solver re-applies the same levels many times per step: pads already held at them change nothing.
            bits &= ~io.ExternalHeldMask | (io.ExternalLevels ^ (uint)levels);
            if (bits == 0) return;
            // The cycles already run belong to the old level (see RP2040Machine.SyncForHostInput).
            _m.SyncForHostInput();
            while (bits != 0)
            {
                var pin = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                io.SetExternalInput(pin, ((levels >> pin) & 1UL) != 0);
            }
        }

        public void ReleaseInputs(ulong mask)
        {
            var io = _m.IoBank0;
            var bits = (uint)mask & AllPins & io.ExternalHeldMask;
            if (bits == 0) return;
            _m.SyncForHostInput();
            io.ReleaseExternalInput(bits);
        }

        public event PinChangeHandler PinChanged
        {
            add
            {
                _changed += value;
                Arm();
            }
            remove
            {
                _changed -= value;
                if (_changed is null) Disarm();
            }
        }

        public ulong WatchMask
        {
            get => _watchMask;
            // The recorder follows every pin, so a pin coming into the watch set compares against where it is now.
            set => _watchMask = value & AllPins;
        }

        private void Arm()
        {
            if (_armed) return;
            _armed = true;
            // PWM pads follow the counter: have their edges reported, at the cycle of the step that made each.
            _m.ExactPwmPadEdges = true;
            _recorder.Attach();
            // SIO writes do not reach IO_BANK0 on their own; the hook forwards just the watched pads
            // that the write moved, so firmware banging other pins pays nothing here.
            _m.Sio.OnGpioChanged += _sioHook;
        }

        public void Disarm()
        {
            if (!_armed) return;
            _armed = false;
            _m.ExactPwmPadEdges = false;
            _recorder.Detach();
            _m.Sio.OnGpioChanged -= _sioHook;
        }

        private void OnSioChanged(uint changed)
        {
            var hit = changed & (uint)_watchMask;
            if (hit != 0) _m.IoBank0.NotifyPads(hit);
        }

        private void OnPadsChanged(uint changed, ulong cycle)
        {
            var handler = _changed;
            if (handler is null) return;
            var moved = changed & (uint)_watchMask;
            if (moved == 0) return;
            _m.IoBank0.GetPadOutputState(out var oe, out var levels);
            var change = new PinChange(moved, oe, levels & oe, _owner._cycleBase + (long)cycle);
            handler(in change);
        }
    }

    // ── Buses ─────────────────────────────────────────────────────────

    private sealed class UartPort(int index, UartPeripheral uart) : IUartPort
    {
        public int Index => index;

        public uint BaudRate
        {
            get
            {
                var cfg = uart.GetConfig();
                var divisor = 64UL * cfg.Ibrd + cfg.Fbrd;
                // UARTIBRD = 0 is invalid on silicon (the port does not transmit).
                if (!cfg.Enabled || cfg.Ibrd == 0) return 0;
                // baud = clk_peri / (16 * (IBRD + FBRD / 64)), with clk_peri at the system clock.
                return (uint)(4UL * RP2040Machine.CLK_HZ / divisor);
            }
        }

        public event Action<byte>? TxByte
        {
            add => uart.OnByteTransmit += value;
            remove => uart.OnByteTransmit -= value;
        }

        public bool TryInjectRx(byte value) => uart.TryInjectByte(value);
    }

    private sealed class SpiPort : ISpiPort
    {
        private readonly SpiPeripheral _spi;
        private Func<uint, uint>? _transfer;

        public SpiPort(int index, SpiPeripheral spi)
        {
            Index = index;
            _spi = spi;
            // The contract says an unattached bus reads all ones; the peripheral alone reads zeros.
            _spi.OnTransfer ??= Idle;
        }

        public int Index { get; }

        public int FrameBits => _spi.GetConfig().DataBits;

        public Func<uint, uint>? Transfer
        {
            get => _transfer;
            set
            {
                _transfer = value;
                _spi.OnTransfer = value is null ? Idle : frame => (ushort)value(frame);
            }
        }

        private ushort Idle(ushort frame) => (ushort)((1u << FrameBits) - 1);
    }

    private sealed class I2cPort(int index, I2cPeripheral i2c) : II2cPort
    {
        private const uint IcCon = 0x000, IcRxflr = 0x078;
        private const uint IcConMasterMode = 1u << 0, IcConSlaveDisable = 1u << 6;
        private const uint RxFifoDepth = 16;

        private readonly Dictionary<byte, II2cTarget> _targets = [];
        private II2cTarget? _active;
        private byte _activeAddr;
        private bool _activeWrite;

        public int Index => index;

        public void AttachTarget(byte addr7, II2cTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (_active is not null && _activeAddr == addr7) EndTransaction();
            var first = _targets.Count == 0;
            _targets[addr7] = target;
            if (first) Install();
        }

        public void DetachTarget(byte addr7)
        {
            if (_active is not null && _activeAddr == addr7) EndTransaction();
            if (_targets.Remove(addr7) && _targets.Count == 0) Uninstall();
        }

        // The peripheral reports every data byte with its address but never the START, so a
        // transaction is inferred: it begins when the address or direction changes (a repeated START
        // is exactly that) and ends on STOP.
        private void Install()
        {
            i2c.DeviceResponds = addr => _targets.ContainsKey(addr);
            i2c.OnWrite = (addr, data) =>
            {
                if (Begin(addr, write: true)) _active!.Write(data);
            };
            i2c.OnRead = addr => Begin(addr, write: false) ? _active!.Read() : (byte)0xFF;
            i2c.OnStop = EndTransaction;
        }

        private void Uninstall()
        {
            i2c.DeviceResponds = null;
            i2c.OnWrite = null;
            i2c.OnRead = null;
            i2c.OnStop = null;
        }

        private bool Begin(byte addr, bool write)
        {
            if (_active is not null && _activeAddr == addr && _activeWrite == write) return true;
            EndTransaction();
            if (!_targets.TryGetValue(addr, out var target)) return false;
            if (!target.Probe(addr, write))
            {
                i2c.SignalAddressNack();
                return false;
            }
            _active = target;
            _activeAddr = addr;
            _activeWrite = write;
            return true;
        }

        private void EndTransaction()
        {
            var target = _active;
            _active = null;
            target?.Stop();
        }

        // ── Firmware as the I2C slave ────────────────────────────────

        // IC_SAR holds 0x55 out of reset, so the register alone cannot say whether the firmware is a
        // slave: the port is one only with MASTER_MODE and IC_SLAVE_DISABLE both clear in IC_CON.
        public byte SlaveAddress
        {
            get
            {
                var con = i2c.ReadWord(IcCon);
                return (con & (IcConMasterMode | IcConSlaveDisable)) == 0 ? i2c.SlaveAddress : (byte)0;
            }
        }

        public event Action<byte>? SlaveAddressChanged
        {
            add => i2c.SlaveAddressChanged += value;
            remove => i2c.SlaveAddressChanged -= value;
        }

        public bool BeginMasterTransfer(byte addr7, bool write) => i2c.SimulateIncomingAddress(addr7, write);

        public bool MasterWrite(byte data)
        {
            if (i2c.ReadWord(IcRxflr) >= RxFifoDepth) return false;
            i2c.SimulateIncomingData(data);
            return true;
        }

        public bool TryMasterRead(out byte data)
        {
            if (!i2c.HasSlaveTransmitByte)
            {
                data = 0;
                return false;
            }
            data = i2c.ReadSlaveTransmitByte();
            return true;
        }

        public void MasterStop() => i2c.SimulateStop();
    }
}

/// <summary>How <see cref="RP2040Mcu(RP2040McuOptions)"/> builds its machine.</summary>
public sealed record RP2040McuOptions
{
    /// <summary>Flash size in bytes (2 MiB, as on a Pico).</summary>
    public uint FlashSizeBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Bootrom revision.</summary>
    public RP2040BootromRevision Bootrom { get; init; } = RP2040BootromRevision.B1;

    /// <summary>Serial blocks that drive and sample the pads themselves (docs/pin-level-buses.md). None by default.</summary>
    public PinLevel.PinLevelBus PinLevelBuses { get; init; } = PinLevel.PinLevelBus.None;

    /// <summary>Deferred peripheral ticks (<see cref="RP2040Machine.DeferPeripheralTicks"/>): on by default; off ticks every
    /// peripheral after every slice, the reference the deferred machine is checked against.</summary>
    public bool DeferPeripheralTicks { get; init; } = true;
}

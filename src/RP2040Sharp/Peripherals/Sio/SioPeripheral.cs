using System.Runtime.CompilerServices;
using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Sio;

/// <summary>
/// Single-Cycle I/O (SIO) peripheral.
/// Base address: 0xD0000000. Register with BusInterconnect via MapDevice(0xD, sio).
/// Addresses received are already masked (address &amp; 0x0FFFFFFF); since SIO is the
/// only device in region 0xD, local offset = address directly.
/// </summary>
public sealed class SioPeripheral : IMemoryMappedDevice
{
    // ── CPUID ────────────────────────────────────────────────────────────
    private const uint CPUID = 0x000;  // 0 = Core0, 1 = Core1

    // ── GPIO (offsets from SIO base) ─────────────────────────────────
    private const uint GPIO_IN      = 0x004;
    private const uint GPIO_HI_IN   = 0x008;  // QSPI GPIO input
    private const uint GPIO_OUT     = 0x010;
    private const uint GPIO_OUT_SET = 0x014;
    private const uint GPIO_OUT_CLR = 0x018;
    private const uint GPIO_OUT_XOR = 0x01C;
    private const uint GPIO_OE      = 0x020;
    private const uint GPIO_OE_SET  = 0x024;
    private const uint GPIO_OE_CLR  = 0x028;
    private const uint GPIO_OE_XOR  = 0x02C;
    private const uint GPIO_HI_OUT      = 0x030;  // QSPI output
    private const uint GPIO_HI_OUT_SET  = 0x034;
    private const uint GPIO_HI_OUT_CLR  = 0x038;
    private const uint GPIO_HI_OUT_XOR  = 0x03C;
    private const uint GPIO_HI_OE       = 0x040;
    private const uint GPIO_HI_OE_SET   = 0x044;
    private const uint GPIO_HI_OE_CLR   = 0x048;
    private const uint GPIO_HI_OE_XOR   = 0x04C;

    // ── Multicore FIFO ────────────────────────────────────────────────
    private const uint FIFO_ST  = 0x050;   // FIFO status
    private const uint FIFO_WR  = 0x054;   // write to TX FIFO (to other core)
    private const uint FIFO_RD  = 0x058;   // read from RX FIFO (from other core)

    // ── Spinlock status ───────────────────────────────────────────────
    private const uint SPINLOCK_ST = 0x05C;  // bitmask of claimed spinlocks

    // ── Hardware divider ─────────────────────────────────────────────
    private const uint DIV_UDIVIDEND = 0x060;
    private const uint DIV_UDIVISOR  = 0x064;
    private const uint DIV_SDIVIDEND = 0x068;
    private const uint DIV_SDIVISOR  = 0x06C;
    private const uint DIV_QUOTIENT  = 0x070;
    private const uint DIV_REMAINDER = 0x074;
    private const uint DIV_CSR       = 0x078;  // bit0=DIRTY, bit1=READY

    // ── Interpolators (INTERP0: 0x080, INTERP1: 0x0C0, stride 0x40) ───
    private const uint INTERP0_BASE = 0x080;
    private const uint INTERP1_BASE = 0x0C0;

    // Per-interp offsets
    private const uint INTERP_ACCUM0     = 0x00;
    private const uint INTERP_ACCUM1     = 0x04;
    private const uint INTERP_BASE0      = 0x08;
    private const uint INTERP_BASE1      = 0x0C;
    private const uint INTERP_BASE2      = 0x10;
    private const uint INTERP_POP_LANE0  = 0x14;  // read+update
    private const uint INTERP_POP_LANE1  = 0x18;
    private const uint INTERP_POP_FULL   = 0x1C;
    private const uint INTERP_PEEK_LANE0 = 0x20;  // read-only
    private const uint INTERP_PEEK_LANE1 = 0x24;
    private const uint INTERP_PEEK_FULL  = 0x28;
    private const uint INTERP_CTRL_LANE0 = 0x2C;
    private const uint INTERP_CTRL_LANE1 = 0x30;
    private const uint INTERP_ACCUM0_ADD = 0x34;
    private const uint INTERP_ACCUM1_ADD = 0x38;
    private const uint INTERP_BASE_1AND0 = 0x3C;

    // ── Spinlocks ────────────────────────────────────────────────────
    private const uint SPINLOCK_BASE = 0x100;
    private const uint SPINLOCK_END  = 0x17C;

    private readonly CortexM0Plus _cpu;
    private CortexM0Plus? _cpu1;  // Core1 CPU; set by RP2040Machine after construction
    private bool _core1Launched;  // true once the §2.8.3 launch handshake has completed

    /// <summary>
    /// Returns the ID of the core currently performing the bus access (0 or 1).
    /// Set by RP2040Machine before each CPU's Run() call.
    /// </summary>
    public Func<int>? GetActiveCoreId;

    // GPIO state
    private uint _gpioOut;
    private uint _gpioOe;
    private uint _gpioIn;
    private uint _gpioHiOut;
    private uint _gpioHiOe;

    // Divider state. UDIVIDEND and SDIVIDEND are two views of ONE physical register (same for the
    // two divisors, §2.3.1.5): the alias only selects how the operands are interpreted. Keeping
    // them apart made a signed write invisible to a later unsigned one, and vice versa.
    private uint _divDividend, _divDivisor;
    private uint _divQuotient, _divRemainder;
    private uint _divCsr;
    private bool _divSigned;

    // Spinlocks
    private uint _spinLocks;

    // Multicore FIFO — two one-directional queues
    //   _fifo01: Core0 → Core1   (Core0 writes here, Core1 reads here)
    //   _fifo10: Core1 → Core0   (Core1 writes here, Core0 reads here)
    // FIFO_ST bits: VLD[0]=RX not empty, RDY[1]=TX not full, WOF[2], ROE[3]
    private const int FIFO_DEPTH = 8;
    private const uint FIFO_ST_VLD = 1u;        // RX has data
    private const uint FIFO_ST_RDY = 1u << 1;   // TX has space
    private const uint FIFO_ST_WOF = 1u << 2;   // write-overflow (TX write when full)
    private const uint FIFO_ST_ROE = 1u << 3;   // read-underflow (RX read when empty)

    private readonly Queue<uint> _fifo01 = new(FIFO_DEPTH);  // Core0→Core1
    private readonly Queue<uint> _fifo10 = new(FIFO_DEPTH);  // Core1→Core0
    private bool _wof0, _roe0;  // Core0's WOF/ROE flags
    private bool _wof1, _roe1;  // Core1's WOF/ROE flags

    // Multicore launch handshake state (RP2040 datasheet §2.8.3)
    // Core0 sends the 6-word sequence: 0, 0, 1, VTOR, SP, Entry.
    // Before Core1 is running we echo each word back so Core0's blocking
    // pop returns immediately, and we configure + launch Core1 on the 6th word.
    private int _launchSeqPos;
    private uint _launchVtor;
    private uint _launchSp;
    private uint _launchEntry;

    /// <summary>
    /// Fired when Core0 completes the multicore launch sequence (RP2040 §2.8.3).
    /// Parameters: (vtor, sp, entry).  RP2040Machine sets this callback to configure
    /// and start Core1.
    /// </summary>
    public Action<uint, uint, uint>? OnLaunchCore1;

    /// <summary>Raised whenever a SIO GPIO output level or output-enable register changes, so a board can
    /// forward SIO-driven pad changes to an attached external device (the CYW43439 wires REG_ON/CS to SIO).</summary>
    /// <summary>
    /// Raised when SIO changes what the pads are driving, with a mask of the bits that actually
    /// changed. A board forwards this to IO_BANK0 so an attached external device (the Pico W's radio)
    /// sees the edges. The mask matters: re-evaluating all 30 pads on every GPIO write cost about 40%
    /// of the emulator's throughput on a Pico W, for pins that had not moved.
    /// </summary>
    public Action<uint>? OnGpioChanged { get; set; }

    // Interpolators
    private InterpState _interp0;
    private InterpState _interp1;

    public uint Size => 0x10000;  // wide enough to cover spinlocks at 0x100–0x17C

    /// <summary>
    /// PADS_BANK0, so a floating input reads the level its pull resistor sets. Without it every
    /// unconnected pin read as 0 — <c>Pin(n, Pin.IN, Pin.PULL_UP)</c> looked permanently pressed,
    /// and MicroPython's soft-I2C (which <c>I2C.scan()</c> uses for its zero-length writes) hung
    /// forever waiting for the SCL it had released to rise.
    /// </summary>
    public Pads.PadsPeripheral? PadsBank0;

    /// <summary>Pins whose level is imposed by something outside SIO: the PIO, or a wired signal.</summary>
    private uint _gpioDriven;

    /// <summary>Optionally feed current GPIO input state from IO_BANK0.</summary>
    public uint GpioIn
    {
        get => EffectiveGpioIn;
        set => _gpioIn = value;
    }

    /// <summary>
    /// GPIO_IN as the pads present it (§2.19.2: it reflects the pad, not the source). A SIO output
    /// reads back the level it is driving — <c>Pin.value()</c> on an output pin is exactly this read,
    /// and it used to come back 0 no matter what the pin was doing. Pads held by the PIO or by a wired
    /// signal keep that level, and anything nothing holds floats to its pull-up.
    /// </summary>
    private uint EffectiveGpioIn
    {
        get
        {
            var sioDriven = _gpioOe;
            var floating  = ~(sioDriven | _gpioDriven);
            var pullUp    = PadsBank0?.PullUpMask ?? 0;
            return (_gpioIn & ~(sioDriven | floating))   // levels imposed from outside (PIO, wiring)
                 | (_gpioOut & sioDriven)                // an output reads back what it drives
                 | (pullUp & floating);                  // the rest settle on their pull-up
        }
    }

    /// <summary>
    /// Drive input levels from something that actually holds the pin (the PIO, or a wired-up probe),
    /// so those pins are no longer treated as floating.
    /// </summary>
    /// <summary>Announce a pad change, but only when something changed and someone is listening.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyGpio(uint changed)
    {
        if (changed != 0) OnGpioChanged?.Invoke(changed);
    }

    public void DriveGpioIn(uint value, uint mask)
    {
        _gpioIn = (_gpioIn & ~mask) | (value & mask);
        _gpioDriven |= mask;
    }

    public uint GpioOe  => _gpioOe;
    public uint GpioOut => _gpioOut;

    public void SetGpioExternalIn(int pin, bool high)
    {
        var mask = 1u << pin;
        if (high) _gpioIn |=  mask;
        else      _gpioIn &= ~mask;
        _gpioDriven |= mask;   // a wired signal wins over the pad's pull
    }

    public bool GetGpioOutputEnable(int pin) => (_gpioOe  & (1u << pin)) != 0;
    public bool GetGpioOut(int pin)           => (_gpioOut & (1u << pin)) != 0;

    public SioPeripheral(CortexM0Plus cpu)
    {
        _cpu = cpu;
    }

    /// <summary>Register Core1's CPU so FIFO writes can signal it.</summary>
    public void SetCpu1(CortexM0Plus cpu1) => _cpu1 = cpu1;

    // ── IMemoryMappedDevice — reads ──────────────────────────────────

    public uint ReadWord(uint address)
    {
        // Spinlocks 0–31
        if (address >= SPINLOCK_BASE && address <= SPINLOCK_END)
            return ReadSpinlock((int)((address - SPINLOCK_BASE) >> 2));

        // Interpolator 0
        if (address >= INTERP0_BASE && address < INTERP0_BASE + 0x40)
            return ReadInterp(ref _interp0, address - INTERP0_BASE);

        // Interpolator 1
        if (address >= INTERP1_BASE && address < INTERP1_BASE + 0x40)
            return ReadInterp(ref _interp1, address - INTERP1_BASE);

        return address switch
        {
            CPUID        => (uint)(GetActiveCoreId?.Invoke() ?? 0),
            GPIO_IN      => EffectiveGpioIn,
            // GPIO_HI_IN: QSPI GPIO inputs. Bit 1 = QSPI_SS_N (active-low flash select / BOOTSEL).
            // It must read HIGH (1) so the bootrom BOOTSEL check sees "button not pressed" and
            // proceeds to flash boot instead of USB BOOTSEL mode.
            // Other data lines (SD0-SD3, bits 2-5) are HIGH at idle; SCLK (bit 0) is LOW.
            GPIO_HI_IN   => 0b111110u,  // SS_N=1 (bit1), SD0-SD3=1 (bits2-5), SCLK=0 (bit0)
            GPIO_OUT     => _gpioOut,
            GPIO_HI_OUT  => _gpioHiOut,
            GPIO_OE      => _gpioOe,
            GPIO_HI_OE   => _gpioHiOe,
            DIV_UDIVIDEND => _divDividend,
            DIV_UDIVISOR  => _divDivisor,
            DIV_SDIVIDEND => _divDividend,
            DIV_SDIVISOR  => _divDivisor,
            DIV_QUOTIENT  => _divQuotient,
            DIV_REMAINDER => _divRemainder,
            DIV_CSR       => _divCsr,
            FIFO_ST  => BuildFifoStatus(GetActiveCoreId?.Invoke() ?? 0),
            FIFO_RD  => ReadFifoForCore(GetActiveCoreId?.Invoke() ?? 0),
            SPINLOCK_ST   => _spinLocks,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    // ── IMemoryMappedDevice — writes ─────────────────────────────────

    public void WriteWord(uint address, uint value)
    {
        // Spinlocks — any write releases
        if (address >= SPINLOCK_BASE && address <= SPINLOCK_END)
        {
            _spinLocks &= ~(1u << (int)((address - SPINLOCK_BASE) >> 2));
            return;
        }

        // Interpolator 0
        if (address >= INTERP0_BASE && address < INTERP0_BASE + 0x40)
        {
            WriteInterp(ref _interp0, address - INTERP0_BASE, value);
            return;
        }

        // Interpolator 1
        if (address >= INTERP1_BASE && address < INTERP1_BASE + 0x40)
        {
            WriteInterp(ref _interp1, address - INTERP1_BASE, value);
            return;
        }

        switch (address)
        {
            // Apply first, then announce: the listener re-reads the pad through GetPadOutputLevel,
            // so notifying before the store shows it the old level and the change is lost.
            case GPIO_OUT:     { var d = _gpioOut ^ value;  _gpioOut  =  value; NotifyGpio(d);     break; }
            case GPIO_OUT_SET: { var d = value & ~_gpioOut; _gpioOut |=  value; NotifyGpio(d);     break; }
            case GPIO_OUT_CLR: { var d = value &  _gpioOut; _gpioOut &= ~value; NotifyGpio(d);     break; }
            case GPIO_OUT_XOR: {                            _gpioOut ^=  value; NotifyGpio(value); break; }
            case GPIO_OE:      { var d = _gpioOe ^ value;   _gpioOe   =  value; NotifyGpio(d);     break; }
            case GPIO_OE_SET:  { var d = value & ~_gpioOe;  _gpioOe  |=  value; NotifyGpio(d);     break; }
            case GPIO_OE_CLR:  { var d = value &  _gpioOe;  _gpioOe  &= ~value; NotifyGpio(d);     break; }
            case GPIO_OE_XOR:  {                            _gpioOe  ^=  value; NotifyGpio(value); break; }
            case GPIO_HI_OUT:      _gpioHiOut  =  value; break;
            case GPIO_HI_OUT_SET:  _gpioHiOut |=  value; break;
            case GPIO_HI_OUT_CLR:  _gpioHiOut &= ~value; break;
            case GPIO_HI_OUT_XOR:  _gpioHiOut ^=  value; break;
            case GPIO_HI_OE:       _gpioHiOe  =  value; break;
            case GPIO_HI_OE_SET:   _gpioHiOe |=  value; break;
            case GPIO_HI_OE_CLR:   _gpioHiOe &= ~value; break;
            case GPIO_HI_OE_XOR:   _gpioHiOe ^=  value; break;

            case FIFO_WR:
            {
                var coreId = GetActiveCoreId?.Invoke() ?? 0;
                if (coreId == 0)
                {
                    if (!_core1Launched)
                    {
                        // Core1 not yet running: handle the RP2040 §2.8.3 launch handshake
                        // natively by echoing each word back so Core0's pop returns immediately.
                        // (_cpu1 is always wired up at construction, so gate on the launch
                        // state — not on a null reference — to actually enter the handshake.)
                        HandleLaunchHandshake(value);
                    }
                    else
                    {
                        // Core0 sends to Core1 (normal FIFO operation after Core1 is live)
                        if (_fifo01.Count < FIFO_DEPTH)
                        {
                            _fifo01.Enqueue(value);
                            _cpu1!.SetInterrupt(16, true);          // SIO_IRQ_PROC1 on Core1
                            _cpu1.Registers.EventRegistered = true; // wake WFE
                        }
                        else _wof0 = true;
                    }
                }
                else
                {
                    // Core1 sends to Core0
                    if (_fifo10.Count < FIFO_DEPTH)
                    {
                        _fifo10.Enqueue(value);
                        _cpu.SetInterrupt(15, true);           // SIO_IRQ_PROC0 on Core0
                        _cpu.Registers.EventRegistered = true; // wake WFE
                    }
                    else _wof1 = true;
                }
                break;
            }
            case FIFO_ST:
            {
                // Write 1 to clear WOF and ROE
                var coreId = GetActiveCoreId?.Invoke() ?? 0;
                if (coreId == 0)
                {
                    if ((value & FIFO_ST_WOF) != 0) _wof0 = false;
                    if ((value & FIFO_ST_ROE) != 0) _roe0 = false;
                }
                else
                {
                    if ((value & FIFO_ST_WOF) != 0) _wof1 = false;
                    if ((value & FIFO_ST_ROE) != 0) _roe1 = false;
                }
                break;
            }

            // §2.3.1.5: "The calculation is started by a write to either the DIVIDEND or the DIVISOR
            // register" — not the divisor alone. pico-sdk's 64-bit divide (divmod_u64u64_unsafe,
            // reached by any `uint64_t / uint64_t` in firmware) writes DIVISOR first and DIVIDEND
            // second, so a divisor-only trigger handed it the PREVIOUS division's quotient. That is
            // what made MicroPython compute a nonsense PIO clock divider and reject freq=8_000_000
            // in the ws2812 / neopixel examples with "freq out of range".
            case DIV_UDIVIDEND:
                _divDividend = value;
                _divSigned   = false;
                PerformDivide();
                break;
            case DIV_UDIVISOR:
                _divDivisor = value;
                _divSigned  = false;
                PerformDivide();
                break;
            case DIV_SDIVIDEND:
                _divDividend = value;
                _divSigned   = true;
                PerformDivide();
                break;
            case DIV_SDIVISOR:
                _divDivisor = value;
                _divSigned  = true;
                PerformDivide();
                break;
            case DIV_QUOTIENT:
                _divQuotient = value;
                _divCsr |= 1;
                break;
            case DIV_REMAINDER:
                _divRemainder = value;
                _divCsr |= 1;
                break;
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

    // ── Interpolator implementation ──────────────────────────────────

    private static uint ReadInterp(ref InterpState st, uint offset)
    {
        return offset switch
        {
            INTERP_ACCUM0     => st.Accum0,
            INTERP_ACCUM1     => st.Accum1,
            INTERP_BASE0      => st.Base0,
            INTERP_BASE1      => st.Base1,
            INTERP_BASE2      => st.Base2,
            INTERP_CTRL_LANE0 => st.Ctrl0,
            INTERP_CTRL_LANE1 => st.Ctrl1,
            INTERP_PEEK_LANE0 => ComputeLane(ref st, 0),
            INTERP_PEEK_LANE1 => ComputeLane(ref st, 1),
            INTERP_PEEK_FULL  => ComputeFull(ref st),
            INTERP_POP_LANE0  => PopLane(ref st, 0),
            INTERP_POP_LANE1  => PopLane(ref st, 1),
            INTERP_POP_FULL   => PopFull(ref st),
            INTERP_ACCUM0_ADD => st.Accum0,
            INTERP_ACCUM1_ADD => st.Accum1,
            _                 => 0,
        };
    }

    private static void WriteInterp(ref InterpState st, uint offset, uint value)
    {
        switch (offset)
        {
            case INTERP_ACCUM0:     st.Accum0 = value; break;
            case INTERP_ACCUM1:     st.Accum1 = value; break;
            case INTERP_BASE0:      st.Base0  = value; break;
            case INTERP_BASE1:      st.Base1  = value; break;
            case INTERP_BASE2:      st.Base2  = value; break;
            case INTERP_CTRL_LANE0: st.Ctrl0  = value; break;
            case INTERP_CTRL_LANE1: st.Ctrl1  = value; break;
            case INTERP_ACCUM0_ADD: st.Accum0 += value; break;
            case INTERP_ACCUM1_ADD: st.Accum1 += value; break;
            case INTERP_BASE_1AND0:
                // sets BASE0 and BASE1 from a combined 32-bit write:
                // BASE0 = bits [15:0], BASE1 = bits [31:16]
                st.Base0 = (ushort)value;
                st.Base1 = value >> 16;
                break;
        }
    }

    /// <summary>
    /// Compute the primary (pre-CROSS_RESULT) result for one lane.
    /// Implements the full RP2040 TRM §2.3.1 interpolator pipeline:
    /// shift → mask → sign-extend → +BASE, with ADD_RAW, BLEND, CLAMP modes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeLane(ref InterpState st, int lane)
    {
        var ctrl    = lane == 0 ? st.Ctrl0 : st.Ctrl1;
        var shift   = (int)(ctrl & 0x1F);
        var maskLsb = (int)((ctrl >> 5) & 0x1F);
        var maskMsb = (int)((ctrl >> 10) & 0x1F);
        var signed  = (ctrl & (1u << 15)) != 0;
        var crossIn = (ctrl & (1u << 16)) != 0;
        var addRaw  = (ctrl & (1u << 20)) != 0;  // ADD_RAW: add raw (unshifted) accum to result
        var blend   = (ctrl & (1u << 21)) != 0;  // BLEND: linear interpolation (lane 0 ctrl only)
        var clamp   = (ctrl & (1u << 22)) != 0;  // CLAMP: clamp result to [BASE0, BASE1]

        uint accum = crossIn
            ? (lane == 0 ? st.Accum1 : st.Accum0)
            : (lane == 0 ? st.Accum0 : st.Accum1);

        // BLEND mode (RP2040 TRM §2.3.1.4): only used for lane 0; result = Base0 + alpha*(Base1-Base0)/256
        // where alpha = accum0[7:0]. Ignores shift/mask/sign.
        if (blend && lane == 0)
        {
            uint alpha = st.Accum0 & 0xFF;
            // Arithmetic on signed Base values to handle Base1 < Base0 wrap
            int blended = (int)st.Base0 + (int)((alpha * ((long)(int)st.Base1 - (int)st.Base0)) / 256);
            return (uint)blended;
        }

        uint shifted = signed
            ? (uint)((int)accum >> shift)
            : accum >> shift;

        uint mask   = BuildMask(maskLsb, maskMsb);
        uint masked = shifted & mask;

        // Sign-extend at maskMsb when SIGNED
        if (signed && maskMsb < 31)
        {
            uint signBit = 1u << maskMsb;
            if ((masked & signBit) != 0)
                masked |= ~mask;
        }

        uint baseVal = lane == 0 ? st.Base0 : st.Base1;

        uint result;
        if (addRaw)
            // ADD_RAW: skip mask, add the raw shifted (but not masked) accumulator to BASE
            result = shifted + baseVal;
        else
            result = masked + baseVal;

        // CLAMP (RP2040 TRM §2.3.1.5): only applies to lane 0; clamp to [BASE0, BASE1].
        // Base0 is the lower bound, Base1 the upper bound (unsigned comparison).
        if (clamp && lane == 0)
        {
            if (result < st.Base0) result = st.Base0;
            if (result > st.Base1) result = st.Base1;
        }

        return result;
    }

    /// <summary>
    /// Compute the FULL result: applies CROSS_RESULT routing and adds BASE2.
    /// CROSS_RESULT on a lane swaps which lane's primary result feeds into the full output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeFull(ref InterpState st)
    {
        uint r0 = ComputeLane(ref st, 0);
        uint r1 = ComputeLane(ref st, 1);

        // CROSS_RESULT (bit 17): if set for lane 0, lane 0's contribution to FULL uses lane 1's result.
        // If set for lane 1, lane 1's contribution is ignored for FULL (the full result uses lane 0).
        // Per TRM: FULL = RESULT0 + BASE2, with CROSS_RESULT_0 swapping RESULT0 ↔ RESULT1 for that slot.
        var crossResult0 = (st.Ctrl0 & (1u << 17)) != 0;
        uint fullBase = crossResult0 ? r1 : r0;
        return fullBase + st.Base2;
    }

    private static uint PopLane(ref InterpState st, int lane)
    {
        uint r0 = ComputeLane(ref st, 0);
        uint r1 = ComputeLane(ref st, 1);
        // POP writes results back to accumulators (advances the pipeline)
        st.Accum0 = r0;
        st.Accum1 = r1;
        return lane == 0 ? r0 : r1;
    }

    private static uint PopFull(ref InterpState st)
    {
        uint r0 = ComputeLane(ref st, 0);
        uint r1 = ComputeLane(ref st, 1);
        st.Accum0 = r0;
        st.Accum1 = r1;
        var crossResult0 = (st.Ctrl0 & (1u << 17)) != 0;
        uint fullBase = crossResult0 ? r1 : r0;
        return fullBase + st.Base2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BuildMask(int lsb, int msb)
    {
        if (msb < lsb) return 0;
        int bits = msb - lsb + 1;
        uint mask = bits >= 32 ? 0xFFFFFFFF : (1u << bits) - 1;
        return mask << lsb;
    }

    // ── Divider ──────────────────────────────────────────────────────

    private void PerformDivide()
    {
        _cpu.Cycles += 8;
        _divCsr = 0x2;  // READY, not DIRTY

        if (_divSigned)
        {
            var dividend = (int)_divDividend;
            var divisor  = (int)_divDivisor;
            if (divisor == 0)
            {
                // RP2040 TRM §2.3.1.6: for signed div-by-zero:
                //   quotient = +1 when dividend >= 0  (0x00000001)
                //   quotient = -1 when dividend <  0  (0xFFFFFFFF)
                // remainder = dividend in both cases.
                _divQuotient  = dividend >= 0 ? 1u : 0xFFFFFFFF;
                _divRemainder = _divDividend;
            }
            else if (dividend == int.MinValue && divisor == -1)
            {
                // The one case C# throws on: the true quotient is not representable. Silicon wraps.
                _divQuotient  = 0x80000000;
                _divRemainder = 0;
            }
            else
            {
                _divQuotient  = (uint)(dividend / divisor);
                _divRemainder = (uint)(dividend % divisor);
            }
        }
        else
        {
            if (_divDivisor == 0)
            {
                _divQuotient  = 0xFFFFFFFF;
                _divRemainder = _divDividend;
            }
            else
            {
                _divQuotient  = _divDividend / _divDivisor;
                _divRemainder = _divDividend % _divDivisor;
            }
        }
    }

    // ── Multicore launch handshake ────────────────────────────────────

    /// <summary>
    /// Processes one word of the RP2040 §2.8.3 multicore launch sequence sent by Core0
    /// while Core1 is not yet running.  Each word is echoed back into Core0's RX FIFO
    /// so Core0's blocking pop returns immediately.  When all 6 words of the sequence
    /// (0, 0, 1, VTOR, SP, Entry) have been received, <see cref="OnLaunchCore1"/> is
    /// invoked and the sequence position is reset to allow re-launch if needed.
    /// </summary>
    private void HandleLaunchHandshake(uint value)
    {
        // Validate sequence position (§2.8.3: 0, 0, 1, VTOR, SP, Entry).
        // Positions 0–2 carry constant magic values that identify a genuine launch attempt;
        // positions 3–5 carry firmware-specific addresses (VTOR, SP, Entry) that we accept
        // unconditionally.  A mismatch at positions 0–2 resets the counter to let Core0 retry.
        bool valid = _launchSeqPos switch
        {
            0 or 1 => value == 0,   // sync/flush words
            2      => value == 1,   // magic "ready" sentinel
            _      => true,         // positions 3,4,5: VTOR, SP, Entry (any value valid)
        };

        if (!valid)
        {
            // Mismatch: Core0 is retrying, restart from position 0.
            _launchSeqPos = 0;
            // If the new value is 0 it matches position 0; process it.
            if (value != 0) return;
        }

        // Store payload words for positions 3–5.
        switch (_launchSeqPos)
        {
            case 3: _launchVtor  = value; break;
            case 4: _launchSp    = value; break;
            case 5: _launchEntry = value; break;
        }

        // Echo value back to Core0's RX FIFO (simulates Core1's response).
        if (_fifo10.Count < FIFO_DEPTH)
        {
            _fifo10.Enqueue(value);
            _cpu.SetInterrupt(15, true);           // SIO_IRQ_PROC0 on Core0
            _cpu.Registers.EventRegistered = true; // wake Core0's WFE
        }

        _launchSeqPos++;

        if (_launchSeqPos == 6)
        {
            _launchSeqPos  = 0;     // reset sequence tracking
            _core1Launched = true;  // subsequent FIFO writes are normal Core0→Core1 traffic
            OnLaunchCore1?.Invoke(_launchVtor, _launchSp, _launchEntry);
        }
    }

    /// <summary>
    /// Clear the multicore launch state so a subsequent §2.8.3 handshake re-launches Core1.
    /// Called by <see cref="RP2040Machine.Reset"/>.
    /// </summary>
    public void ResetMulticoreLaunch()
    {
        _core1Launched = false;
        _launchSeqPos  = 0;
    }

    /// <summary>
    /// Simulate Core 1's bootrom reporting "ready" after it is released from a PSM reset
    /// (<c>multicore_reset_core1()</c>).  On real silicon Core 1 comes out of reset, drains
    /// its own inbound mailbox, then pushes a single 0 to Core 0.  We reproduce that: clear
    /// Core 1's RX FIFO and status flags, enqueue the 0 into Core 0's RX FIFO, and raise an
    /// event so Core 0's blocking <c>multicore_fifo_pop_blocking()</c>/WFE returns.
    /// </summary>
    public void SignalCore1BootromReady()
    {
        // Core 1 drains its own inbound mailbox (Core0→Core1) as it reboots.
        _fifo01.Clear();
        _wof1 = _roe1 = false;

        // Push the "ready" sentinel (0) into Core 0's RX FIFO (Core1→Core0) and wake it.
        if (_fifo10.Count < FIFO_DEPTH)
            _fifo10.Enqueue(0);
        _cpu.SetInterrupt(15, true);           // SIO_IRQ_PROC0: data available for Core 0
        _cpu.Registers.EventRegistered = true; // SEV: wake Core 0 out of WFE
    }

    // ── FIFO helpers ──────────────────────────────────────────────────

    private uint BuildFifoStatus(int coreId)
    {
        if (coreId == 0)
        {
            // Core0: RX = fifo10 (Core1→Core0), TX = fifo01 (Core0→Core1)
            return (_fifo10.Count > 0 ? FIFO_ST_VLD : 0u)
                 | (_fifo01.Count < FIFO_DEPTH ? FIFO_ST_RDY : 0u)
                 | (_wof0 ? FIFO_ST_WOF : 0u)
                 | (_roe0 ? FIFO_ST_ROE : 0u);
        }
        else
        {
            // Core1: RX = fifo01 (Core0→Core1), TX = fifo10 (Core1→Core0)
            return (_fifo01.Count > 0 ? FIFO_ST_VLD : 0u)
                 | (_fifo10.Count < FIFO_DEPTH ? FIFO_ST_RDY : 0u)
                 | (_wof1 ? FIFO_ST_WOF : 0u)
                 | (_roe1 ? FIFO_ST_ROE : 0u);
        }
    }

    private uint ReadFifoForCore(int coreId)
    {
        if (coreId == 0)
        {
            if (_fifo10.TryDequeue(out var v)) return v;
            _roe0 = true;
            return 0;
        }
        else
        {
            if (_fifo01.TryDequeue(out var v)) return v;
            _roe1 = true;
            return 0;
        }
    }

    private uint ReadFifoRx() => ReadFifoForCore(0);  // legacy alias for Core0

    /// <summary>
    /// Push a value into Core0's RX FIFO as if Core1 sent it.
    /// Used by tests and simulated multicore scenarios.
    /// </summary>
    public void InjectFifoRx(uint value)
    {
        if (_fifo10.Count < FIFO_DEPTH)
        {
            _fifo10.Enqueue(value);
            _cpu.SetInterrupt(15, true);   // SIO_IRQ_PROC0: notify Core0 data is available
        }
    }

    /// <summary>
    /// Drain the TX FIFO (values written by Core0 and "sent" to Core1).
    /// </summary>
    public bool TryDequeueTx(out uint value) => _fifo01.TryDequeue(out value);

    // ── Spinlocks ─────────────────────────────────────────────────────

    private uint ReadSpinlock(int index)
    {
        var bit = 1u << index;
        if ((_spinLocks & bit) != 0)
            return 0;   // already taken

        _spinLocks |= bit;
        return bit;
    }
}

// ── Interpolator state ────────────────────────────────────────────────────────
internal struct InterpState
{
    public uint Accum0, Accum1;
    public uint Base0, Base1, Base2;
    public uint Ctrl0, Ctrl1;
}

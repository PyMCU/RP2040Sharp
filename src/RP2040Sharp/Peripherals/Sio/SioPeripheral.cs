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
public sealed class SioPeripheral : IMemoryMappedDevice, IAddressDecoder
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
    // The divider and both interpolators are PER-CORE on silicon (§2.3.1.3, §2.3.1.5): each core sees its own
    // instance at the same SIO addresses. Sharing one made two cores' divisions clobber each other whenever their
    // run slices interleaved between the operand writes and the result read (every `/` and `%` on RP2040 goes
    // through the divider), handing one core the other's quotient.
    private struct DivState { public uint Dividend, Divisor, Quotient, Remainder, Csr; public bool Signed; }
    private readonly DivState[] _div = new DivState[2];
    private int CoreIdx => (GetActiveCoreId?.Invoke() ?? 0) & 1;

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
    private readonly InterpState[] _interp0 = new InterpState[2];
    private readonly InterpState[] _interp1 = new InterpState[2];

    public uint Size => 0x10000;  // wide enough to cover spinlocks at 0x100–0x17C

    public uint DecodeLimit => Size;
    public bool[]? SlotPresent => null;
    public int SlotShift => 0;

    /// <summary>
    /// PADS_BANK0, so a floating input reads the level its pull resistor sets. Without it every
    /// unconnected pin read as 0 — <c>Pin(n, Pin.IN, Pin.PULL_UP)</c> looked permanently pressed,
    /// and MicroPython's soft-I2C (which <c>I2C.scan()</c> uses for its zero-length writes) hung
    /// forever waiting for the SCL it had released to rise.
    /// </summary>
    public Pads.PadsPeripheral? PadsBank0;

    /// <summary>Pins whose level is imposed by something outside SIO: the PIO, or a wired signal.</summary>
    private uint _gpioDriven;

    /// <summary>
    /// IO_BANK0's view of the pads: the level of every pad after the GPIO function mux, the OUTOVER / OEOVER overrides
    /// and the pad's own IE and OD. When set, GPIO_IN reads it, because GPIO_IN is the pad, not a copy of SIO's
    /// own output register (datasheet 2.3.1.2); otherwise SIO's standalone estimate is used.
    /// </summary>
    internal Func<uint>? PadInputProvider;

    /// <summary>Told which pads SIO just changed (GPIO_OUT / GPIO_OE), so IO_BANK0 can latch edges for pins SIO drives.</summary>
    internal Action<uint>? PadSink;

    /// <summary>Optionally feed current GPIO input state from IO_BANK0.</summary>
    public uint GpioIn
    {
        get => PadInputProvider?.Invoke() ?? EffectiveGpioIn;
        set => _gpioIn = value;
    }

    /// <summary>
    /// Levels of pads nothing on the chip drives: whatever a wired signal or the PIO imposed from outside, or the
    /// pull resistor for a pad nothing holds. IO_BANK0 merges this with the pads the chip does drive.
    /// </summary>
    internal uint UndrivenLevels
    {
        get
        {
            var pullUp = PadsBank0?.PullUpMask ?? 0;
            return (_gpioIn & _gpioDriven) | (pullUp & ~_gpioDriven);
        }
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
        if (changed == 0) return;
        PadSink?.Invoke(changed);
        OnGpioChanged?.Invoke(changed);
    }

    public void DriveGpioIn(uint value, uint mask)
    {
        _gpioIn = (_gpioIn & ~mask) | (value & mask);
        _gpioDriven |= mask;
    }

    /// <summary>Hands the pads in <paramref name="mask"/> back to float: forgets the level a wired signal
    /// imposed on them (see <see cref="SetGpioExternalIn"/>) so they settle on their pull again.</summary>
    public void ReleaseGpioExternal(uint mask)
    {
        _gpioDriven &= ~mask;
        _gpioIn     &= ~mask;
    }

    private const uint GpioMask   = (1u << 30) - 1;
    private const uint GpioHiMask = (1u << 6) - 1;

    /// <summary>The levels of the pads something holds from outside (a wired signal or the PIO), where it holds them.</summary>
    internal uint HeldInputLevels => _gpioIn & _gpioDriven;

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
            return ReadInterp(ref _interp0[CoreIdx], false, address - INTERP0_BASE);

        // Interpolator 1
        if (address >= INTERP1_BASE && address < INTERP1_BASE + 0x40)
            return ReadInterp(ref _interp1[CoreIdx], true, address - INTERP1_BASE);

        return address switch
        {
            CPUID        => (uint)(GetActiveCoreId?.Invoke() ?? 0),
            GPIO_IN      => GpioIn,
            // GPIO_HI_IN: QSPI GPIO inputs. Bit 1 = QSPI_SS_N (active-low flash select / BOOTSEL).
            // It must read HIGH (1) so the bootrom BOOTSEL check sees "button not pressed" and
            // proceeds to flash boot instead of USB BOOTSEL mode.
            // Other data lines (SD0-SD3, bits 2-5) are HIGH at idle; SCLK (bit 0) is LOW.
            GPIO_HI_IN   => 0b111110u,  // SS_N=1 (bit1), SD0-SD3=1 (bits2-5), SCLK=0 (bit0)
            GPIO_OUT     => _gpioOut,
            GPIO_HI_OUT  => _gpioHiOut,
            GPIO_OE      => _gpioOe,
            GPIO_HI_OE   => _gpioHiOe,
            DIV_UDIVIDEND => _div[CoreIdx].Dividend,
            DIV_UDIVISOR  => _div[CoreIdx].Divisor,
            DIV_SDIVIDEND => _div[CoreIdx].Dividend,
            DIV_SDIVISOR  => _div[CoreIdx].Divisor,
            DIV_QUOTIENT  => _div[CoreIdx].Quotient,
            DIV_REMAINDER => _div[CoreIdx].Remainder,
            DIV_CSR       => _div[CoreIdx].Csr,
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
            WriteInterp(ref _interp0[CoreIdx], false, address - INTERP0_BASE, value);
            return;
        }

        // Interpolator 1
        if (address >= INTERP1_BASE && address < INTERP1_BASE + 0x40)
        {
            WriteInterp(ref _interp1[CoreIdx], true, address - INTERP1_BASE, value);
            return;
        }

        switch (address)
        {
            // Apply first, then announce: the listener re-reads the pad through GetPadOutputLevel,
            // so notifying before the store shows it the old level and the change is lost.
            // GPIO_OUT and GPIO_OE hold the 30 user pins (bits 29:0); the GPIO_HI_* registers the 6 QSPI pins.
            case GPIO_OUT:     { value &= GpioMask; var d = _gpioOut ^ value;  _gpioOut  =  value; NotifyGpio(d);     break; }
            case GPIO_OUT_SET: { value &= GpioMask; var d = value & ~_gpioOut; _gpioOut |=  value; NotifyGpio(d);     break; }
            case GPIO_OUT_CLR: { value &= GpioMask; var d = value &  _gpioOut; _gpioOut &= ~value; NotifyGpio(d);     break; }
            case GPIO_OUT_XOR: { value &= GpioMask;                            _gpioOut ^=  value; NotifyGpio(value); break; }
            case GPIO_OE:      { value &= GpioMask; var d = _gpioOe ^ value;   _gpioOe   =  value; NotifyGpio(d);     break; }
            case GPIO_OE_SET:  { value &= GpioMask; var d = value & ~_gpioOe;  _gpioOe  |=  value; NotifyGpio(d);     break; }
            case GPIO_OE_CLR:  { value &= GpioMask; var d = value &  _gpioOe;  _gpioOe  &= ~value; NotifyGpio(d);     break; }
            case GPIO_OE_XOR:  { value &= GpioMask;                            _gpioOe  ^=  value; NotifyGpio(value); break; }
            case GPIO_HI_OUT:      _gpioHiOut  =  value & GpioHiMask; break;
            case GPIO_HI_OUT_SET:  _gpioHiOut |=  value & GpioHiMask; break;
            case GPIO_HI_OUT_CLR:  _gpioHiOut &= ~value; break;
            case GPIO_HI_OUT_XOR:  _gpioHiOut ^=  value & GpioHiMask; break;
            case GPIO_HI_OE:       _gpioHiOe  =  value & GpioHiMask; break;
            case GPIO_HI_OE_SET:   _gpioHiOe |=  value & GpioHiMask; break;
            case GPIO_HI_OE_CLR:   _gpioHiOe &= ~value; break;
            case GPIO_HI_OE_XOR:   _gpioHiOe ^=  value & GpioHiMask; break;

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
                _div[CoreIdx].Dividend = value;
                _div[CoreIdx].Signed   = false;
                PerformDivide(CoreIdx);
                break;
            case DIV_UDIVISOR:
                _div[CoreIdx].Divisor = value;
                _div[CoreIdx].Signed  = false;
                PerformDivide(CoreIdx);
                break;
            case DIV_SDIVIDEND:
                _div[CoreIdx].Dividend = value;
                _div[CoreIdx].Signed   = true;
                PerformDivide(CoreIdx);
                break;
            case DIV_SDIVISOR:
                _div[CoreIdx].Divisor = value;
                _div[CoreIdx].Signed  = true;
                PerformDivide(CoreIdx);
                break;
            case DIV_QUOTIENT:
                _div[CoreIdx].Quotient = value;
                _div[CoreIdx].Csr |= 1;
                break;
            case DIV_REMAINDER:
                _div[CoreIdx].Remainder = value;
                _div[CoreIdx].Csr |= 1;
                break;
        }
    }

    // The SIO registers are IO registers too: a narrow write is replicated across the data bus (datasheet 2.1.4).
    public void WriteHalfWord(uint address, ushort value) => WriteWord(address & ~3u, value * 0x00010001u);

    public void WriteByte(uint address, byte value) => WriteWord(address & ~3u, value * 0x01010101u);

    // ── Interpolator implementation ──────────────────────────────────
    // Datasheet 2.3.1.6. Each lane shifts, masks and optionally sign-extends one accumulator; the lane values are
    // added to BASE0, BASE1 and (both together, with BASE2) to form RESULT0, RESULT1 and the FULL result.

    private const uint CTRL_SIGNED       = 1u << 15;
    private const uint CTRL_CROSS_INPUT  = 1u << 16;
    private const uint CTRL_CROSS_RESULT = 1u << 17;
    private const uint CTRL_ADD_RAW      = 1u << 18;
    private const uint CTRL_BLEND        = 1u << 21;   // lane 0 of INTERP0 only
    private const uint CTRL_CLAMP        = 1u << 22;   // lane 0 of INTERP1 only

    private static uint ReadInterp(ref InterpState st, bool interp1, uint offset)
    {
        switch (offset)
        {
            case INTERP_ACCUM0: return st.Accum0;
            case INTERP_ACCUM1: return st.Accum1;
            case INTERP_BASE0:  return st.Base0;
            case INTERP_BASE1:  return st.Base1;
            case INTERP_BASE2:  return st.Base2;
            case INTERP_CTRL_LANE0:
            {
                // OVERF0 / OVERF1 / OVERF (bits 23-25) are read-only status of the shift and mask stage
                var r = Evaluate(ref st, interp1);
                return st.Ctrl0 | (r.Overf0 ? 1u << 23 : 0) | (r.Overf1 ? 1u << 24 : 0) | (r.Overf0 || r.Overf1 ? 1u << 25 : 0);
            }
            case INTERP_CTRL_LANE1: return st.Ctrl1;
            case INTERP_PEEK_LANE0: return Evaluate(ref st, interp1).PresentedResult0;
            case INTERP_PEEK_LANE1: return Evaluate(ref st, interp1).PresentedResult1;
            case INTERP_PEEK_FULL:  return Evaluate(ref st, interp1).Result2;
            case INTERP_POP_LANE0:  return Pop(ref st, interp1).PresentedResult0;
            case INTERP_POP_LANE1:  return Pop(ref st, interp1).PresentedResult1;
            case INTERP_POP_FULL:   return Pop(ref st, interp1).Result2;
            // Reading ACCUMx_ADD yields the lane's raw shift-and-mask value (BASEx not added)
            case INTERP_ACCUM0_ADD: return Evaluate(ref st, interp1).Mask0;
            case INTERP_ACCUM1_ADD: return Evaluate(ref st, interp1).Mask1;
            default: return 0;
        }
    }

    private static void WriteInterp(ref InterpState st, bool interp1, uint offset, uint value)
    {
        switch (offset)
        {
            case INTERP_ACCUM0:     st.Accum0 = value; break;
            case INTERP_ACCUM1:     st.Accum1 = value; break;
            case INTERP_BASE0:      st.Base0  = value; break;
            case INTERP_BASE1:      st.Base1  = value; break;
            case INTERP_BASE2:      st.Base2  = value; break;
            // BLEND exists on INTERP0 lane 0, CLAMP on INTERP1 lane 0; OVERF is read-only. FORCE_MSB..SHIFT
            // (bits 20:0) exist on both lanes.
            case INTERP_CTRL_LANE0: st.Ctrl0  = value & (interp1 ? 0x005FFFFFu : 0x003FFFFFu); break;
            case INTERP_CTRL_LANE1: st.Ctrl1  = value & 0x001FFFFFu; break;
            case INTERP_ACCUM0_ADD: st.Accum0 += value & 0x00FFFFFFu; break;   // 23:0
            case INTERP_ACCUM1_ADD: st.Accum1 += value & 0x00FFFFFFu; break;
            case INTERP_BASE_1AND0:
            {
                // Two 16-bit halves, each sign-extended if the lane's SIGNED is set; in blend mode LANE1's
                // SIGNED decides for both (datasheet 2.3.1.6.2).
                var blend = !interp1 && (st.Ctrl0 & CTRL_BLEND) != 0;
                var signed0 = ((blend ? st.Ctrl1 : st.Ctrl0) & CTRL_SIGNED) != 0;
                var signed1 = (st.Ctrl1 & CTRL_SIGNED) != 0;
                st.Base0 = signed0 ? (uint)(short)value : (ushort)value;
                st.Base1 = signed1 ? (uint)(short)(value >> 16) : value >> 16;
                break;
            }
        }
    }

    private struct InterpResult
    {
        public uint Mask0, Mask1;                 // lane shift+mask(+sign extension) values
        public uint Result0, Result1, Result2;    // as they feed the accumulators on POP
        public uint PresentedResult0, PresentedResult1;   // as the processor reads them (FORCE_MSB applied)
        public bool Overf0, Overf1;
    }

    /// <summary>One lane's shift, mask and sign-extension of <paramref name="input"/>.</summary>
    private static uint ShiftMask(uint input, uint ctrl, out bool overflow)
    {
        var shift = (int)(ctrl & 0x1F);
        var lsb = (int)((ctrl >> 5) & 0x1F);
        var msb = (int)((ctrl >> 10) & 0x1F);

        var shifted = input >> shift;
        var upTo = (uint)(((ulong)1 << (msb + 1)) - 1);            // bits 0..msb
        var below = (uint)(((ulong)1 << lsb) - 1);                 // bits 0..lsb-1
        var masked = msb < lsb ? 0u : shifted & upTo & ~below;
        overflow = (shifted & ~upTo) != 0;                         // set bits above the mask

        // Sign extension "from the top of the mask": bit MSB is ORed into every more significant bit
        if ((ctrl & CTRL_SIGNED) != 0 && ((shifted >> msb) & 1) != 0)
            masked |= ~upTo;
        return masked;
    }

    private static InterpResult Evaluate(ref InterpState st, bool interp1)
    {
        var in0 = (st.Ctrl0 & CTRL_CROSS_INPUT) != 0 ? st.Accum1 : st.Accum0;
        var in1 = (st.Ctrl1 & CTRL_CROSS_INPUT) != 0 ? st.Accum0 : st.Accum1;
        var sm0 = ShiftMask(in0, st.Ctrl0, out var overf0);
        var sm1 = ShiftMask(in1, st.Ctrl1, out var overf1);

        // ADD_RAW bypasses shift and mask for the lane result (not for FULL)
        var val0 = (st.Ctrl0 & CTRL_ADD_RAW) != 0 ? in0 : sm0;
        var val1 = (st.Ctrl1 & CTRL_ADD_RAW) != 0 ? in1 : sm1;

        var r = new InterpResult { Mask0 = sm0, Mask1 = sm1, Overf0 = overf0, Overf1 = overf1 };

        if (!interp1 && (st.Ctrl0 & CTRL_BLEND) != 0)
        {
            // Blend (2.3.1.6.2): lane 0 yields the 8-bit alpha, lane 1 the interpolation of BASE0 and BASE1,
            // and FULL leaves lane 1 out. The 40-bit intermediate is signed or unsigned per LANE1.SIGNED.
            var alpha = sm1 & 0xFF;
            var signed1 = (st.Ctrl1 & CTRL_SIGNED) != 0;
            long b0 = signed1 ? (int)st.Base0 : st.Base0;
            long b1 = signed1 ? (int)st.Base1 : st.Base1;
            r.Result0 = alpha;
            r.Result1 = (uint)(b0 + ((alpha * (b1 - b0)) >> 8));
            r.Result2 = st.Base2 + sm0;
        }
        else if (interp1 && (st.Ctrl0 & CTRL_CLAMP) != 0)
        {
            // Clamp (2.3.1.6.3): lane 0 is the lane value held between BASE0 and BASE1, nothing added
            var clamped = sm0;
            if ((st.Ctrl0 & CTRL_SIGNED) != 0)
            {
                if ((int)clamped > (int)st.Base1) clamped = st.Base1;
                if ((int)clamped < (int)st.Base0) clamped = st.Base0;
            }
            else
            {
                if (clamped > st.Base1) clamped = st.Base1;
                if (clamped < st.Base0) clamped = st.Base0;
            }
            r.Result0 = clamped;
            r.Result1 = val1 + st.Base1;
            r.Result2 = st.Base2 + sm0 + sm1;
        }
        else
        {
            r.Result0 = val0 + st.Base0;
            r.Result1 = val1 + st.Base1;
            r.Result2 = st.Base2 + sm0 + sm1;
        }

        // FORCE_MSB is ORed into bits 29:28 of what the processor reads; the internal datapath is untouched
        r.PresentedResult0 = r.Result0 | (((st.Ctrl0 >> 19) & 3) << 28);
        r.PresentedResult1 = r.Result1 | (((st.Ctrl1 >> 19) & 3) << 28);
        return r;
    }

    /// <summary>A POPx read: the same value as PEEKx, and the lane results are written back to the accumulators
    /// (swapped when a lane's CROSS_RESULT is set).</summary>
    private static InterpResult Pop(ref InterpState st, bool interp1)
    {
        var r = Evaluate(ref st, interp1);
        st.Accum0 = (st.Ctrl0 & CTRL_CROSS_RESULT) != 0 ? r.Result1 : r.Result0;
        st.Accum1 = (st.Ctrl1 & CTRL_CROSS_RESULT) != 0 ? r.Result0 : r.Result1;
        return r;
    }

    // ── Divider ──────────────────────────────────────────────────────

    private void PerformDivide(int core)
    {
        ref var d = ref _div[core];
        (core == 1 && _cpu1 is not null ? _cpu1 : _cpu).Cycles += 8;   // the issuing core waits out the 8-cycle divide
        d.Csr = 0x2;  // READY, not DIRTY

        if (d.Signed)
        {
            var dividend = (int)d.Dividend;
            var divisor  = (int)d.Divisor;
            if (divisor == 0)
            {
                // RP2040 TRM §2.3.1.6: for signed div-by-zero:
                //   quotient = +1 when dividend >= 0  (0x00000001)
                //   quotient = -1 when dividend <  0  (0xFFFFFFFF)
                // remainder = dividend in both cases.
                d.Quotient  = dividend >= 0 ? 1u : 0xFFFFFFFF;
                d.Remainder = d.Dividend;
            }
            else if (dividend == int.MinValue && divisor == -1)
            {
                // The one case C# throws on: the true quotient is not representable. Silicon wraps.
                d.Quotient  = 0x80000000;
                d.Remainder = 0;
            }
            else
            {
                d.Quotient  = (uint)(dividend / divisor);
                d.Remainder = (uint)(dividend % divisor);
            }
        }
        else
        {
            if (d.Divisor == 0)
            {
                d.Quotient  = 0xFFFFFFFF;
                d.Remainder = d.Dividend;
            }
            else
            {
                d.Quotient  = d.Dividend / d.Divisor;
                d.Remainder = d.Dividend % d.Divisor;
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

    public void Reset()
    {
        var changed = _gpioOut | _gpioOe;
        _gpioOut = _gpioOe = _gpioHiOut = _gpioHiOe = 0;
        Array.Clear(_div);
        Array.Clear(_interp0);
        Array.Clear(_interp1);
        _spinLocks = 0;
        _fifo01.Clear();
        _fifo10.Clear();
        _wof0 = _roe0 = _wof1 = _roe1 = false;
        ResetMulticoreLaunch();
        NotifyGpio(changed);
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

using RP2040.Core.Memory;

namespace RP2040.Peripherals.Watchdog;

/// <summary>
/// Watchdog peripheral (0x40058000).
/// Implements SCRATCH registers (used by pico-sdk to pass boot info),
/// TICK generator, and watchdog countdown timer.
/// When CTRL_ENABLE (bit 30) is set, the timer counts down from LOAD.
/// Fires <see cref="OnReset"/> (and sets REASON_TIMER) when it reaches zero.
/// </summary>
public sealed class WatchdogPeripheral : IMemoryMappedDevice, IDeferrableTickable
{
    private const uint CTRL      = 0x00;
    private const uint LOAD      = 0x04;
    private const uint REASON    = 0x08;
    private const uint SCRATCH0  = 0x0C;
    private const uint SCRATCH7  = 0x28;   // SCRATCH0 + 7*4
    private const uint TICK      = 0x2C;

    // CTRL bits
    private const uint CTRL_TRIGGER    = 1u << 31;
    private const uint CTRL_ENABLE     = 1u << 30;
    private const uint CTRL_PAUSE_DBG0 = 1u << 26;
    private const uint CTRL_PAUSE_DBG1 = 1u << 25;
    private const uint CTRL_PAUSE_JTAG = 1u << 24;
    private const uint CTRL_TIME_MASK  = 0x00FFFFFF; // bits [23:0] = remaining time (µs × 2)

    // REASON bits (per RP2040 TRM §4.7.6 and rp2040js)
    private const uint REASON_TIMER = 1u << 0;  // bit 0: watchdog countdown reached zero
    private const uint REASON_FORCE = 1u << 1;  // bit 1: CTRL[TRIGGER] was written

    // TICK bits: [8:0] = CYCLES (divider), [9] = ENABLE, [10] = RUNNING, [19:11] = COUNT
    private const uint TICK_RUNNING = 1u << 10;
    private const uint TICK_ENABLE  = 1u << 9;

    // Per RP2040-E1 errata the watchdog counter decrements TWICE per 1 MHz tick (hence pico-sdk loading
    // delay_ms * 2000, WATCHDOG_XFACTOR = 2). At 125 MHz that is one decrement per 62.5 cycles. (The model used
    // to decrement once per microsecond "so load values map intuitively to microseconds"; the SDK already
    // compensates for the errata, so that made every watchdog take twice as long as requested.)
    private const long HALF_CYCLES_PER_WDOG_TICK = 125;   // _accumUs counts half-cycles: 2 per CPU cycle

    private uint _ctrl;
    private uint _load;
    private uint _reason;
    private uint _tick = TICK_ENABLE | TICK_RUNNING | 12;  // enabled, running, 12 cycles (default)
    private readonly uint[] _scratch = new uint[8];

    private long _accumUs;   // accumulated sub-decrement time, in half CPU cycles
    private uint _countDown; // current countdown in µs×2 (mirrors CTRL[23:0])

    /// <summary>Invoked when the watchdog timer expires. Simulate a system reset here.</summary>
    public Action? OnReset { get; set; }

    public uint Size => 0x1000;

    public uint ReadWord(uint address)
    {
        if (address >= SCRATCH0 && address <= SCRATCH7)
            return _scratch[(address - SCRATCH0) >> 2];

        return address switch
        {
            CTRL   => (_ctrl & ~CTRL_TIME_MASK) | (_countDown & CTRL_TIME_MASK),
            LOAD   => _load,
            REASON => _reason,
            TICK   => _tick,
            _      => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (address >= SCRATCH0 && address <= SCRATCH7)
        {
            _scratch[(address - SCRATCH0) >> 2] = value;
            return;
        }

        switch (address)
        {
            case CTRL:
                // TRIGGER is a write-only strobe — writing it forces an immediate reset
                if ((value & CTRL_TRIGGER) != 0)
                {
                    _reason = REASON_FORCE;
                    OnReset?.Invoke();
                }
                _ctrl = value & ~(CTRL_TRIGGER | CTRL_TIME_MASK); // TRIGGER and TIME are not stored
                // Loading a new CTRL with ENABLE: reload countdown from LOAD
                if ((value & CTRL_ENABLE) != 0)
                {
                    _countDown = _load & CTRL_TIME_MASK;
                    _accumUs   = 0;
                }
                break;
            case LOAD:
                _load = value & CTRL_TIME_MASK;
                // Writing LOAD also reloads the running countdown (matches hardware)
                _countDown = _load;
                _accumUs   = 0;
                break;
            case TICK:
                // ENABLE bit and CYCLES field; RUNNING and COUNT are read-only status
                _tick = (_tick & ~0x3FFu) | (value & 0x3FF);
                _tick = (_tick & ~TICK_RUNNING) | ((value & TICK_ENABLE) != 0 ? TICK_RUNNING : 0u);
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

    // ── ITickable ─────────────────────────────────────────────────────────

    /// <summary>Cycles until the countdown reaches zero (the reset); none while disabled or stopped at zero.</summary>
    public long CyclesUntilEvent()
    {
        if ((_ctrl & CTRL_ENABLE) == 0 || _countDown == 0) return long.MaxValue;
        // smallest c with (_accumUs + 2c) / 125 >= _countDown
        return Math.Max(0, ((long)_countDown * HALF_CYCLES_PER_WDOG_TICK - _accumUs + 1) / 2);
    }

    public void Tick(long deltaCycles)
    {
        if ((_ctrl & CTRL_ENABLE) == 0) return;
        if (_countDown == 0) return;

        _accumUs += deltaCycles * 2;
        var ticks = _accumUs / HALF_CYCLES_PER_WDOG_TICK;   // decrement ticks at 2 MHz per RP2040-E1
        _accumUs %= HALF_CYCLES_PER_WDOG_TICK;

        if (ticks <= 0) return;

        if (ticks >= _countDown)
        {
            _countDown = 0;
            _reason    = REASON_TIMER;
            _ctrl     &= ~CTRL_ENABLE;          // hardware clears ENABLE on reset
            OnReset?.Invoke();
        }
        else
        {
            _countDown -= (uint)ticks;
        }
    }
}

using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Timer;

/// <summary>
/// RP2040 Timer peripheral (base 0x40054000).
/// Maintains a 64-bit microsecond counter driven by <see cref="Tick"/>.
/// Four alarms fire when the lower 32 bits of the counter match their values.
/// </summary>
public sealed class TimerPeripheral : IMemoryMappedDevice, ITickable
{
    private const uint TIMEHW    = 0x000;
    private const uint TIMELW    = 0x004;
    private const uint TIMEHR    = 0x008;
    private const uint TIMELR    = 0x00C;
    private const uint ALARM0    = 0x010;
    private const uint ALARM1    = 0x014;
    private const uint ALARM2    = 0x018;
    private const uint ALARM3    = 0x01C;
    private const uint ARMED     = 0x020;
    private const uint TIMERAWH  = 0x024;
    private const uint TIMERAWL  = 0x028;
    private const uint DBGPAUSE  = 0x02C;
    private const uint PAUSE     = 0x030;
    private const uint INTR      = 0x034;
    private const uint INTE      = 0x038;
    private const uint INTF      = 0x03C;
    private const uint INTS      = 0x040;

    private readonly CortexM0Plus _cpu;
    private readonly uint _clkHz;

    // 64-bit microsecond counter (fractional accumulator for sub-us cycles)
    private long   _cycleAccum;
    private ulong  _timeMicros;

    // Cycle count at the last Tick, so a read landing mid-block can fold in what the core has run
    // since. See LiveMicros.
    private long   _cyclesAtLastTick;
    private ulong  _lastReported;

    // Latched high word when timelr is read (for consistent 64-bit reads)
    private uint _latchedHigh;

    private readonly uint[] _alarm = new uint[4];
    private uint _armed;   // bit N = 1 means alarm N is enabled

    // ── Read-only inspection surface (playground/tests) ──────────────────────
    /// <summary>Live 64-bit microsecond counter (TIMERAWH:TIMERAWL).</summary>
    public ulong GetCounter() => LiveMicros();
    public uint GetAlarm(int i) => (uint)i < 4 ? _alarm[i] : 0;
    public bool IsArmed(int i)  => (uint)i < 4 && (_armed & (1u << i)) != 0;
    private uint _intr;    // raw interrupt status (written 1 to clear)
    private uint _inte;    // interrupt enable
    private uint _intf;    // forced interrupt

    public uint Size => 0x1000;

    public TimerPeripheral(CortexM0Plus cpu, uint clkHz = 125_000_000)
    {
        _cpu = cpu;
        _clkHz = clkHz;
    }

    // ── ITickable ────────────────────────────────────────────────────

    public void Tick(long deltaCycles)
    {
        _cyclesAtLastTick = _cpu.Cycles;
        _cycleAccum += deltaCycles;

        // Convert accumulated cycles to microseconds
        var us = _cycleAccum * 1_000_000 / _clkHz;
        if (us <= 0) return;

        _cycleAccum -= us * _clkHz / 1_000_000;
        _timeMicros += (ulong)us;

        // Check alarms (compare lower 32 bits with wrap-around).
        // Use unsigned elapsed distance: elapsed = (low - alarm) as uint.
        // If elapsed < 2^31 the alarm is in the past or at the current moment (fire it).
        // This correctly handles 32-bit counter wrap-around without false-fires or missed alarms.
        var low = (uint)_timeMicros;
        for (var i = 0; i < 4; i++)
        {
            if ((_armed & (1u << i)) == 0) continue;
            if (unchecked(low - _alarm[i]) < 0x80000000u)
            {
                _armed &= ~(1u << i);
                _intr  |=  (1u << i);
                if ((_inte & (1u << i)) != 0)
                    _cpu.SetInterrupt(i, true);   // Timer IRQ 0-3 = hardware IRQ 0-3
            }
        }
    }

    /// <summary>
    /// The microsecond counter as it stands <em>right now</em>, rather than as of the last
    /// <see cref="Tick"/>.
    ///
    /// <para>A host drives the machine in blocks — <c>Run(n)</c> executes n instructions and only
    /// then ticks the peripherals — so between those boundaries the counter used to stand still. Any
    /// firmware that waits by watching this register (<c>busy_wait_us</c>, and through it every
    /// bit-banged bus, plus the polling loops inside the I2C and SPI drivers) therefore could not
    /// leave its loop until the block ended, whatever delay it had asked for. A 5 µs wait cost
    /// whatever remained of the block: with 125 000-cycle blocks, milliseconds. Bit-banged buses ran
    /// three orders of magnitude below their nominal rate as a result.
    ///
    /// <para>Resolving the counter from the core's own cycle count at the moment of the read fixes
    /// that exactly and for free: no sub-blocking, no extra bookkeeping on the hot path, and the
    /// value is the same one the silicon would report. Alarms stay on <see cref="Tick"/> — they are
    /// interrupt sources, and firing them mid-block would re-enter the CPU from inside its own run
    /// loop.</para>
    /// </summary>
    private ulong LiveMicros()
    {
        var pending = _cpu.Cycles - _cyclesAtLastTick;
        var micros  = pending > 0
            ? _timeMicros + (ulong)((_cycleAccum + pending) * 1_000_000 / _clkHz)
            : _timeMicros;
        // Two cores share this counter and Tick charges it the longer of the two, so a read taken
        // from the core that is behind could otherwise step backwards. Time never does that.
        if (micros < _lastReported) micros = _lastReported;
        _lastReported = micros;
        return micros;
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        var now = LiveMicros();
        return address switch
        {
            TIMEHW   => 0,
            TIMELW   => 0,
            TIMEHR   => _latchedHigh,   // returns value latched when TIMELR was read
            TIMELR   =>
                // Reading TIMELR latches TIMEHR for a coherent 64-bit read
                (_ = (_latchedHigh = (uint)(now >> 32)),
                 (uint)now).Item2,
            TIMERAWH => (uint)(now >> 32),
            TIMERAWL => (uint)now,
            ALARM0   => _alarm[0],
            ALARM1   => _alarm[1],
            ALARM2   => _alarm[2],
            ALARM3   => _alarm[3],
            ARMED    => _armed,
            INTR     => _intr,
            INTE     => _inte,
            INTF     => _intf,
            INTS     => (_intr | _intf) & _inte,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case TIMEHW:
                // High word written first; no-op in sim (low write triggers)
                break;
            case TIMELW:
                // Setting timer (e.g. during boot); snap to a specific time
                _timeMicros = ((ulong)_latchedHigh << 32) | value;
                // Firmware is allowed to wind this counter BACKWARDS, so the write has to clear the
                // monotonic floor that LiveMicros keeps and re-base the mid-block cycle origin.
                // Otherwise the floor silently swallows the write and the counter keeps reporting the
                // old, larger value.
                _lastReported     = _timeMicros;
                _cyclesAtLastTick = _cpu.Cycles;
                _cycleAccum       = 0;
                break;
            case ALARM0: WriteAlarm(0, value); break;
            case ALARM1: WriteAlarm(1, value); break;
            case ALARM2: WriteAlarm(2, value); break;
            case ALARM3: WriteAlarm(3, value); break;
            case ARMED:
                _armed &= ~value;   // write 1 to disarm
                break;
            case INTR:
                _intr &= ~value;    // write 1 to clear raw IRQ
                // De-assert any timer NVIC line whose effective interrupt (INTR|INTF)&INTE is now clear.
                // Without this the level-high IRQ re-fires every instruction after the handler clears it,
                // and a firmware that arms a periodic alarm (e.g. the Pico W cyw43/lwIP boot) spins forever
                // in alarm_pool_irq_handler. Only touched on the clear path — no per-tick NVIC churn.
                for (var i = 0; i < 4; i++)
                    if ((((_intr | _intf) & _inte) & (1u << i)) == 0)
                        _cpu.SetInterrupt(i, false);
                break;
            case INTE:
                _inte = value & 0xF;
                break;
            case INTF:
                _intf = value & 0xF;
                // Force-trigger interrupts for set bits
                for (var i = 0; i < 4; i++)
                    if ((_intf & (1u << i)) != 0 && (_inte & (1u << i)) != 0)
                        _cpu.SetInterrupt(i, true);
                break;
        }
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 2) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    private void WriteAlarm(int idx, uint value)
    {
        _alarm[idx] = value;
        _armed |= 1u << idx;   // arming happens automatically on alarm write
    }
}

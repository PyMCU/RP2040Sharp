using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Rtc;

/// <summary>
/// Real-Time Clock peripheral (0x4005C000).
/// Advances 1 second per 125M CPU cycles (CLK_SYS = 125 MHz).
/// Fires IRQ 25 (RTC_IRQ) when the enabled alarm matches the current time.
/// </summary>
public sealed class RtcPeripheral : IMemoryMappedDevice, ITickable
{
    private const uint RTC_SETUP0   = 0x04;   // YEAR[27:16], MONTH[11:8], DAY[4:0]
    private const uint RTC_SETUP1   = 0x08;   // DOTW[26:24], HOUR[20:16], MIN[13:8], SEC[5:0]
    private const uint RTC_CTRL     = 0x0C;   // ENABLE[0], ACTIVE[1], LOAD[4]
    private const uint IRQ_SETUP_0  = 0x10;
    private const uint IRQ_SETUP_1  = 0x14;
    private const uint RTC_RTC1     = 0x18;   // RTC_1 read reg = YEAR/MONTH/DAY  (NB: the read regs SWAP layout vs SETUP)
    private const uint RTC_RTC0     = 0x1C;   // RTC_0 read reg = DOTW/HOUR/MIN/SEC

    private const uint CTRL_ENABLE  = 1u;
    private const uint CTRL_ACTIVE  = 1u << 1;
    private const uint CTRL_LOAD    = 1u << 4;

    // IRQ_SETUP_0 bit masks
    // ENA bits are one position above the MSB of each value field to avoid overlap
    private const uint IRQ0_MATCH_ENA  = 1u << 31;
    private const uint IRQ0_YEAR_ENA   = 1u << 28;  // YEAR [27:16] — bit above field OK
    private const uint IRQ0_MONTH_ENA  = 1u << 12;  // MONTH [11:8] — ENA at 12, not 11
    private const uint IRQ0_DAY_ENA    = 1u << 5;   // DAY [4:0]   — ENA at 5, not 4

    // IRQ_SETUP_1 bit masks
    private const uint IRQ1_MATCH_ACTIVE = 1u << 31;
    private const uint IRQ1_DOTW_ENA   = 1u << 28;  // DOTW [26:24] — bit 28 OK (gap at 27)
    private const uint IRQ1_HOUR_ENA   = 1u << 21;  // HOUR [20:16] — ENA at 21, not 20
    private const uint IRQ1_MIN_ENA    = 1u << 14;  // MIN  [13:8]  — ENA at 14, not 13
    private const uint IRQ1_SEC_ENA    = 1u << 6;   // SEC  [5:0]   — ENA at 6, not 5

    private const int  RTC_IRQ        = 25;
    private const long CLK_HZ         = 125_000_000; // 125 MHz

    private readonly CortexM0Plus? _cpu;

    private uint _setup0;
    private uint _setup1;
    private uint _ctrl;
    private uint _irqSetup0;
    private uint _irqSetup1;
    // Running time registers
    private uint _rtcDate;   // YEAR[27:16] MONTH[11:8] DAY[4:0]
    private uint _rtcTime;   // DOTW[26:24] HOUR[20:16] MIN[13:8] SEC[5:0]

    private long _accumCycles;

    public uint Size => 0x1000;

    public RtcPeripheral(CortexM0Plus? cpu = null)
    {
        _cpu = cpu;
        // Default to 2024-01-01 Monday 00:00:00
        _rtcDate = (2024u << 16) | (1u << 8) | 1u;
        _rtcTime = (1u << 24); // Monday
    }

    /// <summary>Inject a specific date/time into the RTC.</summary>
    public void SetDateTime(int year, int month, int day, int dayOfWeek, int hour, int min, int sec)
    {
        _rtcDate = ((uint)year << 16) | ((uint)month << 8) | (uint)day;
        _rtcTime = ((uint)dayOfWeek << 24) | ((uint)hour << 16) | ((uint)min << 8) | (uint)sec;
    }

    // ── ITickable ─────────────────────────────────────────────────────────

    public void Tick(long deltaCycles)
    {
        if ((_ctrl & CTRL_ENABLE) == 0) return;

        _accumCycles += deltaCycles;
        while (_accumCycles >= CLK_HZ)
        {
            _accumCycles -= CLK_HZ;
            AdvanceSecond();
            CheckAlarm();
        }
    }

    // ── IMemoryMappedDevice ───────────────────────────────────────────────

    public uint ReadWord(uint address) => address switch
    {
        RTC_SETUP0  => _setup0,
        RTC_SETUP1  => _setup1,
        RTC_CTRL    => (_ctrl & CTRL_ENABLE) != 0 ? (_ctrl | CTRL_ACTIVE) : _ctrl,
        IRQ_SETUP_0 => _irqSetup0,
        IRQ_SETUP_1 => _irqSetup1,
        RTC_RTC1    => _rtcDate,   // RTC_1 (0x18) = YEAR/MONTH/DAY
        RTC_RTC0    => _rtcTime,   // RTC_0 (0x1C) = DOTW/HOUR/MIN/SEC
        _           => 0,
    };

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case RTC_SETUP0:
                _setup0 = value;
                break;
            case RTC_SETUP1:
                _setup1 = value;
                break;
            case RTC_CTRL:
                if ((value & CTRL_LOAD) != 0)
                {
                    _rtcDate = _setup0;
                    _rtcTime = _setup1;
                    _accumCycles = 0;
                }
                _ctrl = value & CTRL_ENABLE; // LOAD is strobe, ACTIVE is read-only
                break;
            case IRQ_SETUP_0: _irqSetup0 = value; break;
            case IRQ_SETUP_1:
                // bit 31 (MATCH_ACTIVE) is write-1-to-clear
                if ((value & IRQ1_MATCH_ACTIVE) != 0) _irqSetup1 &= ~IRQ1_MATCH_ACTIVE;
                _irqSetup1 = (_irqSetup1 & IRQ1_MATCH_ACTIVE) | (value & ~IRQ1_MATCH_ACTIVE);
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

    // ── Private helpers ───────────────────────────────────────────────────

    private void AdvanceSecond()
    {
        var sec   = (int)(_rtcTime & 0x3F);
        var min   = (int)((_rtcTime >> 8) & 0x3F);
        var hour  = (int)((_rtcTime >> 16) & 0x1F);
        var dotw  = (int)((_rtcTime >> 24) & 0x7);
        var day   = (int)(_rtcDate & 0x1F);
        var month = (int)((_rtcDate >> 8) & 0xF);
        var year  = (int)((_rtcDate >> 16) & 0xFFF);

        sec++;
        if (sec >= 60) { sec = 0; min++; }
        if (min >= 60) { min = 0; hour++; }
        if (hour >= 24)
        {
            hour = 0;
            dotw = (dotw + 1) % 7;
            day++;
            var daysInMonth = year > 0 && month is >= 1 and <= 12
                ? DateTime.DaysInMonth(year, month) : 31;
            if (day > daysInMonth) { day = 1; month++; }
            if (month > 12) { month = 1; year++; }
        }

        _rtcDate = ((uint)year << 16) | ((uint)month << 8) | (uint)day;
        _rtcTime = ((uint)dotw << 24) | ((uint)hour << 16) | ((uint)min << 8) | (uint)sec;
    }

    private void CheckAlarm()
    {
        if ((_irqSetup0 & IRQ0_MATCH_ENA) == 0) return;

        var sec   = _rtcTime & 0x3F;
        var min   = (_rtcTime >> 8) & 0x3F;
        var hour  = (_rtcTime >> 16) & 0x1F;
        var dotw  = (_rtcTime >> 24) & 0x7;
        var day   = _rtcDate & 0x1F;
        var month = (_rtcDate >> 8) & 0xF;
        var year  = (_rtcDate >> 16) & 0xFFF;

        var matched = true;
        if ((_irqSetup0 & IRQ0_YEAR_ENA)  != 0) matched &= ((_irqSetup0 >> 16) & 0xFFF) == year;
        if ((_irqSetup0 & IRQ0_MONTH_ENA) != 0) matched &= ((_irqSetup0 >> 8)  & 0xF)   == month;
        if ((_irqSetup0 & IRQ0_DAY_ENA)   != 0) matched &= (_irqSetup0 & 0x1F)           == day;
        if ((_irqSetup1 & IRQ1_DOTW_ENA)  != 0) matched &= ((_irqSetup1 >> 24) & 0x7)   == dotw;
        if ((_irqSetup1 & IRQ1_HOUR_ENA)  != 0) matched &= ((_irqSetup1 >> 16) & 0x1F)  == hour;
        if ((_irqSetup1 & IRQ1_MIN_ENA)   != 0) matched &= ((_irqSetup1 >> 8)  & 0x3F)  == min;
        if ((_irqSetup1 & IRQ1_SEC_ENA)   != 0) matched &= (_irqSetup1 & 0x3F)           == sec;

        if (matched)
        {
            _irqSetup1 |= IRQ1_MATCH_ACTIVE;  // set MATCH_ACTIVE flag
            _cpu?.SetInterrupt(RTC_IRQ, true);
        }
    }
}

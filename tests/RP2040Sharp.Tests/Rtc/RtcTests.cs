using FluentAssertions;
using RP2040.Peripherals.Rtc;
using Xunit;

namespace RP2040.Peripherals.Tests.Rtc;

public class RtcTests
{
    // Register offsets
    private const uint RTC_SETUP0  = 0x04;
    private const uint RTC_SETUP1  = 0x08;
    private const uint RTC_CTRL    = 0x0C;
    private const uint IRQ_SETUP_0 = 0x10;
    private const uint IRQ_SETUP_1 = 0x14;
    private const uint RTC_RTC1    = 0x18;   // RTC_1 read reg = YEAR/MONTH/DAY
    private const uint RTC_RTC0    = 0x1C;   // RTC_0 read reg = DOTW/HOUR/MIN/SEC
    private const uint RTC_INTR    = 0x20;
    private const uint RTC_INTE    = 0x24;
    private const uint RTC_INTF    = 0x28;
    private const uint RTC_INTS    = 0x2C;

    private const uint CTRL_ENABLE = 1u;
    private const uint CTRL_ACTIVE = 1u << 1;
    private const uint CTRL_LOAD   = 1u << 4;

    // Layout per RP2040 datasheet 4.8.7 / SVD: SETUP_0, IRQ_SETUP_0 and RTC_1 hold YEAR[23:12] MONTH[11:8]
    // DAY[4:0]; SETUP_1, IRQ_SETUP_1 and RTC_0 hold DOTW[26:24] HOUR[20:16] MIN[13:8] SEC[5:0]. The enables
    // sit above the value fields: IRQ_SETUP_0 MATCH_ACTIVE[29] (RO) MATCH_ENA[28] YEAR_ENA[26] MONTH_ENA[25]
    // DAY_ENA[24]; IRQ_SETUP_1 DOTW_ENA[31] HOUR_ENA[30] MIN_ENA[29] SEC_ENA[28].
    private const int  YEAR_SHIFT         = 12;
    private const uint IRQ0_MATCH_ACTIVE = 1u << 29;
    private const uint IRQ0_MATCH_ENA    = 1u << 28;
    private const uint IRQ1_SEC_ENA      = 1u << 28;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RtcPeripheral MakeEnabled()
    {
        var rtc = new RtcPeripheral();
        // Load setup and enable
        rtc.WriteWord(RTC_SETUP0, (2024u << YEAR_SHIFT) | (1u << 8) | 1u);  // 2024-01-01
        rtc.WriteWord(RTC_SETUP1, (1u << 24));                        // Monday 00:00:00
        rtc.WriteWord(RTC_CTRL, CTRL_LOAD | CTRL_ENABLE);
        return rtc;
    }

    // ── Default registers ─────────────────────────────────────────────────

    [Fact]
    public void DefaultTime_Is_20240101_Monday_000000()
    {
        var rtc = new RtcPeripheral();
        // RTC_1: YEAR[23:12]=2024, MONTH[11:8]=1, DAY[4:0]=1
        var rtc0 = rtc.ReadWord(RTC_RTC1);
        ((rtc0 >> YEAR_SHIFT) & 0xFFF).Should().Be(2024);
        ((rtc0 >> 8)  & 0xF)  .Should().Be(1);
        (rtc0 & 0x1F)          .Should().Be(1);

        // RTC_0: DOTW[26:24]=1 (Monday)
        var rtc1 = rtc.ReadWord(RTC_RTC0);
        ((rtc1 >> 24) & 0x7).Should().Be(1);
        ((rtc1 >> 16) & 0x1F).Should().Be(0); // hour
        ((rtc1 >> 8)  & 0x3F).Should().Be(0); // min
        (rtc1 & 0x3F)         .Should().Be(0); // sec
    }

    // ── CTRL LOAD latches SETUP values ───────────────────────────────────

    [Fact]
    public void CtrlLoad_Latches_Setup0_And_Setup1()
    {
        var rtc = new RtcPeripheral();
        rtc.WriteWord(RTC_SETUP0, (2025u << YEAR_SHIFT) | (6u << 8) | 15u); // 2025-06-15
        rtc.WriteWord(RTC_SETUP1, (3u << 24) | (10u << 16) | (30u << 8) | 45u); // Wed 10:30:45

        rtc.WriteWord(RTC_CTRL, CTRL_LOAD | CTRL_ENABLE);

        var rtc0   = rtc.ReadWord(RTC_RTC1);
        var rtc1   = rtc.ReadWord(RTC_RTC0);

        ((rtc0 >> YEAR_SHIFT) & 0xFFF).Should().Be(2025);
        ((rtc0 >> 8)  & 0xF)  .Should().Be(6);
        (rtc0 & 0x1F)          .Should().Be(15);

        ((rtc1 >> 24) & 0x7)  .Should().Be(3);  // Wed
        ((rtc1 >> 16) & 0x1F) .Should().Be(10);
        ((rtc1 >> 8)  & 0x3F) .Should().Be(30);
        (rtc1 & 0x3F)          .Should().Be(45);
    }

    // ── CTRL ACTIVE reflects CTRL ENABLE ─────────────────────────────────

    [Fact]
    public void CtrlActive_Reflects_Enable()
    {
        var rtc = new RtcPeripheral();

        // Disabled: ACTIVE should be 0
        rtc.WriteWord(RTC_CTRL, 0);
        (rtc.ReadWord(RTC_CTRL) & CTRL_ACTIVE).Should().Be(0);

        // Enabled: ACTIVE should be set
        rtc.WriteWord(RTC_CTRL, CTRL_ENABLE);
        (rtc.ReadWord(RTC_CTRL) & CTRL_ACTIVE).Should().NotBe(0u);
    }

    // ── Tick advances time ───────────────────────────────────────────────

    [Fact]
    public void Tick_125M_Cycles_Advances_One_Second()
    {
        var rtc = MakeEnabled();

        rtc.Tick(125_000_000L);

        var rtc1 = rtc.ReadWord(RTC_RTC0);
        (rtc1 & 0x3F).Should().Be(1); // sec = 1
    }

    [Fact]
    public void Tick_Disabled_Does_Not_Advance()
    {
        var rtc = new RtcPeripheral();
        // Don't enable
        rtc.WriteWord(RTC_CTRL, 0);

        rtc.Tick(125_000_000L * 60);

        var rtc1 = rtc.ReadWord(RTC_RTC0);
        (rtc1 & 0x3F).Should().Be(0); // still 0
    }

    [Fact]
    public void Tick_Rolls_Seconds_Into_Minutes()
    {
        var rtc = MakeEnabled();

        rtc.Tick(125_000_000L * 60); // 60 seconds

        var rtc1 = rtc.ReadWord(RTC_RTC0);
        (rtc1 & 0x3F)          .Should().Be(0); // sec = 0
        ((rtc1 >> 8)  & 0x3F)  .Should().Be(1); // min = 1
    }

    [Fact]
    public void Tick_Rolls_Day_And_Increments_DayOfWeek()
    {
        var rtc = MakeEnabled(); // Monday 2024-01-01

        rtc.Tick(125_000_000L * 86400); // 24 hours

        var rtc0 = rtc.ReadWord(RTC_RTC1);
        var rtc1 = rtc.ReadWord(RTC_RTC0);

        (rtc0 & 0x1F)          .Should().Be(2);  // day = 2
        ((rtc1 >> 24) & 0x7)   .Should().Be(2);  // Tuesday
    }

    // ── SetDateTime helper ───────────────────────────────────────────────

    [Fact]
    public void SetDateTime_Updates_Running_Registers()
    {
        var rtc = new RtcPeripheral();
        rtc.SetDateTime(2030, 12, 31, 5, 23, 59, 59); // Fri 23:59:59

        var rtc0 = rtc.ReadWord(RTC_RTC1);
        var rtc1 = rtc.ReadWord(RTC_RTC0);

        ((rtc0 >> YEAR_SHIFT) & 0xFFF).Should().Be(2030);
        ((rtc0 >> 8)  & 0xF)  .Should().Be(12);
        (rtc0 & 0x1F)          .Should().Be(31);
        ((rtc1 >> 24) & 0x7)  .Should().Be(5);   // Fri
        ((rtc1 >> 16) & 0x1F) .Should().Be(23);
        ((rtc1 >> 8)  & 0x3F) .Should().Be(59);
        (rtc1 & 0x3F)          .Should().Be(59);
    }

    // ── Alarm ────────────────────────────────────────────────────────────

    [Fact]
    public void Alarm_SecMatch_Sets_MatchActive()
    {
        var rtc = MakeEnabled(); // 2024-01-01 Monday 00:00:00

        // Configure alarm: match second=1, enable MATCH_ENA + SEC_ENA
        // IRQ_SETUP_0: MATCH_ENA bit 28
        // IRQ_SETUP_1: SEC_ENA bit 28, SEC value bits[5:0]
        rtc.WriteWord(IRQ_SETUP_0, IRQ0_MATCH_ENA);
        rtc.WriteWord(IRQ_SETUP_1, IRQ1_SEC_ENA | 1u); // match when sec=1

        rtc.Tick(125_000_000L); // advance 1 second → sec=1

        // MATCH_ACTIVE (bit 29 of IRQ_SETUP_0, read-only) should be set, and it shows in INTR
        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().NotBe(0u);
        rtc.ReadWord(RTC_INTR).Should().Be(1u);
    }

    [Fact]
    public void Alarm_MatchActive_Is_Cleared_By_Clearing_MatchEna()
    {
        // Datasheet 4.8.7 IRQ_SETUP_0: MATCH_ACTIVE is read-only and "cleared by clearing MATCH_ENA".
        var rtc = MakeEnabled();
        rtc.WriteWord(IRQ_SETUP_0, IRQ0_MATCH_ENA);
        rtc.WriteWord(IRQ_SETUP_1, IRQ1_SEC_ENA | 1u);

        rtc.Tick(125_000_000L); // trigger alarm
        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().NotBe(0u);

        // Writing the read-only bit does not clear it
        rtc.WriteWord(IRQ_SETUP_0, IRQ0_MATCH_ENA | IRQ0_MATCH_ACTIVE);
        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().NotBe(0u);

        rtc.WriteWord(IRQ_SETUP_0, 0);
        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().Be(0u);
        rtc.ReadWord(RTC_INTR).Should().Be(0u);
    }

    [Fact]
    public void Alarm_NoMatch_When_EnableOff()
    {
        var rtc = MakeEnabled();
        // MATCH_ENA is 0 — alarm disabled
        rtc.WriteWord(IRQ_SETUP_0, 0);
        rtc.WriteWord(IRQ_SETUP_1, IRQ1_SEC_ENA | 1u);

        rtc.Tick(125_000_000L);

        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().Be(0u);
    }

    [Fact]
    public void Alarm_DoesNotFire_When_SecMismatch()
    {
        var rtc = MakeEnabled();
        rtc.WriteWord(IRQ_SETUP_0, IRQ0_MATCH_ENA);
        rtc.WriteWord(IRQ_SETUP_1, IRQ1_SEC_ENA | 5u); // match sec=5

        rtc.Tick(125_000_000L); // only sec=1

        (rtc.ReadWord(IRQ_SETUP_0) & IRQ0_MATCH_ACTIVE).Should().Be(0u);
    }

    // ── INTR / INTE / INTF / INTS (datasheet 4.8.7, offsets 0x20-0x2C) ────

    [Fact]
    public void Ints_Is_Intr_Or_Intf_Masked_By_Inte()
    {
        var rtc = MakeEnabled();
        rtc.WriteWord(IRQ_SETUP_0, IRQ0_MATCH_ENA);
        rtc.WriteWord(IRQ_SETUP_1, IRQ1_SEC_ENA | 1u);
        rtc.Tick(125_000_000L);

        rtc.ReadWord(RTC_INTR).Should().Be(1u);
        rtc.ReadWord(RTC_INTS).Should().Be(0u, "INTE is still 0");
        rtc.WriteWord(RTC_INTE, 1);
        rtc.ReadWord(RTC_INTS).Should().Be(1u);
    }

    [Fact]
    public void Intf_Forces_Ints_Without_An_Alarm()
    {
        var rtc = MakeEnabled();
        rtc.WriteWord(RTC_INTE, 1);
        rtc.WriteWord(RTC_INTF, 1);

        rtc.ReadWord(RTC_INTR).Should().Be(0u);
        rtc.ReadWord(RTC_INTS).Should().Be(1u);
    }
}

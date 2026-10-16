// SPDX-License-Identifier: BUSL-1.1
using FluentAssertions;
using RP2040.Peripherals;
using Xunit;

namespace RP2040Sharp.Tests.Peripherals;

/// <summary>
/// The microsecond counter resolves against the core's live cycle count, not just the last peripheral
/// tick, so firmware that waits by watching it (busy_wait_us, and through it every bit-banged bus) can
/// leave its loop mid-block. These pin both halves of that: the counter must move within a block, and
/// the monotonic floor that keeps it from stepping backwards must not swallow a firmware write.
/// </summary>
public class TimerLiveCounterTests
{
    private const uint TIMEHW = 0x40054000, TIMELW = 0x40054004, TIMERAWL = 0x40054028;

    // A bare machine has an empty boot ROM, and running off the end of it now faults (the ROM is 16 KiB,
    // datasheet 2.2) instead of wrapping around forever, so give core 0 a loop to spin in.
    private static RP2040Machine Spinning()
    {
        var m = new RP2040Machine();
        m.Bus.WriteHalfWord(0x20000000, 0xE7FE);   // b .
        m.Cpu.Registers.PC = 0x20000000;
        return m;
    }

    [Fact]
    public void Counter_advances_within_a_run_block()
    {
        using var m = Spinning();
        m.Run(4096);
        var before = m.Bus.ReadWord(TIMERAWL);
        var cyclesBefore = m.Cpu.Cycles;
        m.Cpu.Run(200_000);                       // core advances; peripherals not ticked
        var after = m.Bus.ReadWord(TIMERAWL);

        // The cycles the core ran, at 125 MHz (the spin loop's taken branch costs 2 cycles, so this is
        // 3200 us). Without a live read this stayed put until the block ended.
        var elapsedUs = (uint)((m.Cpu.Cycles - cyclesBefore) / 125);
        (after - before).Should().BeCloseTo(elapsedUs, 8u);
    }

    [Fact]
    public void A_firmware_write_can_wind_the_counter_backwards()
    {
        using var m = Spinning();
        for (var i = 0; i < 20; i++) m.Run(50_000);
        m.Bus.ReadWord(TIMERAWL).Should().BeGreaterThan(1_000);

        // RP2040 datasheet 4.6: TIMELW is not copied to the counter until TIMEHW is written, so the low word
        // goes first and the high-word write commits the pair.
        m.Bus.WriteWord(TIMELW, 1_000);
        m.Bus.WriteWord(TIMEHW, 0);

        // The counter is writable (datasheet 4.6.5); the live-counter floor must not outvote the write.
        m.Bus.ReadWord(TIMERAWL).Should().Be(1_000);
    }
}

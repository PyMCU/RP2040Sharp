using FluentAssertions;
using RP2040.Peripherals.Watchdog;
using Xunit;

namespace RP2040.Peripherals.Tests.Watchdog;

public class WatchdogTests
{
    private const uint CTRL     = 0x00;
    private const uint LOAD     = 0x04;
    private const uint REASON   = 0x08;
    private const uint SCRATCH0 = 0x0C;
    private const uint SCRATCH7 = 0x28;
    private const uint TICK     = 0x2C;

    private const uint CTRL_TRIGGER = 1u << 31;
    private const uint CTRL_ENABLE  = 1u << 30;
    private const uint REASON_TIMER = 1u << 0;  // bit 0 per RP2040 TRM §4.7.6
    private const uint REASON_FORCE = 1u << 1;  // bit 1 per RP2040 TRM §4.7.6
    private const uint TICK_ENABLE  = 1u << 9;
    private const uint TICK_RUNNING = 1u << 10;

    // 1 µs = 125 CPU cycles at 125 MHz
    private const long CYCLES_PER_US = 125;

    // ── SCRATCH registers ────────────────────────────────────────────────

    [Fact]
    public void Scratch0_through_7_are_read_write()
    {
        var wdg = new WatchdogPeripheral();
        for (uint i = 0; i < 8; i++)
        {
            var addr = SCRATCH0 + i * 4;
            wdg.WriteWord(addr, 0xDEAD_0000u | i);
            wdg.ReadWord(addr).Should().Be(0xDEAD_0000u | i, $"SCRATCH{i}");
        }
    }

    [Fact]
    public void Scratch_registers_are_independent()
    {
        var wdg = new WatchdogPeripheral();
        wdg.WriteWord(SCRATCH0, 0xAAAAAAAA);
        wdg.WriteWord(SCRATCH7, 0x55555555);

        wdg.ReadWord(SCRATCH0).Should().Be(0xAAAAAAAAu);
        wdg.ReadWord(SCRATCH7).Should().Be(0x55555555u);
    }

    // ── TICK register ────────────────────────────────────────────────────

    [Fact]
    public void Tick_register_defaults_to_running_with_12_cycles()
    {
        var wdg = new WatchdogPeripheral();
        var tick = wdg.ReadWord(TICK);

        (tick & 0x1FFu).Should().Be(12u, "CYCLES default = 12");
        (tick & TICK_ENABLE).Should().NotBe(0u, "ENABLE default = 1");
        (tick & TICK_RUNNING).Should().NotBe(0u, "RUNNING default = 1");
    }

    [Fact]
    public void Writing_tick_updates_cycles_and_running()
    {
        var wdg = new WatchdogPeripheral();
        wdg.WriteWord(TICK, TICK_ENABLE | 100u); // 100 cycles, enabled
        var tick = wdg.ReadWord(TICK);

        (tick & 0x1FFu).Should().Be(100u);
        (tick & TICK_RUNNING).Should().NotBe(0u, "running when enabled");
    }

    [Fact]
    public void Disabling_tick_clears_running()
    {
        var wdg = new WatchdogPeripheral();
        wdg.WriteWord(TICK, 12u); // ENABLE=0
        var tick = wdg.ReadWord(TICK);

        (tick & TICK_RUNNING).Should().Be(0u, "not running when disabled");
    }

    // ── CTRL TRIGGER (force reset) ────────────────────────────────────────

    [Fact]
    public void Writing_ctrl_trigger_invokes_OnReset_and_sets_REASON_FORCE()
    {
        var wdg = new WatchdogPeripheral();
        var resetCount = 0;
        wdg.OnReset = () => resetCount++;

        wdg.WriteWord(CTRL, CTRL_TRIGGER);

        resetCount.Should().Be(1, "OnReset called once");
        wdg.ReadWord(REASON).Should().Be(REASON_FORCE, "REASON_FORCE on trigger");
    }

    [Fact]
    public void Ctrl_trigger_bit_not_stored_in_ctrl()
    {
        var wdg = new WatchdogPeripheral();
        wdg.OnReset = () => { };
        wdg.WriteWord(CTRL, CTRL_TRIGGER | CTRL_ENABLE);

        (wdg.ReadWord(CTRL) & CTRL_TRIGGER).Should().Be(0u, "TRIGGER is a strobe, not stored");
    }

    // ── Watchdog countdown (ITickable) ────────────────────────────────────

    [Fact]
    public void Watchdog_does_not_fire_when_disabled()
    {
        var wdg = new WatchdogPeripheral();
        var resetCount = 0;
        wdg.OnReset = () => resetCount++;

        // 1 µs = 125 cycles, LOAD=1 (fire after 1 µs elapsed)
        wdg.WriteWord(LOAD, 1u);
        // Do NOT enable — CTRL_ENABLE not set
        wdg.Tick(CYCLES_PER_US * 10);

        resetCount.Should().Be(0, "disabled watchdog must never fire");
    }

    [Fact]
    public void Watchdog_fires_after_load_microseconds()
    {
        var wdg = new WatchdogPeripheral();
        var resetCount = 0;
        wdg.OnReset = () => resetCount++;

        // LOAD counts watchdog ticks, which run at 2 per microsecond (RP2040-E1; pico-sdk loads delay_ms * 2000):
        // LOAD = 20 is 10 µs.
        wdg.WriteWord(LOAD, 20u);
        // Enable with CTRL_ENABLE — this also reloads countdown from LOAD
        wdg.WriteWord(CTRL, CTRL_ENABLE);

        // Tick 9 µs — should not fire yet
        wdg.Tick(CYCLES_PER_US * 9);
        resetCount.Should().Be(0, "not yet expired after 9 µs");

        // Tick 1 more µs — total = 10 µs, should fire now
        wdg.Tick(CYCLES_PER_US);
        resetCount.Should().Be(1, "fired after 10 µs");
    }

    [Fact]
    public void Watchdog_sets_REASON_TIMER_when_fired()
    {
        var wdg = new WatchdogPeripheral();
        wdg.OnReset = () => { };

        wdg.WriteWord(LOAD, 1u);
        wdg.WriteWord(CTRL, CTRL_ENABLE);
        wdg.Tick(CYCLES_PER_US); // expire

        wdg.ReadWord(REASON).Should().Be(REASON_TIMER);
    }

    [Fact]
    public void Watchdog_disables_itself_after_firing()
    {
        var wdg = new WatchdogPeripheral();
        var resetCount = 0;
        wdg.OnReset = () => resetCount++;

        wdg.WriteWord(LOAD, 1u);
        wdg.WriteWord(CTRL, CTRL_ENABLE);
        wdg.Tick(CYCLES_PER_US * 100); // fire + extra ticks

        resetCount.Should().Be(1, "fires exactly once");
        (wdg.ReadWord(CTRL) & CTRL_ENABLE).Should().Be(0u, "ENABLE cleared after firing");
    }

    [Fact]
    public void Writing_load_reloads_countdown()
    {
        var wdg = new WatchdogPeripheral();
        var resetCount = 0;
        wdg.OnReset = () => resetCount++;

        // Enable with LOAD=10 (5 µs at 2 ticks/µs), tick 4 µs
        wdg.WriteWord(LOAD, 10u);
        wdg.WriteWord(CTRL, CTRL_ENABLE);
        wdg.Tick(CYCLES_PER_US * 4);
        resetCount.Should().Be(0);

        // Reload with LOAD=20 — countdown restarts from 20 (10 µs)
        wdg.WriteWord(LOAD, 20u);
        wdg.Tick(CYCLES_PER_US * 9); // 9 µs from reload — not yet
        resetCount.Should().Be(0, "not yet — countdown was reset to 10");

        wdg.Tick(CYCLES_PER_US); // 10 µs from reload — fire
        resetCount.Should().Be(1);
    }
}

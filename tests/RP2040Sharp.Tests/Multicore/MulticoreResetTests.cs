using RP2040.Peripherals.Tests.Fixtures;

namespace RP2040.Peripherals.Tests.Multicore;

/// <summary>
/// Verifies the emulator reproduces pico-sdk's <c>multicore_reset_core1()</c>, which forces
/// PROC1 off then on through <c>PSM.FRCE_OFF</c>.  On silicon this brings Core 1 out of reset;
/// Core 1 re-runs its bootrom and pushes a "ready" word to Core 0, which is blocked in
/// <c>multicore_fifo_pop_blocking()</c>.  MicroPython's <c>_thread</c> module runs this before
/// every <c>start_new_thread</c>, so without it a <c>_thread</c> firmware (e.g. the Wokwi
/// pedestrian-crossing sketch) hangs with Core 0 asleep in WFE.
/// </summary>
public sealed class MulticoreResetTests : MachineTestBase
{
    // PSM @ 0x40010000. hw_set_bits/hw_clear_bits target the atomic SET/CLR aliases.
    private const uint PSM_FRCE_OFF     = 0x40010004;
    private const uint PSM_FRCE_OFF_SET = 0x40012004;  // base + SET(0x2000) + FRCE_OFF(0x04)
    private const uint PSM_FRCE_OFF_CLR = 0x40013004;  // base + CLR(0x3000) + FRCE_OFF(0x04)
    private const uint PROC1_BIT        = 1u << 16;

    // SIO @ 0xD0000000. FIFO_ST bit0 = VLD (Core 0's RX FIFO has data).
    private const uint SIO_FIFO_ST = 0xD0000050;
    private const uint SIO_FIFO_RD = 0xD0000058;
    private const uint FIFO_ST_VLD = 1u;

    [Fact]
    public void MulticoreResetCore1_InjectsReadyWord_ForBlockedPop()
    {
        // Core 0's RX FIFO starts empty (nothing available to a blocking pop).
        (Machine.Bus.ReadWord(SIO_FIFO_ST) & FIFO_ST_VLD).Should().Be(0u,
            "Core 0's RX FIFO must be empty before the reset");

        // multicore_reset_core1(): hw_set_bits(PROC1), spin on read-back, hw_clear_bits(PROC1).
        Machine.Bus.WriteWord(PSM_FRCE_OFF_SET, PROC1_BIT);
        (Machine.Bus.ReadWord(PSM_FRCE_OFF) & PROC1_BIT).Should().Be(PROC1_BIT,
            "the PROC1 force-off bit must read back as set (the SDK spins on this)");
        Machine.Bus.WriteWord(PSM_FRCE_OFF_CLR, PROC1_BIT);

        // Releasing PROC1 must make Core 1's bootrom push its "ready" sentinel to Core 0.
        (Machine.Bus.ReadWord(SIO_FIFO_ST) & FIFO_ST_VLD).Should().Be(FIFO_ST_VLD,
            "Core 1 leaving reset must signal Core 0 via the FIFO");
        Machine.Bus.ReadWord(SIO_FIFO_RD).Should().Be(0u,
            "the ready sentinel Core 1 pushes is 0");
    }

    [Fact]
    public void MulticoreResetCore1_WakesCore0FromWfe()
    {
        Machine.Cpu.Registers.EventRegistered = false;

        Machine.Bus.WriteWord(PSM_FRCE_OFF_SET, PROC1_BIT);
        Machine.Bus.WriteWord(PSM_FRCE_OFF_CLR, PROC1_BIT);

        Machine.Cpu.Registers.EventRegistered.Should().BeTrue(
            "the reset must SEV Core 0 so a WFE inside multicore_fifo_pop_blocking wakes");
    }

    [Fact]
    public void SettingProc1_WithoutRelease_DoesNotSignalCore0()
    {
        // Only the set → clear transition releases Core 1; setting alone must be inert.
        Machine.Bus.WriteWord(PSM_FRCE_OFF_SET, PROC1_BIT);

        (Machine.Bus.ReadWord(SIO_FIFO_ST) & FIFO_ST_VLD).Should().Be(0u,
            "forcing PROC1 off must not push anything to Core 0");
    }
}

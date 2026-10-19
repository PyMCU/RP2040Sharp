namespace RP2040.Peripherals.Tests;

/// <summary>
/// <see cref="RP2040CoSim"/> with a target cycle: a run stops at the first instruction boundary at or past it (a
/// sleeping core exactly on it), and the deferred machine with stretches and bounded runs ends where the eager one
/// running every slice plainly does.
/// </summary>
public class RP2040CoSimTests
{
    // adds r0, #1 ; b .-2 (a register loop with a two-cycle branch)
    private static readonly byte[] Busy = [0x01, 0x30, 0xFD, 0xE7];
    private static readonly byte[] Sleep = [0x30, 0xBF, 0xFD, 0xE7];   // wfi ; b .-2

    private static (RP2040Machine, RP2040CoSim) Make(byte[] image, bool defer)
    {
        var m = new RP2040Machine { DeferPeripheralTicks = defer };
        m.LoadFlash(image);
        return (m, new RP2040CoSim(m) { Quantum = 16_384 });
    }

    private static RunRequest To(long target, bool fast) => new()
    {
        TargetCycle = target,
        Flags = fast ? CoSimFlags.SleepingStretches | CoSimFlags.BoundedRuns : CoSimFlags.None,
    };

    [Theory]
    [InlineData(1L)]
    [InlineData(750L)]
    [InlineData(10_001L)]
    [InlineData(1_000_000L)]
    public void A_busy_core_stops_at_the_first_instruction_boundary_at_or_past_the_target(long budget)
    {
        foreach (var (defer, fast) in new[] { (false, false), (true, false), (true, true) })
        {
            var (m, cosim) = Make(Busy, defer);
            using (m)
            {
                long start = m.Cpu.Cycles;
                var r = cosim.RunUntilEdge(To(start + budget, fast));
                Assert.Equal(RunStopReason.TargetCycle, r.Reason);
                Assert.InRange(m.Cpu.Cycles - start, budget, budget + 2);   // the longest instruction here is the branch
                Assert.Equal(m.Cpu.Cycles - start, r.Cycles);
            }
        }
    }

    [Theory]
    [InlineData(750L)]
    [InlineData(123_457L)]
    public void A_sleeping_core_stops_exactly_on_the_target(long budget)
    {
        foreach (var fast in new[] { false, true })
        {
            var (m, cosim) = Make(Sleep, true);
            using (m)
            {
                cosim.Run(10);   // into WFI
                long start = m.Cpu.Cycles;
                var r = cosim.RunUntilEdge(To(start + budget, fast));
                Assert.Equal(start + budget, m.Cpu.Cycles);
                Assert.Equal(budget, r.Cycles);
                if (fast && budget > 2 * 16_384) Assert.True(cosim.StretchedSlices > 0);
            }
        }
    }

    [Fact]
    public void Stepped_runs_to_a_target_are_the_same_eager_and_fast()
    {
        (long, uint, long) Go(bool fast)
        {
            var (m, cosim) = Make(Busy, fast);
            using (m)
            {
                for (int i = 0; i < 2000; i++) cosim.RunUntilEdge(To(m.Cpu.Cycles + 750, fast));
                return (m.Cpu.Cycles, m.Cpu.Registers.R0, m.SweptCycles);
            }
        }
        Assert.Equal(Go(false), Go(true));
    }
}

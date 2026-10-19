using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Deferred peripheral ticks, sleeping stretches and bounded awake runs against the plain slices they replace, on
/// hand-assembled firmware: identical call returns, pad events with their cycles, serial, both cores, retired counts,
/// SRAM and peripheral registers (<see cref="Differential.Check"/>), at the same guard.
/// </summary>
public class SliceDifferentialTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        string[] firmware = ["square", "busy", "sleep", "alarm-wfi", "alarm-poll", "pwm-wfi", "pwm-poll", "systick"];
        uint[] guards = [0, 1, 4, 75, 300];
        uint[] maxes = [20_000, 1_000_000];
        foreach (var f in firmware)
            foreach (var g in guards)
                foreach (var m in maxes)
                    yield return [f, g, m];
    }

    private static byte[] Image(string name) => name switch
    {
        "square" => Firmware.SquareWave(20, 37),
        "busy" => Firmware.BusyLoop(),
        "sleep" => Firmware.SleepForever(),
        "alarm-wfi" => Firmware.AlarmIsr(37),
        "alarm-poll" => Firmware.AlarmIsr(37, poll: true),
        "pwm-wfi" => Firmware.PwmIsr(),
        "pwm-poll" => Firmware.PwmIsr(poll: true),
        "systick" => Firmware.SysTickIsr(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Same_result_as_plain_slices(string firmware, uint guard, uint max)
    {
        var uf2 = Firmware.Uf2(Image(firmware));
        const ulong span = 125 * 1500; // 1.5 ms
        var probe = Differential.Check(run =>
        {
            Assert.Equal(1, Abi.LoadUf2(run.H, uf2));
            int call = 0;
            while (run.Cycles < span)
            {
                if (guard == 0) run.Run(max);
                else run.RunUntilEdge(max, guard);
                // A host input now and then: pad 13 follows the call count (no firmware here reads it).
                if (++call % 7 == 0) Abi.PinSetInput(run.H, 13, call / 7 % 3 - 1);
            }
        });
        output.WriteLine($"{firmware} guard {guard} max {max}: stretched {probe.Stretched}, bounded {probe.Bounded}, plain {probe.Plain}");
    }

    [Fact]
    public void Firmware_does_what_the_tests_assume()
    {
        // The ISR images toggle GP15: check they actually run, at a guard where every edge is its own call.
        foreach (var f in new[] { "alarm-wfi", "alarm-poll", "pwm-wfi", "pwm-poll", "systick" })
        {
            using var run = new DiffRun(SliceMode.Fast);
            Assert.Equal(1, Abi.LoadUf2(run.H, Firmware.Uf2(Image(f))));
            while (run.Cycles < 125 * 1500) run.RunUntilEdge(1_000_000, 75);
            var gp15 = run.Host.Pins.Count(p => p.Gpio == 15);
            var gp14 = run.Host.Pins.Count(p => p.Gpio == 14);
            output.WriteLine($"{f}: GP15 {gp15} events, GP14 {gp14}, stretched {run.Chip.StretchedSlices}, bounded {run.Chip.BoundedSlices}, plain {run.Chip.PlainSlices}");
            Assert.True(gp15 > 10, $"{f}: {gp15} GP15 events");
            if (f.StartsWith("pwm")) Assert.True(gp14 > 20, $"{f}: {gp14} GP14 events");
        }
    }
}

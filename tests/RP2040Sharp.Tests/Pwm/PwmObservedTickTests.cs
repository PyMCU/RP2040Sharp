using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pwm;

namespace RP2040.Peripherals.Tests.Pwm;

/// <summary>
/// The observed PWM tick (<see cref="PwmPeripheral.OutputStep"/>, behind exact PWM pad edges): every output change is
/// reported at the cycle of the counter step that made it, in time order across slices, whatever the tick lengths,
/// and the end state is that of the plain tick.
/// </summary>
public class PwmObservedTickTests
{
    private static uint Reg(int s, uint off) => (uint)(s * 0x14) + off;

    private static void Setup(PwmPeripheral p, int s, uint top, uint div, bool phase, uint cc)
    {
        p.WriteWord(Reg(s, 0x04), div);
        p.WriteWord(Reg(s, 0x10), top);
        p.WriteWord(Reg(s, 0x0C), cc);
        p.WriteWord(Reg(s, 0x00), 1u | (phase ? 2u : 0u));
    }

    public static IEnumerable<object[]> Configs()
    {
        uint[] tops = [1, 7, 40, 999];
        uint[] divs = [0x10, 0x30, 0x13];
        foreach (var top in tops)
            foreach (var div in divs)
                foreach (var phase in new[] { false, true })
                    yield return [top, div, phase];
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Every_output_edge_carries_the_cycle_of_its_step(uint top, uint div, bool phase)
    {
        var rnd = new Random((int)(top * 31 + div * 7 + (phase ? 1 : 0)));
        var cpu = new CortexM0Plus(new BusInterconnect());
        var plain = new PwmPeripheral(cpu);
        var observed = new PwmPeripheral(cpu);
        var stepped = new PwmPeripheral(cpu);   // one cycle per tick: the reference for when each edge happens
        uint cc0 = (Math.Min(5u, top) << 16) | Math.Min(3u, top), cc1 = ((top / 2) << 16) | (top / 3 + 1);
        foreach (var p in new[] { plain, observed, stepped })
        {
            Setup(p, 0, top, div, phase, cc0);
            Setup(p, 3, top / 2 + 1, 0x15, !phase, cc1);   // a second slice with another period, for the ordering
        }
        long now = 0, tickStart = 0;
        var seen = new List<(long, int, bool, bool)>();
        observed.OutputStep = (s, offset, window) =>
        {
            Assert.InRange(offset, 1, window);
            seen.Add((tickStart + offset, s, observed.GetChannelOutput(s, false), observed.GetChannelOutput(s, true)));
        };
        var expected = new List<(long, int, bool, bool)>();
        int[] slices = [0, 3];
        for (int i = 0; i < 300; i++)
        {
            long delta = rnd.Next(1, 120);
            tickStart = now;
            plain.Tick(delta);
            observed.Tick(delta);
            for (long c = 1; c <= delta; c++)
            {
                var before = slices.Select(s => (stepped.GetChannelOutput(s, false), stepped.GetChannelOutput(s, true))).ToArray();
                stepped.Tick(1);
                for (int k = 0; k < slices.Length; k++)
                {
                    bool a = stepped.GetChannelOutput(slices[k], false), b = stepped.GetChannelOutput(slices[k], true);
                    if ((a, b) != before[k]) expected.Add((now + c, slices[k], a, b));
                }
            }
            now += delta;
            for (int s = 0; s < 8; s++)
            {
                Assert.Equal(plain.ReadWord(Reg(s, 0x08)), observed.ReadWord(Reg(s, 0x08)));
                Assert.Equal(plain.IsCountingUp(s), observed.IsCountingUp(s));
            }
            Assert.Equal(plain.ReadWord(0xA4), observed.ReadWord(0xA4));
        }
        Assert.NotEmpty(expected);
        Assert.Equal(expected, seen);
    }
}

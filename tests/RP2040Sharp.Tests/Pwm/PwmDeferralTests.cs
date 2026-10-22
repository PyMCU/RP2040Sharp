using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Pwm;

namespace RP2040.Peripherals.Tests.Pwm;

/// <summary>
/// The PWM counter's closed-form advance and its <see cref="PwmPeripheral.CyclesUntilEvent"/>: against a
/// one-step-at-a-time model of the counter (the loop the closed form replaced), and with ticks merged up to the
/// event (what deferred ticks do), the counter, direction, raw interrupt and outputs are the same.
/// </summary>
public class PwmDeferralTests
{
    private static uint Reg(int s, uint off) => (uint)(s * 0x14) + off;

    /// <summary>One slice stepped one counter step at a time, as the PWM did before the closed form.</summary>
    private sealed class Reference(uint top, bool phase, uint ctr)
    {
        public uint Ctr = ctr;
        public bool Up = phase && ctr == 0;
        public bool Wrapped;

        public void Step()
        {
            if (phase)
            {
                if (Up) { Ctr++; if (Ctr >= top) { Ctr = top; Up = false; } }
                else if (Ctr == 0) { Up = true; Wrapped = true; }
                else Ctr--;
            }
            else { Ctr++; if (Ctr > top) { Ctr = 0; Wrapped = true; } }
        }
    }

    public static IEnumerable<object[]> Configs()
    {
        uint[] tops = [0, 1, 7, 40, 1499];
        uint[] divs = [0x10, 0x30, 0x13];   // 1.0, 3.0, 1 + 3/16
        foreach (var top in tops)
            foreach (var div in divs)
                foreach (var phase in new[] { false, true })
                    yield return [top, div, phase];
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Closed_form_matches_stepping_and_merged_ticks_match_single_ticks(uint top, uint div, bool phase)
    {
        var rnd = new Random((int)(top * 31 + div * 7 + (phase ? 1 : 0)));
        var cpu = new CortexM0Plus(new BusInterconnect());
        var eager = new PwmPeripheral(cpu);
        var merged = new PwmPeripheral(cpu);
        const uint cc = (5u << 16) | 3u;
        foreach (var p in new[] { eager, merged })
        {
            p.WriteWord(Reg(0, 0x04), div);
            p.WriteWord(Reg(0, 0x10), top);
            p.WriteWord(Reg(0, 0x0C), cc);
            p.WriteWord(Reg(0, 0x00), 1u | (phase ? 2u : 0u));
        }
        var reference = new Reference(top, phase, 0);
        long frac = 0, divisor = (div >> 4) * 16 + (div & 0xF), owed = 0;
        for (int i = 0; i < 400; i++)
        {
            long delta = rnd.Next(1, 40);
            // Reference: the steps this tick takes, one at a time.
            frac += delta * 16;
            for (var n = frac / divisor; n > 0; n--) reference.Step();
            frac %= divisor;
            eager.Tick(delta);
            Assert.Equal(reference.Ctr, eager.ReadWord(Reg(0, 0x08)));
            Assert.Equal(reference.Wrapped, (eager.ReadWord(0xA4) & 1) != 0);
            // Merged: ticks put off until the event, as DeferredTick does.
            var until = merged.CyclesUntilEvent();
            owed += delta;
            if (owed >= until) { merged.Tick(owed); owed = 0; }
            else
            {
                // Nothing observable may change before the event: the merged block still shows the last state.
                Assert.Equal(eager.GetChannelOutput(0, false), merged.GetChannelOutput(0, false));
                Assert.Equal(eager.GetChannelOutput(0, true), merged.GetChannelOutput(0, true));
                Assert.Equal(eager.ReadWord(0xA4), merged.ReadWord(0xA4));
                continue;
            }
            Assert.Equal(eager.ReadWord(Reg(0, 0x08)), merged.ReadWord(Reg(0, 0x08)));
            Assert.Equal(eager.IsCountingUp(0), merged.IsCountingUp(0));
            Assert.Equal(eager.ReadWord(0xA4), merged.ReadWord(0xA4));
        }
    }
}

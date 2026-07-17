using FluentAssertions;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Regression tests for <c>machine.bitstream</c> (the transport under the <c>neopixel</c> module).
/// The rp2 port encodes each bit as a high-pulse width and busy-waits on SysTick's CVR between the
/// pin_high and pin_low writes, so a SysTick that only advances on the peripheral tick boundary
/// collapses both delays to zero and every bit leaves the chip with the same width.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonBitstreamTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.21.0";
    private const int DataPin = 2;

    /// <summary>
    /// Drives a 3-pixel WS2812 frame whose GRB bytes are a known mix of 0x00 and 0xFF, so the
    /// wire carries 24 one-bits and 48 zero-bits, and asserts that the captured high-pulse widths
    /// fall into two separated clusters with a ~2x ratio (WS2812 T0H 0.4us vs T1H 0.8us).
    /// </summary>
    [Fact]
    public async Task Bitstream_ZeroAndOneBits_ProduceDistinguishableHighPulses()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue("MicroPython must reach the REPL");

        runner.ExecuteAndWait("import machine, neopixel", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait($"np = neopixel.NeoPixel(machine.Pin({DataPin}), 3)", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("np[0] = (255, 0, 0); np[1] = (0, 255, 0); np[2] = (0, 0, 255)", ">>> ")
              .Should().BeTrue();

        var widths = CaptureHighPulseWidths(runner);

        widths.Count.Should().BeGreaterThanOrEqualTo(72,
            "a 3-pixel frame carries 72 bits, each one a high pulse");

        var shortest = widths.Min();
        var longest  = widths.Max();

        longest.Should().BeGreaterThan(shortest,
            "a zero-bit and a one-bit must not leave the pin with the same high time");

        var ratio = (double)longest / shortest;
        ratio.Should().BeInRange(1.5, 3.0,
            "WS2812 encodes the bit as T1H/T0H ~ 2x; got {0} vs {1}", longest, shortest);

        // Every pulse must belong to one of the two clusters — a spread continuum would mean the
        // widths are noise rather than an encoding.
        var midpoint = (shortest + longest) / 2.0;
        var zeros = widths.Where(w => w < midpoint).ToList();
        var ones  = widths.Where(w => w >= midpoint).ToList();

        (zeros.Max() - zeros.Min()).Should().BeLessThan((long)((longest - shortest) / 4.0),
            "the zero-bit pulses must form a tight cluster");
        (ones.Max() - ones.Min()).Should().BeLessThan((long)((longest - shortest) / 4.0),
            "the one-bit pulses must form a tight cluster");

        // The point of the encoding is that a downstream LED can recover the payload: threshold the
        // widths at the midpoint and the frame must decode back to the GRB bytes that were written.
        var decoded = new byte[9];
        for (var bit = 0; bit < 72; bit++)
            if (widths[bit] >= midpoint)
                decoded[bit / 8] |= (byte)(0x80 >> (bit % 8));

        decoded.Should().Equal(new byte[]
        {
            0x00, 0xFF, 0x00,   // pixel 0 = (255, 0, 0) sent G, R, B
            0xFF, 0x00, 0x00,   // pixel 1 = (0, 255, 0)
            0x00, 0x00, 0xFF,   // pixel 2 = (0, 0, 255)
        }, "the pulse widths must carry the frame the firmware wrote");
    }

    /// <summary>
    /// Runs <c>np.write()</c> while recording, for every SIO write that flips the data pin, the
    /// core-0 cycle count, and returns the width of each high pulse.
    /// </summary>
    private static List<long> CaptureHighPulseWidths(MicroPythonRunner runner)
    {
        var machine = runner.Simulation.Rp2040;
        var sio = machine.Sio;
        var cpu = machine.Cpu;

        var widths = new List<long>();
        var level = sio.GetGpioOut(DataPin);
        long? roseAt = null;

        sio.OnGpioChanged = () =>
        {
            var now = sio.GetGpioOut(DataPin);
            if (now == level) return;
            level = now;
            if (now)
                roseAt = cpu.Cycles;
            else if (roseAt is { } start)
                widths.Add(cpu.Cycles - start);
        };

        runner.Execute("np.write()");
        runner.Simulation.RunMilliseconds(200);
        sio.OnGpioChanged = null;

        return widths;
    }
}

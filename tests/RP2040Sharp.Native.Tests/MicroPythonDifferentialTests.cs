using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The real MicroPython v1.28.0 (RPI_PICO, SHA-256 00aedb84...8152) booted and driven over USB CDC in every
/// <see cref="SliceMode"/>: boot (plain runs), then, with every pad watched at the given guard, the idle REPL taking
/// host lines, a 1 kHz PWM on GP14 whose duty and frequency the host changes, and a GP15 toggle script. Everything a
/// host sees (call returns, pad events with cycles, CDC chunks with the cycle they arrived at) and the machine state
/// at the end must be identical to the plain slices.
/// </summary>
public class MicroPythonDifferentialTests(ITestOutputHelper output)
{
    private const ulong Ms = 125_000;

    /// <summary>Host lines and the simulated time (ms after the REPL is up) at which the host types them.</summary>
    private static readonly (ulong AtMs, string Line)[] Script =
    [
        (5, "1+1\r"),
        (25, "print('x'*60)\r"),
        (45, "import time; time.sleep_ms(3); print('slept')\r"),
        (60, "[i*i for i in range(20)]\r"),
        (80, "from machine import Pin, PWM\r"),
        (85, "p=PWM(Pin(14), freq=1000, duty_u16=20000)\r"),
        (110, "p.duty_u16(40000)\r"),
        (125, "p.freq(20000)\r"),
        (135, "p.duty_u16(0)\r"),
        (145, "exec(\"q=Pin(15,Pin.OUT,value=0)\\nfor i in range(6):\\n q.toggle()\\n time.sleep_us(200)\\nprint('done', q.value())\")\r"),
    ];

    private const ulong EndMs = 165;

    internal const string WlanLine =
        "exec(\"import network\\nw=network.WLAN(network.STA_IF)\\nw.active(True)\\nprint('wlan', w.active(), len(w.config('mac')))\")\r";

    [SkippableTheory]
    [InlineData(1u)]
    [InlineData(75u)]
    [InlineData(300u)]
    public void Repl_pwm_and_gpio_are_the_same_in_every_mode(uint guard)
    {
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var image = File.ReadAllBytes(uf2!);
        string cdc = "";
        int pads = 0;
        var probe = Differential.Check(run =>
        {
            Assert.True(Abi.LoadUf2(run.H, image) > 1000);
            var bootEnd = run.Cycles + 20 * 1000 * Ms;
            while (run.Cycles < bootEnd && !run.Host.Cdc.Contains(">>> ", StringComparison.Ordinal))
                run.Run(1_000_000);
            Assert.Contains(">>> ", run.Host.Cdc);
            var t0 = run.Cycles;
            int next = 0;
            while (run.Cycles < t0 + EndMs * Ms)
            {
                if (next < Script.Length && run.Cycles >= t0 + Script[next].AtMs * Ms)
                    Abi.SerialWrite(run.H, 2, Script[next++].Line);
                run.RunUntilEdge(1_000_000, guard);
            }
            cdc = run.Host.Cdc;
            pads = run.Host.Pins.Count;
        });
        output.WriteLine($"guard {guard}: stretched {probe.Stretched}, bounded {probe.Bounded}, plain {probe.Plain}, {pads} pad events");
        output.WriteLine(cdc[Math.Max(0, cdc.Length - 600)..].Replace("\r", "").Replace("\n", " | "));
        Assert.Contains("slept", cdc);
        Assert.Contains("done 0", cdc);
        Assert.Contains("[0, 1, 4, 9, 16", cdc);
    }

    /// <summary>The Pico W image with the CYW43439 (ST_RP2040_CYW43): boot (plain runs) and WLAN.active(True) at guard
    /// 75, the deferred and fast paths against the eager one.</summary>
    [SkippableFact]
    public void Pico_W_boot_and_wlan_are_the_same_in_every_mode()
    {
        var uf2 = Firmware.MicroPythonPicoW();
        Skip.If(uf2 is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        var image = File.ReadAllBytes(uf2!);
        string cdc = "";
        int calls = 0;
        var probe = Differential.Check(run =>
        {
            Assert.True(Abi.LoadUf2(run.H, image) > 1000);
            var end = run.Cycles + 20 * 1000 * Ms;
            while (run.Cycles < end && !run.Host.Cdc.Contains(">>> ", StringComparison.Ordinal))
                run.Run(1_000_000);
            Assert.Contains(">>> ", run.Host.Cdc);
            int from = run.Host.Cdc.Length;
            Abi.SerialWrite(run.H, 2, WlanLine);
            end = run.Cycles + 30 * 1000 * Ms;
            while (run.Cycles < end && run.Host.Cdc.IndexOf("wlan True 6", from, StringComparison.Ordinal) < 0)
                run.RunUntilEdge(1_000_000, 75);
            cdc = run.Host.Cdc;
            calls = run.Calls.Count;
        }, flags: Abi.Cyw43);
        output.WriteLine($"stretched {probe.Stretched}, bounded {probe.Bounded}, plain {probe.Plain}, {calls} calls");
        Assert.Contains("wlan True 6", cdc);
    }
}

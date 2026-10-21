using System.Diagnostics;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The real MicroPython v1.28.0 images (RPI_PICO and RPI_PICO_W) through the C ABI only: the REPL over the USB CDC
/// channel, a script exec'd there, the pin callback, and the CYW43439 of the Pico W.
/// </summary>
public class MicroPythonTests(ITestOutputHelper output)
{
    private const uint AllPads = (1u << 30) - 1;

    /// <summary>Run (run_until_edge with every pad watched) until <paramref name="text"/> appears on the CDC after
    /// <paramref name="from"/>, or <paramref name="maxCycles"/> pass.</summary>
    internal static bool RunUntil(nint h, Host host, string text, ulong maxCycles, uint guard = 75, int from = 0)
    {
        var end = Abi.Cycles(h) + maxCycles;
        while (Abi.Cycles(h) < end)
        {
            Abi.RunUntilEdge(h, 1_000_000, guard, AllPads);
            Assert.Equal(0, Abi.Status(h));
            if (host.Cdc.IndexOf(text, from, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    internal static (nint H, Host Host) BootToRepl(string uf2Path, uint flags, ulong maxCycles = 125_000_000UL * 20, byte[]? uniqueId = null)
    {
        var h = Abi.CreateEx(0, flags);
        Assert.NotEqual(0, h);
        if (uniqueId is not null) Abi.SetFlashUniqueId(h, uniqueId);   // before the firmware's start-up reads it
        var host = new Host();
        Abi.Attach(h, host);
        Assert.True(Abi.LoadUf2(h, File.ReadAllBytes(uf2Path)) > 1000);
        // Boot without watching (plain runs), then the REPL prompt the host's CR LF draws after enumeration.
        var end = Abi.Cycles(h) + maxCycles;
        while (Abi.Cycles(h) < end && !host.Cdc.Contains(">>> ", StringComparison.Ordinal))
            Abi.Run(h, 1_000_000);
        return (h, host);
    }

    [SkippableFact]
    public void Pico_boots_to_the_repl_over_cdc_and_runs_an_execd_script()
    {
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var sw = Stopwatch.StartNew();
        var (h, host) = BootToRepl(uf2!, 0);
        try
        {
            output.WriteLine($"REPL after {Abi.Cycles(h) / 125_000.0:F0} ms simulated, {sw.ElapsedMilliseconds} ms wall");
            Assert.Contains(">>> ", host.Cdc);
            Assert.Contains("MicroPython v1.28.0", host.Cdc);

            int from = host.Cdc.Length;
            host.Pins.Clear();
            Abi.SerialWrite(h, 2,
                "exec(\"from machine import Pin\\nimport time\\np=Pin(15,Pin.OUT,value=0)\\n" +
                "for i in range(6):\\n p.toggle()\\n time.sleep_us(200)\\nprint('done', p.value(), 6*7)\")\r");
            Assert.True(RunUntil(h, host, "done 0 42", 125_000_000UL * 5, from: from), host.Cdc[from..]);
            output.WriteLine(host.Cdc[from..].Replace("\r", "").Replace("\n", " | "));

            var gp15 = host.Pins.Where(p => p.Gpio == 15 && p.Oe == 1).ToList();
            Assert.Equal(7, gp15.Count); // driven low by Pin(), then six toggles
            Assert.Equal([0, 1, 0, 1, 0, 1, 0], gp15.Select(p => p.Level));
            for (int i = 2; i < gp15.Count; i++)
                Assert.InRange((long)(gp15[i].Cycle - gp15[i - 1].Cycle), 200 * 125, 400 * 125); // sleep_us(200) apart
            Assert.Equal(0, Abi.Status(h));
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    [SkippableFact]
    public void Pico_W_boots_with_the_cyw43_and_creates_a_wlan()
    {
        var uf2 = Firmware.MicroPythonPicoW();
        Skip.If(uf2 is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        var sw = Stopwatch.StartNew();
        var (h, host) = BootToRepl(uf2!, Abi.Cyw43);
        try
        {
            output.WriteLine($"REPL after {Abi.Cycles(h) / 125_000.0:F0} ms simulated, {sw.ElapsedMilliseconds} ms wall");
            Assert.Contains(">>> ", host.Cdc);
            Assert.Contains("Pico W", host.Cdc);

            int from = host.Cdc.Length;
            Abi.SerialWrite(h, 2,
                "exec(\"import network\\nw=network.WLAN(network.STA_IF)\\nw.active(True)\\nprint('wlan', w.active(), len(w.config('mac')))\")\r");
            Assert.True(RunUntil(h, host, "wlan True 6", 125_000_000UL * 30, from: from), host.Cdc[from..]);
            output.WriteLine(host.Cdc[from..].Replace("\r", "").Replace("\n", " | ") + $"  ({sw.ElapsedMilliseconds} ms wall)");
            Assert.Equal(0, Abi.Status(h));
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }
}

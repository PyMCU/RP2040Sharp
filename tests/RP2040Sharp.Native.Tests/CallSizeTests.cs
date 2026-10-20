using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// st_rp2040_run's slices are counted across calls (RP2040CoSim.Run): a host's call size moves no slice boundary, it
/// only adds a sweep where a call ends inside a slice. MicroPython (RPI_PICO v1.28.0) booted to the REPL over the USB
/// CDC with the same instructions in calls of 1.5M and of 10M instructions lands on the same cycle with the same text
/// and pad events. (Slices counted from each call's start, as before, put every boundary somewhere else at each call
/// size.) Mirrors RP2350Sharp's Cyw43CallSizeTests.
/// </summary>
public class CallSizeTests(ITestOutputHelper output)
{
    private const long Total = 150_000_000;

    private static (ulong Cycles, string Cdc, List<(int, int, int, ulong)> Pins) Boot(string uf2, uint size)
    {
        var h = Abi.CreateEx(0, 0);
        var host = new Host();
        try
        {
            Abi.Attach(h, host);
            Assert.True(Abi.LoadUf2(h, File.ReadAllBytes(uf2)) > 1000);
            for (long done = 0; done < Total; done += size) Abi.Run(h, (uint)Math.Min(size, Total - done));
            Assert.Equal(0, Abi.Status(h));
            return (Abi.Cycles(h), host.Cdc, [.. host.Pins]);
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    [SkippableFact]
    public void MicroPython_boot_with_run_is_the_same_at_1_5M_and_10M_instructions_per_call()
    {
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var a = Boot(uf2!, 1_500_000);
        var b = Boot(uf2!, 10_000_000);
        output.WriteLine($"1.5M: {a.Cycles} cycles, {a.Pins.Count} pad events; 10M: {b.Cycles} cycles, {b.Pins.Count} pad events");
        Assert.Contains("MicroPython v1.28.0", a.Cdc);
        Assert.Equal(a.Cycles, b.Cycles);
        Assert.Equal(a.Cdc, b.Cdc);
        Assert.Equal(a.Pins, b.Pins);
    }
}

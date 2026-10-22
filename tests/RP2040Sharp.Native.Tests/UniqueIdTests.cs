using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// machine.unique_id() on the real MicroPython v1.28.0 images (Pico and Pico W) through the C ABI: the SDK reads the
/// 64-bit id from the flash (0x4B) once at start-up, so it is non-zero, differs between two chips, and equals what
/// st_rp2040_set_flash_unique_id gave the chip before its first run.
/// </summary>
public class UniqueIdTests(ITestOutputHelper output)
{
    private static string ReadId(string uf2, uint flags, byte[]? set)
    {
        var (h, host) = MicroPythonTests.BootToRepl(uf2, flags, uniqueId: set);
        try
        {
            Assert.Contains(">>> ", host.Cdc);
            int from = host.Cdc.Length;
            Abi.SerialWrite(h, 2, "exec(\"import machine, binascii\\nprint('UID', binascii.hexlify(machine.unique_id()).decode(), 'END')\")\r");
            Assert.True(MicroPythonTests.RunUntil(h, host, " END", 125_000_000UL * 10, from: from), host.Cdc[from..]);
            var text = host.Cdc[from..].Replace("\r", "");
            var line = text.Split('\n').First(l => l.StartsWith("UID ", StringComparison.Ordinal));
            return line.Split(' ')[1];
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }

    [SkippableFact]
    public void Pico_unique_id_is_not_zero_differs_between_chips_and_follows_the_setter()
    {
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        var a = ReadId(uf2!, 0, null);
        var b = ReadId(uf2!, 0, null);
        output.WriteLine($"{a} {b}");
        Assert.Equal(16, a.Length);
        Assert.NotEqual("0000000000000000", a);
        Assert.NotEqual("0000000000000000", b);
        Assert.NotEqual(a, b);
        Assert.Equal("e6614104039b2a17", ReadId(uf2!, 0, [0xE6, 0x61, 0x41, 0x04, 0x03, 0x9B, 0x2A, 0x17]));
    }

    [SkippableFact]
    public void Pico_W_unique_id_is_not_zero_and_follows_the_setter()
    {
        var uf2 = Firmware.MicroPythonPicoW();
        Skip.If(uf2 is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        var a = ReadId(uf2!, Abi.Cyw43, null);
        output.WriteLine(a);
        Assert.NotEqual("0000000000000000", a);
        Assert.Equal("0102030405060708", ReadId(uf2!, Abi.Cyw43, [1, 2, 3, 4, 5, 6, 7, 8]));
    }
}

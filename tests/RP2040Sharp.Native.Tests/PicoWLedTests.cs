using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SiliconTwin.Native;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The Pico W's onboard LED (CYW43439 WL_GPIO0) through the C ABI: st_rp2040_wifi_gpio, with MicroPython v1.28.0
/// RPI_PICO_W driven as the Proteus model drives it (run_until_edge at guard 75, the symbol's pads watched). The host
/// types <c>Pin("LED", Pin.OUT).on()</c> and <c>.off()</c> over the USB CDC; the export follows, and the guest reads
/// the level back through the "ccgpioin" iovar. Every export is called through an unmanaged function pointer.
/// </summary>
public unsafe class PicoWLedTests(ITestOutputHelper output)
{
    private const uint Cyw43 = 0x100, BusAll = 7;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSerial(nint user, int channel, byte* data, nuint len)
    {
        var cdc = (StringBuilder)GCHandle.FromIntPtr(user).Target!;
        if (channel == 2) cdc.Append(Encoding.Latin1.GetString(data, (int)len));
    }

    private static readonly delegate* unmanaged[Cdecl]<uint, uint, nint> CreateEx = &Exports.CreateEx;
    private static readonly delegate* unmanaged[Cdecl]<nint, void> Destroy = &Exports.Destroy;
    private static readonly delegate* unmanaged[Cdecl]<nint, int> Status = &Exports.Status;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*> LastError = &Exports.LastError;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> LoadUf2 = &Exports.LoadUf2;
    private static readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, ulong> RunUntilEdge = &Exports.RunUntilEdge;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> SerialWrite = &Exports.SerialWrite;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>, nint, void> SetSerialCb = &Exports.SetSerialCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, uint> WifiGpio = &Exports.WifiGpio;

    private static string Error(nint h) => Marshal.PtrToStringAnsi((nint)LastError(h))!;

    [SkippableFact]
    public void Pin_led_on_and_off_show_in_wifi_gpio()
    {
        var path = Firmware.MicroPythonPicoW();
        Skip.If(path is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        var uf2 = File.ReadAllBytes(path!);
        var cdc = new StringBuilder();
        var gc = GCHandle.Alloc(cdc);
        var h = CreateEx(0, BusAll | Cyw43);
        Assert.NotEqual(0, h);
        try
        {
            SetSerialCb(h, &OnSerial, GCHandle.ToIntPtr(gc));
            fixed (byte* p = uf2) Assert.True(LoadUf2(h, p, (nuint)uf2.Length) > 1000);
            Assert.Equal(0u, WifiGpio(h));

            // Run until `marker` and the prompt after it are on the CDC, recording every value the export took.
            var seen = new List<uint>();
            void RunTo(string marker)
            {
                for (ulong cycles = 0; ; )
                {
                    cycles = RunUntilEdge(h, 30_000, 75, PicoWIdleTests.ProteusWatch);
                    Assert.True(Status(h) == 0, Error(h));
                    Assert.True(cycles < 3_000_000_000UL, "no '" + marker + "': " + cdc);
                    var g = WifiGpio(h);
                    if (seen.Count == 0 || seen[^1] != g) seen.Add(g);
                    var t = cdc.ToString();
                    var at = t.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0 && t.IndexOf(">>> ", at, StringComparison.Ordinal) >= 0) return;
                }
            }
            void Type(string line)
            {
                var b = Encoding.ASCII.GetBytes(line + "\r");
                fixed (byte* p = b) SerialWrite(h, 2, p, (nuint)b.Length);
            }

            RunTo("");
            Type("from machine import Pin; led=Pin('LED', Pin.OUT); led.on(); print('LED', led.value())");
            RunTo("LED ");
            Assert.Contains("LED 1", cdc.ToString());
            Assert.Equal(1u, WifiGpio(h));
            Type("led.off(); print('OFF', led.value())");
            RunTo("OFF ");
            Assert.Contains("OFF 0", cdc.ToString());
            Assert.Equal(0u, WifiGpio(h));
            output.WriteLine(cdc.ToString().Replace("\r", "").Replace("\n", " | "));
            Assert.Equal([0u, 1u, 0u], seen);
        }
        finally
        {
            Destroy(h);
            gc.Free();
        }
    }

    // No firmware needed.
    [Fact]
    public void A_plain_pico_and_a_dead_handle_read_zero()
    {
        var plain = CreateEx(0, BusAll);
        var w = CreateEx(0, BusAll | Cyw43);
        try
        {
            Assert.Equal(0u, WifiGpio(plain));
            Assert.Equal(0, Status(plain));                       // not an error: nothing to reject
            Assert.Equal(0u, WifiGpio(w));                        // powered off
            Assert.Equal(0u, WifiGpio(0));
        }
        finally { Destroy(plain); Destroy(w); }
        Assert.Equal(0u, WifiGpio(w));                            // destroyed
    }
}

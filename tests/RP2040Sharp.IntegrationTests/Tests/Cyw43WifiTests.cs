using System.Text;
using FluentAssertions;
using RP2040.Peripherals.Usb;
using RP2040.TestKit;
using RP2040Sharp.Wireless.Cyw43;
using RP2040Sharp.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// WiFi bring-up on the Pico W: real MicroPython on the emulated CYW43439 brings up the WLAN interface
/// (firmware download over the gSPI backplane, then SDPCM/WLC ioctls) and scans, seeing a virtual AP.
///
/// SKIPPED (in-progress WiFi gap; BLE on the same chip already works — see Cyw43BleTests): the first WLAN
/// SDPCM ioctl ('clmload') is received by the emulated chip and a 28-byte response is queued
/// (OnPacketReadyChanged), but the firmware's cyw43_ll do_ioctl poll never issues the F2 read — it gates
/// on the WL_HOST_WAKE pin (GPIO24) reading high at the moment it polls, and the gSPI F2 host-wake/poll
/// timing isn't satisfied (the BT shared bus works because it reads SDIO_INT_STATUS over F1, never GPIO24).
/// Result: "[CYW43] do_ioctl/STALL timeout, CLM load failed". Next: fix the GPIO24 host-wake level held
/// between gSPI transactions for the F2 poll path (GSpiSlave). Unskip once WLAN.active(True) + scan work.
/// </summary>
public class Cyw43WifiTests(ITestOutputHelper output)
{
    private const string PicoW = "/Users/begeistert/Repos/micropython/ports/rp2/build-RPI_PICO_W/firmware.uf2";

    [Fact]
    public void Wlan_brings_up_and_scans()
    {
        if (!File.Exists(PicoW)) { output.WriteLine("skip"); return; }
        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(PicoW)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += () => sim.Rp2040.IoBank0.NotifyPads(0xFFFFFFFFu);

        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp("RP2040Sharp-AP", [0x02, 0, 0x5E, 0, 4, 1], 6, -50, false));
        var ioctls = new List<string>();
        dev.Sdpcm.OnIoctl += (cmd, kind, name, len) => { if (ioctls.Count < 200) ioctls.Add($"ioctl cmd={cmd} kind={kind} '{name}' in={len}"); };
        dev.Sdpcm.OnPacketReadyChanged += (ready, len) => { if (ioctls.Count < 200) ioctls.Add($"  pktReady={ready} len={len}"); };
        dev.OnCommand += (w, fn, addr, sz) => { if (fn == 2 && ioctls.Count < 200) ioctls.Add($"  F2 {(w ? "WR" : "RD")} sz{sz}"); };

        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));

        void Step(long max, Func<bool> done)
        { for (long i = 0; i < max && !done(); i++) sim.Rp2040.Run(sim.Rp2040.Core0Waiting ? 1600 : 512); }

        Step(120_000_000, () => rx.ToString().Contains(">>>"));

        cdc.SendSerialBytes("\x01"u8);
        cdc.SendSerialBytes(Encoding.ASCII.GetBytes(
            "import network\nw=network.WLAN(network.STA_IF)\nw.active(True)\nprint('WLAN', w.active())\n" +
            "print('SCAN', [s[0] for s in w.scan()])\n"));
        cdc.SendSerialBytes("\x04"u8);

        int at = rx.Length;
        Step(3_000_000_000, () => rx.ToString(at, rx.Length - at).Contains("SCAN ") || rx.ToString(at, rx.Length - at).Contains("Error"));

        File.WriteAllText("/tmp/rp2040_wifi.txt", "REPL:\n" + rx + "\n\nFirmwareBytes=" + dev.FirmwareBytes +
            " WlanCoreUp=" + dev.DebugWlanCoreUp + "\nIOCTLS:\n  " + string.Join("\n  ", ioctls));
        output.WriteLine(rx.ToString()[at..]);

        rx.ToString().Should().Contain("WLAN True", "WLAN.active(True) must bring the interface up");
        rx.ToString().Should().Contain("RP2040Sharp-AP", "scan must surface the virtual AP over SDPCM");
        sim.Cpu.IsLockedUp.Should().BeFalse();
    }
}

using System.Text;
using FluentAssertions;
using RP2040.Core.Cpu;
using RP2040.Peripherals.Usb;
using RP2040.TestKit;
using RP2040Sharp.Wireless.Cyw43;
using RP2040Sharp.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.IntegrationTests.Tests;

file sealed class PanicObserver(RP2040TestSimulation sim, System.Collections.Generic.Queue<string> tl) : IProfilingObserver
{
    public bool Hit;
    public uint MsgPtr, Arg1;
    private uint _epIn;
    private int _lastActive = -1;
    private uint _lastPc;
    public void OnInstruction(uint pc, ushort opcode, long cycles)
    {
        if (Hit) return;
        if (pc == 0x20000C30)   // hw_endpoint_xfer_continue(ep): R0 = ep*
        {
            var ep = sim.Cpu.Registers.R0;
            byte B(uint o) => sim.Rp2040.Bus.ReadByte(ep + o);
            ushort H(uint o) => (ushort)(B(o) | B(o + 1) << 8);
            if (B(2) == 0x80) _epIn = ep;   // latch EP0-IN struct address
            tl.Enqueue($"  xfer_continue ep=0x{B(2):X2} active={B(26)} rem={H(20)} xfd={H(22)}");
            if (tl.Count > 80) tl.Dequeue();
        }
        if (_epIn != 0)   // watch EP0-IN ep->active (offset 26): log the PC that drives it 1 -> 0
        {
            int a = sim.Rp2040.Bus.ReadByte(_epIn + 26);
            if (_lastActive == 1 && a == 0)
            {
                tl.Enqueue($"  >>> active 1->0 by PC=0x{_lastPc:X8}");
                if (tl.Count > 80) tl.Dequeue();
            }
            _lastActive = a;
        }
        _lastPc = pc;
        if (pc == 0x10055650)   // pico-sdk panic() entry
        {
            MsgPtr = sim.Cpu.Registers.R0; Arg1 = sim.Cpu.Registers.R1; Hit = true;
        }
    }
}

/// <summary>
/// BLE bring-up on the Pico W: real MicroPython (btstack) on the emulated CYW43439 brings up the
/// Bluetooth LE controller. The BT firmware downloads over the shared gSPI backplane and the HCI bring-up
/// sequence (Reset, Read BD_ADDR, buffer sizes, …) completes against the emulated HciController, so
/// <c>bluetooth.BLE().active(True)</c> returns True and real HCI flows. Mirrors the validated RP2350.Wireless.
/// </summary>
public class Cyw43BleTests(ITestOutputHelper output)
{
    private static readonly string? PicoW = FirmwareCache.GetMicroPythonPicoWAsync("v1.21.0").GetAwaiter().GetResult();

    [Fact(Skip = "Diagnostic-only USB-DCD trace.")]
    public void Diag_usb_timeline()
    {
        if (!File.Exists(PicoW)) { output.WriteLine("skip"); return; }
        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(PicoW!)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += () => sim.Rp2040.IoBank0.NotifyPads(0xFFFFFFFFu);
        _ = new Cyw43439Device(sim.Rp2040.IoBank0);
        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));
        var tl = new System.Collections.Generic.Queue<string>();
        sim.Rp2040.Usb.OnUsbEvent += s => { tl.Enqueue(s); if (tl.Count > 80) tl.Dequeue(); };
        var obs = new PanicObserver(sim, tl);
        for (long i = 0; i < 600_000 && !rx.ToString().Contains(">>>") && !obs.Hit; i++)
            sim.Rp2040.RunProfiled(sim.Rp2040.Core0Waiting ? 1600 : 512, obs);
        var msg = "";
        if (obs.MsgPtr is >= 0x10000000 and < 0x20000000)
            for (uint k = 0; k < 80; k++) { var b = sim.Rp2040.Bus.ReadByte(obs.MsgPtr + k); if (b == 0) break; msg += (char)b; }
        File.WriteAllText("/tmp/rp2040_usbtl.txt",
            $"hitPanic={obs.Hit} msg='{msg}' ep=0x{obs.Arg1:X2} reached>>>={rx.ToString().Contains(">>>")}\n" +
            "--- unified timeline (last 80: SETUP/ARM/BS + xfer_continue) ---\n" + string.Join("\n", tl));
        output.WriteLine(File.ReadAllText("/tmp/rp2040_usbtl.txt"));
    }

    [Fact]
    public void Ble_controller_brings_up()
    {
        if (!File.Exists(PicoW)) { output.WriteLine("skip"); return; }
        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(PicoW!)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += () => sim.Rp2040.IoBank0.NotifyPads(0xFFFFFFFFu);

        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        var hci = new List<string>();
        dev.BtBus.OnHostPacket += (t, p) => { if (hci.Count < 200) hci.Add($"H2B t{t} [{Convert.ToHexString(p)}]"); };
        dev.BtBus.OnChipPacket += (t, p) => { if (hci.Count < 200) hci.Add($"B2H t{t} [{Convert.ToHexString(p)}]"); };
        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));

        void Step(long max, Func<bool> done)
        { for (long i = 0; i < max && !done(); i++) sim.Rp2040.Run(sim.Rp2040.Core0Waiting ? 1600 : 512); }

        Step(120_000_000, () => rx.ToString().Contains(">>>"));

        cdc.SendSerialBytes("\x01"u8);
        cdc.SendSerialBytes(Encoding.ASCII.GetBytes(
            "import bluetooth\nble=bluetooth.BLE()\nble.active(True)\nprint('BLE', ble.active())\n"));
        cdc.SendSerialBytes("\x04"u8);

        int at = rx.Length;
        Step(2_000_000_000, () => rx.ToString(at, rx.Length - at).Contains("BLE ") || rx.ToString(at, rx.Length - at).Contains("Error"));

        output.WriteLine(rx.ToString()[at..]);
        output.WriteLine("HCI:\n  " + string.Join("\n  ", hci));
        rx.ToString().Should().Contain("BLE True", "bluetooth.BLE().active(True) must bring the controller up");
        // The controller really came up — the HCI Reset was answered and the bring-up sequence flowed
        // over the emulated BT shared bus (not just an optimistic active() return).
        hci.Should().Contain(h => h.StartsWith("H2B t1 [030C00]"), "the host must issue HCI Reset");
        hci.Should().Contain(h => h.StartsWith("B2H t4 [0E0401030C00]"), "the controller must answer HCI Reset Command Complete");
        hci.Should().Contain(h => h.Contains("0E0A01091000"), "Read BD_ADDR must return the controller address");
        sim.Cpu.IsLockedUp.Should().BeFalse();
    }
}

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
/// Async RX regression (the "Pico 1 never gets the MQTT topic push" bug, at emulator level). A TCP/HTTP
/// server on real MicroPython over the emulated CYW43439: the guest associates, gets a DHCP lease, listens
/// on :80 and BLOCKS in accept() — asleep in WFI. The virtual gateway then connects and GETs; the guest
/// must wake on the WL_HOST_WAKE (GPIO24) edge to receive the request and reply. Ported from the RP2350
/// Cyw43HttpTests (which passes) to exercise the RP2040 GSpiSlave host-wake path.
/// </summary>
public class Cyw43HttpRxTests(ITestOutputHelper output)
{
    private static readonly string? PicoW = FirmwareCache.GetMicroPythonPicoWAsync("v1.21.0").GetAwaiter().GetResult();

    [Fact]
    public void Guest_http_server_is_reachable_over_the_virtual_network()
    {
        if (!File.Exists(PicoW)) { output.WriteLine("skip"); return; }
        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(PicoW!)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += () => sim.Rp2040.IoBank0.NotifyPads(0xFFFFFFFFu);

        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp("RP2040Sharp-AP", [0x02, 0, 0x5E, 0, 4, 1], 6, -50, false));
        var net = new VirtualNet(dev.Sdpcm);
        string? httpResponse = null;
        net.OnTcpClosed += data => httpResponse = Encoding.Latin1.GetString(data);
        var diag = new List<string>();
        net.OnGuestFrame += (itf, et, len) => { if (diag.Count < 80) diag.Add($"FRAME et=0x{et:X4} len={len}"); };
        net.OnTcpSegment += (fl, seq, ack, plen) => { if (diag.Count < 80) diag.Add($"TCP flags=0x{fl:X2} seq={seq} ack={ack} plen={plen}"); };

        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));

        void Step(long max, Func<bool> done)
        { for (long i = 0; i < max && !done(); i++) sim.Rp2040.Run(sim.Rp2040.Core0Waiting ? 1600 : 512); }

        Step(120_000_000, () => rx.ToString().Contains(">>>"));

        cdc.SendSerialBytes("\x01"u8);
        cdc.SendSerialBytes(Encoding.ASCII.GetBytes(
            "import network,time,socket\n" +
            "w=network.WLAN(network.STA_IF)\nw.active(True)\nw.connect('RP2040Sharp-AP')\n" +
            "while w.status()!=3:\n time.sleep_ms(50)\n" +
            "s=socket.socket()\ns.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)\n" +
            "s.bind(('0.0.0.0',80))\ns.listen(1)\nprint('LISTENING')\n" +
            "cl,a=s.accept()\nreq=cl.recv(512)\n" +
            "cl.send(b'HTTP/1.0 200 OK\\r\\nContent-Type: text/plain\\r\\nConnection: close\\r\\n\\r\\nHello from RP2040Sharp')\n" +
            "cl.close()\nprint('SERVED')\n"));
        cdc.SendSerialBytes("\x04"u8);

        Step(2_000_000_000, () => rx.ToString().Contains("LISTENING"));
        rx.ToString().Should().Contain("LISTENING", "the guest must reach an IP and bind its server socket");

        net.TcpConnect(itf: 0, serverPort: 80,
            Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: 192.168.4.2\r\n\r\n"));

        Step(2_000_000_000, () => httpResponse != null);

        output.WriteLine(rx.ToString());
        output.WriteLine("HTTP RESPONSE: " + httpResponse);
        File.WriteAllText("/tmp/rp2040_http.txt", $"response={httpResponse}\nREPL:\n{rx}\nDIAG:\n  " + string.Join("\n  ", diag));

        httpResponse.Should().NotBeNull("the TCP connection must complete and close with a response");
        httpResponse.Should().Contain("200 OK");
        httpResponse.Should().Contain("Hello from RP2040Sharp", "the guest HTTP body must arrive over the virtual TCP path");
        sim.Cpu.IsLockedUp.Should().BeFalse();
    }
}

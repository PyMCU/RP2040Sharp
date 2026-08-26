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
/// The Pico W as a station on the virtual network, end to end: associate, take a DHCP lease, read the
/// signal level, resolve a name, dial out over TCP, exchange a UDP datagram, and drop the link again.
///
/// Everything here runs the real MicroPython + cyw43-driver stack against the emulated CYW43439, so it
/// covers the parts a WiFi program actually uses beyond "the interface came up": the outbound socket
/// path (an HTTP/MQTT client dials, it does not wait to be dialled), DNS (the gate every name-based
/// client passes), and disconnect() actually taking the link down.
/// </summary>
public class Cyw43StationTests(ITestOutputHelper output)
{
    private static string? PicoW => FirmwareCache.GetMicroPythonPicoWAsync().GetAwaiter().GetResult();

    [Fact]
    public void Station_associates_resolves_dials_out_and_disconnects()
    {
        var picoW = PicoW;
        if (picoW is null) { output.WriteLine("Pico W firmware unavailable (offline) — skipped"); return; }

        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(picoW)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += mask => sim.Rp2040.IoBank0.NotifyPads(mask);

        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp("RP2040Sharp-AP", [0x02, 0, 0x5E, 0, 4, 1], 6, -50, false));

        var net = new VirtualNet(dev.Sdpcm);
        net.DnsRecords["api.test"] = [192, 168, 4, 1];
        string? dnsQuery = null;
        net.OnDnsQuery += (name, answer) => dnsQuery = $"{name}->{(answer is null ? "NXDOMAIN" : string.Join('.', answer))}";
        string? httpRequest = null;
        net.ListenTcp(8080, req =>
        {
            httpRequest = Encoding.Latin1.GetString(req);
            return Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\nPONG!");
        });
        net.ListenUdp(9999, d => Encoding.ASCII.GetBytes("pong:" + Encoding.ASCII.GetString(d)));

        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));
        void Step(long max, Func<bool> done)
        { for (long i = 0; i < max && !done(); i++) sim.Rp2040.Run(sim.Rp2040.Core0Waiting ? 1600 : 512); }

        Step(200_000, () => rx.ToString().Contains(">>>"));
        rx.ToString().Should().Contain(">>>", "MicroPython must reach the REPL");

        cdc.SendSerialBytes("\x01"u8);
        Step(20_000, () => rx.ToString().Contains("raw REPL"));
        var at = rx.Length;
        cdc.SendSerialBytes(Encoding.ASCII.GetBytes(
            "import network, socket, time\n" +
            "w = network.WLAN(network.STA_IF)\n" +
            "w.active(True)\n" +
            "print('SCAN', [s[0] for s in w.scan()])\n" +
            "w.connect('RP2040Sharp-AP')\n" +
            "n = 0\n" +
            "while w.status() != 3 and n < 200:\n time.sleep_ms(50); n += 1\n" +
            "print('IP', w.ifconfig()[0], 'RSSI', w.status('rssi'))\n" +
            "print('DNS', socket.getaddrinfo('api.test', 8080)[0][-1])\n" +
            "c = socket.socket(); c.settimeout(5)\n" +
            "c.connect(socket.getaddrinfo('api.test', 8080)[0][-1])\n" +
            "c.send(b'GET /ping HTTP/1.0\\r\\n\\r\\n')\n" +
            "print('HTTP', c.recv(200).split(b'\\r\\n\\r\\n')[-1])\n" +
            "c.close()\n" +
            "u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.settimeout(5)\n" +
            "u.sendto(b'ping', ('192.168.4.1', 9999))\n" +
            "print('UDP', u.recvfrom(64)[0]); u.close()\n" +
            "w.disconnect(); time.sleep_ms(300)\n" +
            "print('DOWN', w.status(), w.isconnected())\n"));
        cdc.SendSerialBytes("\x04"u8);
        Step(400_000_000, () => rx.ToString(at, rx.Length - at).Contains("DOWN ")
                                || rx.ToString(at, rx.Length - at).Contains("Error"));

        var text = rx.ToString()[at..];
        output.WriteLine(text.Replace("\r", " ").Replace("\n", " | "));

        text.Should().Contain("SCAN [b'RP2040Sharp-AP']", "the scan must surface the virtual AP");
        text.Should().Contain("IP 192.168.4.2", "the station must take a DHCP lease");
        text.Should().Contain("RSSI -50", "WLC_GET_RSSI must report the associated AP's level, not 0");
        text.Should().Contain("DNS ('192.168.4.1', 8080)", "getaddrinfo must resolve through the virtual DNS");
        text.Should().Contain("HTTP b'PONG!'", "an outbound socket must reach a server on the virtual network");
        text.Should().Contain("UDP b'pong:ping'", "UDP datagrams must round-trip");
        text.Should().Contain("DOWN 1 False", "disconnect() must actually bring the link down");

        httpRequest.Should().StartWith("GET /ping", "the server side must receive the guest's request");
        dnsQuery.Should().Be("api.test->192.168.4.1");
        sim.Cpu.IsLockedUp.Should().BeFalse();
    }
}

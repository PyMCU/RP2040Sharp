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
/// Joining a password-protected network — what every real AP is, and what the shape of Raspberry Pi's
/// own <c>wireless/webserver.py</c> assumes. The driver only reaches WIFI_JOIN_STATE_ALL once the
/// supplicant reports WLC_SUP_KEYED, so a chip that models only open networks leaves
/// <c>WLAN.connect(ssid, password)</c> timing out with "network connection failed".
///
/// The happy path here runs the same program that example does: connect with a passphrase, serve HTTP,
/// and switch a GPIO from the request line. The unhappy path checks a wrong passphrase is refused
/// rather than silently accepted.
/// </summary>
public class Cyw43Wpa2Tests(ITestOutputHelper output)
{
    private static string? PicoW => FirmwareCache.GetMicroPythonPicoWAsync().GetAwaiter().GetResult();

    private const string Ssid = "RP2040Sharp-Secure";
    private const string Password = "correct-horse-battery";

    private const string Program =
        "import network, socket, time\n" +
        "from machine import Pin\n" +
        "led = Pin(15, Pin.OUT)\n" +
        "w = network.WLAN(network.STA_IF)\n" +
        "w.active(True)\n" +
        "w.connect('{SSID}', '{PASSWORD}')\n" +
        "n = 0\n" +
        "while w.status() >= 0 and w.status() < 3 and n < 200:\n time.sleep_ms(50); n += 1\n" +
        "print('JOIN', w.status(), w.ifconfig()[0])\n" +
        "if w.status() == 3:\n" +
        "    s = socket.socket()\n" +
        "    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)\n" +
        "    s.bind(('0.0.0.0', 80)); s.listen(1)\n" +
        "    print('SERVING')\n" +
        "    cl, a = s.accept()\n" +
        "    req = cl.recv(512)\n" +
        "    if b'/light/on' in req: led.on()\n" +
        "    cl.send(b'HTTP/1.0 200 OK\\r\\n\\r\\nLED ' + (b'ON' if led.value() else b'OFF'))\n" +
        "    cl.close(); print('SERVED')\n";

    [Theory]
    [InlineData(Password, true)]
    [InlineData("wrong-passphrase", false)]
    public void Station_joins_a_wpa2_network_only_with_the_right_passphrase(string passphrase, bool shouldJoin)
    {
        var picoW = PicoW;
        if (picoW is null) { output.WriteLine("Pico W firmware unavailable (offline) — skipped"); return; }

        using var sim = RP2040TestSimulation.Create().WithBinary(Uf2Reader.ToFlashImage(File.ReadAllBytes(picoW)));
        sim.Rp2040.Pio0.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Pio1.ReadGpioIn = () => sim.Rp2040.IoBank0.GetInputWord();
        sim.Rp2040.Sio.OnGpioChanged += mask => sim.Rp2040.IoBank0.NotifyPads(mask);

        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp(Ssid, [0x02, 0, 0x5E, 0, 4, 2], 6, -55,
            Secured: true, Passphrase: Password));
        var net = new VirtualNet(dev.Sdpcm);
        string? page = null;
        net.OnTcpClosed += d => page = Encoding.Latin1.GetString(d);

        var cdc = new UsbCdcHost(sim.Rp2040.Usb);
        var rx = new StringBuilder();
        cdc.OnSerialData += d => rx.Append(Encoding.Latin1.GetString(d));
        void Step(long max, Func<bool> done)
        { for (long i = 0; i < max && !done(); i++) sim.Rp2040.Run(sim.Rp2040.Core0Waiting ? 1600 : 512); }

        Step(200_000, () => rx.ToString().Contains(">>>"));
        cdc.SendSerialBytes("\x01"u8);
        Step(20_000, () => rx.ToString().Contains("raw REPL"));
        var at = rx.Length;
        cdc.SendSerialBytes(Encoding.ASCII.GetBytes(
            Program.Replace("{SSID}", Ssid).Replace("{PASSWORD}", passphrase)));
        cdc.SendSerialBytes("\x04"u8);
        Step(60_000_000, () => rx.ToString(at, rx.Length - at).Contains("JOIN "));

        var text = rx.ToString()[at..];
        output.WriteLine(text.Replace("\r", " ").Replace("\n", " | "));

        if (!shouldJoin)
        {
            text.Should().NotContain("JOIN 3", "a wrong passphrase must not be accepted");
            return;
        }

        text.Should().Contain("JOIN 3 192.168.4.2", "the right passphrase must complete the WPA2 handshake");
        Step(60_000_000, () => rx.ToString().Contains("SERVING"));
        rx.ToString().Should().Contain("SERVING");

        net.TcpConnect(itf: 0, serverPort: 80,
            Encoding.ASCII.GetBytes("GET /light/on HTTP/1.0\r\nHost: 192.168.4.2\r\n\r\n"));
        Step(60_000_000, () => page != null);

        page.Should().Contain("LED ON", "the guest must serve the page it built from the request");
        sim.Rp2040.Sio.GetGpioOut(15).Should().BeTrue("the request must have switched GP15 on");
        sim.Cpu.IsLockedUp.Should().BeFalse();
    }
}

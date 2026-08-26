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
/// A four-board fleet on one virtual LAN talking MQTT: two publishers, two subscribers, through a
/// gateway port bridged to a real broker. Everything above the radio is the stock stack — MicroPython,
/// lwIP, umqtt.simple — so this exercises the parts a fleet stresses that a single board never does:
/// per-chip MAC and DHCP lease, the L2 switch under concurrent traffic, and long-lived bridged TCP
/// connections carrying segmented payloads in both directions.
///
/// <para>Offline (or with the broker unreachable) it degrades to a local in-process broker so the test
/// still covers the emulator rather than the internet.</para>
/// </summary>
public class Cyw43MqttFleetTests(ITestOutputHelper output) : IDisposable
{
    private const string Ssid = "FleetAP", Password = "fleet-pass";

    private readonly List<PicoWSimulation> _boards = [];
    private MiniMqttBroker? _broker;
    public void Dispose()
    {
        foreach (var b in _boards) b.Dispose();
        _broker?.Dispose();
    }

    [Fact]
    public void Two_publishers_and_two_subscribers_exchange_messages_through_a_broker()
    {
        var picoW = FirmwareCache.GetMicroPythonPicoWAsync().GetAwaiter().GetResult();
        if (picoW is null) { output.WriteLine("Pico W firmware unavailable (offline) — skipped"); return; }
        var image = Uf2Reader.ToFlashImage(File.ReadAllBytes(picoW));
        var umqtt = PinnedAsset.UmqttSimpleAsync().GetAwaiter().GetResult();
        if (umqtt is null) { output.WriteLine("umqtt.simple unavailable (offline) — skipped"); return; }

        _broker = new MiniMqttBroker();
        var net = new VirtualNet();
        // RP2040SHARP_MQTT_BROKER=host:port points the fleet at a real broker (e.g. test.mosquitto.org:1883).
        var external = Environment.GetEnvironmentVariable("RP2040SHARP_MQTT_BROKER");
        if (external is { Length: > 0 } && external.Split(':') is [var h, var p] && int.TryParse(p, out var pn))
        { net.BridgeTcp(1883, h, pn); output.WriteLine($"bridging to {h}:{pn}"); }
        else net.BridgeTcp(1883, "127.0.0.1", _broker.Port);

        var names = new[] { "pub-a", "pub-b", "sub-a", "sub-b" };
        var rx = new StringBuilder[names.Length];
        var cdc = new UsbCdcHost[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var board = PicoWSimulation.Create(image).OfferAp(Ssid, Password);
            _boards.Add(board);
            cdc[i] = new UsbCdcHost(board.Board.Rp2040.Usb);
            rx[i] = new StringBuilder();
            var sb = rx[i];
            cdc[i].OnSerialData += d => sb.Append(Encoding.Latin1.GetString(d));
        }

        // Single-threaded on purpose: this is the shape a host app (iCircuit) runs, so the fleet is
        // stepped round-robin on the calling thread. FleetRunner can put each board on its own thread
        // where that is available, but nothing here depends on it.
        foreach (var b in _boards) net.AddDevice(b.Wifi);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void StepAll(long rounds, Func<bool> done)
        {
            for (long i = 0; i < rounds && !done(); i++)
            {
                foreach (var b in _boards) b.Step();
                if ((i & 0x3F) == 0) net.Poll();
            }
            net.Poll();
        }

        StepAll(400_000, () => rx.All(r => r.ToString().Contains(">>>")));
        rx.Should().OnlyContain(r => r.ToString().Contains(">>>"), "every board must reach its REPL");

        // Install the MQTT client library on each board.
        for (var i = 0; i < names.Length; i++)
        {
            cdc[i].SendSerialBytes("\x01"u8);
            var r = rx[i];
            StepAll(20_000, () => r.ToString().Contains("raw REPL"));
            r.Clear();
            cdc[i].SendSerialBytes(Encoding.ASCII.GetBytes(
                $"_f=open('umqtt_simple.py','w')\n_f.write({PythonLiteral(umqtt)})\n_f.close()\nprint('LIBOK')\n"));
            cdc[i].SendSerialBytes("\x04"u8);
        }
        StepAll(4_000_000, () => rx.All(r => r.ToString().Contains("LIBOK")));
        rx.Should().OnlyContain(r => r.ToString().Contains("LIBOK"), "the MQTT library must land on every board");

        var Topic = $"rp2040sharp/fleet/{Environment.ProcessId}/telemetry";

        // Subscribers first: MQTT only delivers what was published after the subscription.
        for (var i = 2; i < names.Length; i++)
        {
            rx[i].Clear();
            cdc[i].SendSerialBytes(Encoding.ASCII.GetBytes(Join(names[i]) +
                "got=[]\ndef cb(t, m):\n got.append(m)\n" +
                "c.set_callback(cb)\nc.subscribe('" + Topic + "')\nprint('SUBSCRIBED')\n" +
                "n=0\nwhile len(got)<6 and n<3000:\n c.check_msg()\n time.sleep_ms(10)\n n+=1\n" +
                "print('GOT', sorted(got))\n"));
            cdc[i].SendSerialBytes("\x04"u8);
        }
        StepAll(20_000_000, () => rx.Skip(2).All(r => r.ToString().Contains("SUBSCRIBED")));
        rx.Skip(2).Should().OnlyContain(r => r.ToString().Contains("SUBSCRIBED"),
            "both subscribers must reach the broker through the bridged gateway port");

        for (var i = 0; i < 2; i++)
        {
            rx[i].Clear();
            cdc[i].SendSerialBytes(Encoding.ASCII.GetBytes(Join(names[i]) +
                $"for k in range(3):\n c.publish('{Topic}', b'{names[i]}#%d' % k, qos=1)\n time.sleep_ms(100)\n" +
                "print('PUBLISHED')\n"));
            cdc[i].SendSerialBytes("\x04"u8);
        }

        StepAll(60_000_000, () => rx.Skip(2).All(r => r.ToString().Contains("]")));
        StepAll(200_000, () => false);   // drain the trailing REPL output before reading it

        var simMs = _boards.Select(b => b.Board.Rp2040.Cpu.Cycles / 125_000.0).Average();
        output.WriteLine($"{sw.ElapsedMilliseconds} ms wall, {simMs:F0} ms simulated/board, " +
                         $"{simMs / sw.ElapsedMilliseconds:F2}x realtime per board across {_boards.Count} boards");
        for (var i = 0; i < names.Length; i++)
            output.WriteLine($"{names[i]}: {rx[i].ToString().Replace("\r", " ").Replace("\n", " | ")}");

        // Each board must have taken its own lease — a shared MAC silently collapses the fleet onto one.
        var ips = rx.Select(r => Between(r.ToString(), "NET 3 ", "\r")).ToArray();
        output.WriteLine("leases: " + string.Join(", ", ips));
        ips.Should().OnlyHaveUniqueItems("every board needs its own DHCP lease");

        foreach (var i in new[] { 2, 3 })
        {
            var got = rx[i].ToString();
            got.Should().Contain("GOT ", $"{names[i]} must finish its receive loop");
            foreach (var pub in new[] { "pub-a", "pub-b" })
                for (var k = 0; k < 3; k++)
                    got.Should().Contain($"{pub}#{k}", $"{names[i]} must receive {pub}#{k}");
        }
        _boards.Should().OnlyContain(b => !b.Board.Cpu.IsLockedUp);
    }

    private static string Join(string name) =>
        "import network, time\nfrom umqtt_simple import MQTTClient\n" +
        "w=network.WLAN(network.STA_IF)\nw.active(True)\n" +
        $"w.connect('{Ssid}', '{Password}')\n" +
        "n=0\nwhile w.status()!=3 and n<400:\n time.sleep_ms(50); n+=1\n" +
        "print('NET', w.status(), w.ifconfig()[0])\n" +
        $"c=MQTTClient('{name}', '192.168.4.1', port=1883, keepalive=60)\nc.connect()\nprint('MQTT')\n";

    private static string Between(string s, string start, string end)
    {
        var i = s.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return "(none)";
        i += start.Length;
        var j = s.IndexOfAny([.. end], i);
        return j < 0 ? s[i..] : s[i..j];
    }

    private static string PythonLiteral(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in s)
            sb.Append(ch switch { '\\' => "\\\\", '\'' => "\\'", '\n' => "\\n", '\r' => "\\r", _ => ch.ToString() });
        return sb.Append('\'').ToString();
    }
}

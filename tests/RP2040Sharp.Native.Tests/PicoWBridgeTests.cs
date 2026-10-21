using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SiliconTwin.Native;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Outbound bridges (W3) through the C ABI: st_rp2040_wifi_bridge_tcp relays a guest connection to 192.168.4.1:port to a
/// real server of the test on 127.0.0.1, and st_rp2040_wifi_dns_add gives the gateway's DNS a name for it. MicroPython
/// v1.28.0 RPI_PICO_W runs as the Proteus model runs it (run_until_edge(30000, 75) + st_rp2040_wifi_poll): a TCP echo
/// of several segments both ways, a refused host (the guest's connect() fails at once, ST_WIFI_EV_BRIDGE_FAILED), and
/// umqtt.simple publishing to and receiving from an MQTT broker of the test.
/// </summary>
public unsafe class PicoWBridgeTests(ITestOutputHelper output)
{
    private const uint Cyw43 = 0x100, BusAll = 7;
    private const int Led = 15;

    private sealed class Host
    {
        public readonly StringBuilder Cdc = new();
        public readonly List<(int Event, WifiStatus Status)> Events = [];
        public readonly List<(int Gpio, int Level)> Pads = [];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSerial(nint user, int channel, byte* data, nuint len)
    {
        var h = (Host)GCHandle.FromIntPtr(user).Target!;
        if (channel == 2) lock (h.Cdc) h.Cdc.Append(Encoding.Latin1.GetString(data, (int)len));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWifi(nint user, int ev, ulong cycle, WifiStatus* status)
    {
        var h = (Host)GCHandle.FromIntPtr(user).Target!;
        h.Events.Add((ev, *status));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPin(nint user, int gpio, int level, int oe, ulong cycle)
    {
        var h = (Host)GCHandle.FromIntPtr(user).Target!;
        if (gpio == Led && oe != 0) h.Pads.Add((gpio, level));
    }

    private static readonly delegate* unmanaged[Cdecl]<uint, uint, nint> CreateEx = &Exports.CreateEx;
    private static readonly delegate* unmanaged[Cdecl]<nint, void> Destroy = &Exports.Destroy;
    private static readonly delegate* unmanaged[Cdecl]<nint, int> Status = &Exports.Status;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*> LastError = &Exports.LastError;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> LoadUf2 = &Exports.LoadUf2;
    private static readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, ulong> RunUntilEdge = &Exports.RunUntilEdge;
    private static readonly delegate* unmanaged[Cdecl]<nint, uint, ulong> Run = &Exports.Run;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> SerialWrite = &Exports.SerialWrite;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>, nint, void> SetSerialCb = &Exports.SetSerialCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void>, nint, void> SetPinCb = &Exports.SetPinCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, ulong, WifiStatus*, void>, nint, void> SetWifiCb = &Exports.SetWifiCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int> SetAp = &Exports.WifiSetAp;
    private static readonly delegate* unmanaged[Cdecl]<nint, WifiStatus*, int> GetStatus = &Exports.WifiStatusOf;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, int, int> BridgeTcp = &Exports.WifiBridgeTcp;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int> DnsAdd = &Exports.WifiDnsAdd;
    private static readonly delegate* unmanaged[Cdecl]<nint, int> Poll = &Exports.WifiPoll;

    private static string Error(nint h) => Marshal.PtrToStringAnsi((nint)LastError(h))!;

    private static WifiStatus WifiStatusOf(nint h)
    {
        var st = new WifiStatus { Size = (uint)sizeof(WifiStatus) };
        Assert.Equal(0, GetStatus(h, &st));
        return st;
    }

    private static int Bridge(nint h, int gatewayPort, string? host, int port)
    {
        if (host is null) return BridgeTcp(h, gatewayPort, null, port);
        var b = Encoding.ASCII.GetBytes(host + "\0");
        fixed (byte* p = b) return BridgeTcp(h, gatewayPort, p, port);
    }

    private static int Dns(nint h, string? name, string? ipv4)
    {
        var n = name is null ? null : Encoding.ASCII.GetBytes(name + "\0");
        var i = ipv4 is null ? null : Encoding.ASCII.GetBytes(ipv4 + "\0");
        fixed (byte* pn = n) fixed (byte* pi = i) return DnsAdd(h, pn, pi);
    }

    /// <summary>A port nothing listens on: bound once, then released.</summary>
    internal static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>A chip with the AP set, driven like Proteus on the test thread.</summary>
    private sealed class Session : IDisposable
    {
        public readonly Host Host = new();
        private readonly GCHandle _gc;
        public nint H;
        public long Calls;

        public Session(byte[] uf2, Action<nint> configure)
        {
            _gc = GCHandle.Alloc(Host);
            H = CreateEx(0, BusAll | Cyw43);
            Assert.NotEqual(0, H);
            SetSerialCb(H, &OnSerial, GCHandle.ToIntPtr(_gc));
            SetWifiCb(H, &OnWifi, GCHandle.ToIntPtr(_gc));
            SetPinCb(H, &OnPin, GCHandle.ToIntPtr(_gc));
            var ssid = "Proteus\0"u8.ToArray();
            var pass = "proteus-pass\0"u8.ToArray();
            fixed (byte* s = ssid) fixed (byte* p = pass) Assert.Equal(0, SetAp(H, s, p, 0, 0));
            configure(H);
            fixed (byte* p = uf2) Assert.True(LoadUf2(H, p, (nuint)uf2.Length) > 1000);
        }

        public string Cdc { get { lock (Host.Cdc) return Host.Cdc.ToString(); } }

        public void Step()
        {
            RunUntilEdge(H, 30_000, 75, PicoWIdleTests.ProteusWatch);
            Assert.True(Poll(H) >= 0, Error(H));
            Assert.True(Status(H) == 0, Error(H));
            Calls++;
        }

        public void RunUntil(Func<bool> done, double wallSeconds, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!done())
            {
                Step();
                if (sw.Elapsed.TotalSeconds > wallSeconds) Assert.Fail("timed out waiting for " + what + "; CDC: " + Cdc);
            }
        }

        public void Write(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            fixed (byte* p = b) SerialWrite(H, 2, p, (nuint)b.Length);
        }

        /// <summary>To the raw REPL, then run <paramref name="code"/> there.</summary>
        public void RawRepl(string code)
        {
            RunUntil(() => Cdc.Contains(">>> "), 60, "the REPL");
            Write("\x01");
            RunUntil(() => Cdc.Contains("raw REPL"), 30, "the raw REPL");
            Write(code + "\x04");
        }

        public void Dispose()
        {
            if (H != 0) Destroy(H);
            H = 0;
            if (_gc.IsAllocated) _gc.Free();
        }
    }

    private const string Join =
        "import network,time,socket,machine\n" +
        "w=network.WLAN(network.STA_IF)\nw.active(True)\nw.connect('Proteus','proteus-pass')\n" +
        "while not w.isconnected():\n time.sleep_ms(20)\n" +
        "print('JOINED',w.ifconfig()[0])\n";

    [SkippableFact]
    public void Guest_reaches_an_echo_server_by_name_and_a_refused_host_fails_at_once()
    {
        var uf2 = PicoWForwardTests.PicoWImage();
        using var echo = new EchoServer();
        var closed = ClosedPort();
        using var s = new Session(uf2, h =>
        {
            Assert.Equal(0, Bridge(h, 7777, "127.0.0.1", echo.Port));
            Assert.Equal(0, Bridge(h, 7778, "127.0.0.1", closed));
            Assert.Equal(0, Dns(h, "echo.local", "192.168.4.1"));
        });
        var sw = Stopwatch.StartNew();
        s.RawRepl(Join +
            "a=socket.getaddrinfo('echo.local',7777)[0][-1]\nprint('ADDR',a)\n" +
            "data=bytes((i*7)&255 for i in range(8000))\n" +
            "c=socket.socket()\nc.connect(a)\nc.write(data)\n" +
            "r=b''\nwhile len(r)<len(data):\n d=c.recv(2048)\n if not d: break\n r+=d\n" +
            "print('ECHO',len(r),r==data)\nc.close()\n" +
            "c=socket.socket()\nt=time.ticks_ms()\n" +
            "try:\n c.connect(('192.168.4.1',7778))\n print('CONNECTED?')\n" +
            "except OSError as e:\n print('REFUSED',e.errno,time.ticks_diff(time.ticks_ms(),t))\nc.close()\n" +
            "try:\n socket.getaddrinfo('nowhere.local',80)\n print('RESOLVED?')\nexcept OSError as e:\n print('NXDOMAIN')\n" +
            "print('DONE')\n");
        s.RunUntil(() => s.Cdc.Contains("DONE") || s.Cdc.Contains("Traceback"), 120, "the script");
        s.RunUntil(() => WifiStatusOf(s.H).BridgeOpen == 0, 30, "the bridged connections to finish");
        output.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s wall, {s.Calls} run calls");
        output.WriteLine(s.Cdc.Replace("\r", ""));
        var cdc = s.Cdc;
        Assert.DoesNotContain("Traceback", cdc);
        Assert.Contains("ADDR ('192.168.4.1', 7777)", cdc);
        Assert.Contains("ECHO 8000 True", cdc);
        Assert.Contains("REFUSED 104 ", cdc);                                // ECONNRESET: our reset, not the guest giving up
        Assert.Contains("NXDOMAIN", cdc);
        Assert.Equal(1, echo.Accepted);
        Assert.Equal(8000, Interlocked.Read(ref echo.Bytes));

        var st = WifiStatusOf(s.H);
        output.WriteLine($"bridges {st.Bridges}, connected {st.BridgeConnected}, failed {st.BridgeFailed}, to host {st.BridgeToHost} B, " +
                         $"to guest {st.BridgeToGuest} B; events: " + string.Join(",", s.Host.Events.Select(e => e.Event)));
        Assert.Equal(208u, st.Size);
        Assert.Equal(2u, st.Bridges);
        Assert.Equal(0u, st.BridgeOpen);
        Assert.Equal(1ul, st.BridgeConnected);
        Assert.Equal(1ul, st.BridgeFailed);
        Assert.Equal(8000ul, st.BridgeToHost);
        Assert.Equal(8000ul, st.BridgeToGuest);
        Assert.Equal((ushort)TcpForwarderError.Refused, st.BridgeError);
        var bridgeEvents = s.Host.Events.Where(e => e.Event >= ChipWifi.EvBridgeOpen).ToList();
        // The first connection's close may come after the second's refusal (its FIN exchange ends later): any order.
        Assert.Equal([ChipWifi.EvBridgeOpen, ChipWifi.EvBridgeClose, ChipWifi.EvBridgeFailed], bridgeEvents.Select(e => e.Event).Order());
        Assert.Equal(ChipWifi.EvBridgeOpen, bridgeEvents[0].Event);
        var opened = bridgeEvents[0].Status;
        var failed = bridgeEvents.Single(e => e.Event == ChipWifi.EvBridgeFailed).Status;
        Assert.Equal(7777, opened.BridgeGatewayPort);
        Assert.Equal(echo.Port, opened.BridgeHostPort);
        Assert.NotEqual(0, opened.BridgeGuestPort);
        Assert.Equal(7778, failed.BridgeGatewayPort);
        Assert.Equal(closed, failed.BridgeHostPort);
        Assert.Equal((ushort)TcpForwarderError.Refused, failed.BridgeError);
        Assert.Equal(0u, st.TcpForwards + st.TcpOpen);   // the forwards' counters are untouched
        Assert.Equal(0ul, st.TcpAccepted);
    }

    private enum TcpForwarderError : ushort { Refused = 1, Unreachable = 2, NoHost = 3, Full = 4 }

    /// <summary>umqtt.simple from micropython-lib (MIT), the file the RP2040 suite pins: commit
    /// 5e49b1bd41d312d9d2a8e4f19d7bd6a918896abc, SHA-256 below. Read from RP2040_UMQTT_SIMPLE or the suites' asset caches;
    /// never downloaded here.</summary>
    internal static string? UmqttSimple()
    {
        const string sha = "3cfd101ef774e0c6f9543425650ddc36168a7bbf4b8b8b71667e27023dfd951f";
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("RP2040_UMQTT_SIMPLE");
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);
        candidates.Add(Path.Combine(Path.GetTempPath(), "rp2350sharp-asset-cache", "umqtt_simple.py"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "rp2040sharp-asset-cache", "umqtt_simple.py"));
        foreach (var c in candidates)
            if (File.Exists(c) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(c))).Equals(sha, StringComparison.OrdinalIgnoreCase))
                return File.ReadAllText(c);
        return null;
    }

    internal static string PythonLiteral(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in s)
            sb.Append(ch switch { '\\' => "\\\\", '\'' => "\\'", '\n' => "\\n", '\r' => "\\r", _ => ch.ToString() });
        return sb.Append('\'').ToString();
    }

    [SkippableFact]
    public void Umqtt_publishes_to_a_broker_on_the_pc_and_receives_what_the_pc_publishes()
    {
        var uf2 = PicoWForwardTests.PicoWImage();
        var umqtt = UmqttSimple();
        Skip.If(umqtt is null, "umqtt.simple not found (RP2040_UMQTT_SIMPLE, or the RP2040 suite's asset cache)");
        using var broker = new MqttTestBroker();
        using var s = new Session(uf2, h =>
        {
            Assert.Equal(0, Bridge(h, 1883, "127.0.0.1", broker.Port));
            Assert.Equal(0, Dns(h, "broker.local", "192.168.4.1"));
        });
        var sw = Stopwatch.StartNew();
        s.RawRepl("_f=open('umqtt_simple.py','w')\n_f.write(" + PythonLiteral(umqtt!) + ")\n_f.close()\nprint('LIBOK')\n");
        s.RunUntil(() => s.Cdc.Contains("LIBOK") || s.Cdc.Contains("Traceback"), 120, "the library upload");
        Assert.Contains("LIBOK", s.Cdc);
        var at = s.Cdc.Length;
        s.Write(Join +
            "from umqtt_simple import MQTTClient\n" +
            "led=machine.Pin(15,machine.Pin.OUT,value=0)\ngot=[]\n" +
            "def cb(t,m):\n got.append(m)\n led.value(1 if m==b'on' else 0)\n print('GOT',t,m)\n" +
            "c=MQTTClient('picow','broker.local',port=1883,keepalive=30)\nc.set_callback(cb)\nc.connect()\nprint('MQTT CONNECTED')\n" +
            "c.subscribe(b'pico/led')\nprint('SUBSCRIBED')\n" +
            "n=0\nwhile n<200 and not (got and n>=3):\n" +
            " c.publish(b'pico/telemetry',b'{\"count\":%d}' % n)\n n+=1\n" +
            " for _ in range(10):\n  c.check_msg()\n  time.sleep_ms(10)\n" +
            "c.publish(b'pico/ack',b'led=%d' % led.value())\nc.disconnect()\nprint('MQTT DONE',n)\n\x04");
        s.RunUntil(() => broker.Subscriptions.Contains("pico/led") || s.Cdc.IndexOf("Traceback", at, StringComparison.Ordinal) >= 0,
                   120, "the subscription");
        Assert.DoesNotContain("Traceback", s.Cdc[at..]);
        output.WriteLine($"subscribed after {sw.Elapsed.TotalSeconds:F1} s wall");
        s.RunUntil(() => broker.Published.Count(p => p.Topic == "pico/telemetry") >= 2, 60, "telemetry");
        broker.Publish("pico/led", "on");
        s.RunUntil(() => s.Cdc.Contains("MQTT DONE") || s.Cdc.IndexOf("Traceback", at, StringComparison.Ordinal) >= 0, 120, "the client loop");
        s.RunUntil(() => WifiStatusOf(s.H).BridgeOpen == 0, 30, "the MQTT connection to finish");
        output.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s wall, {s.Calls} run calls");
        output.WriteLine(s.Cdc[at..].Replace("\r", ""));
        var cdc = s.Cdc[at..];
        Assert.DoesNotContain("Traceback", cdc);
        Assert.Contains("MQTT CONNECTED", cdc);
        Assert.Contains("GOT b'pico/led' b'on'", cdc);
        Assert.Contains((Led, 1), s.Host.Pads);
        var pubs = broker.Published;
        Assert.True(pubs.Count(p => p.Topic == "pico/telemetry") >= 3, "telemetry published");
        Assert.Contains(pubs, p => p.Topic == "pico/telemetry" && Encoding.ASCII.GetString(p.Payload) == "{\"count\":0}");
        Assert.Contains(pubs, p => p.Topic == "pico/ack" && Encoding.ASCII.GetString(p.Payload) == "led=1");
        Assert.Equal(1, broker.Connects);
        var st = WifiStatusOf(s.H);
        Assert.Equal(1ul, st.BridgeConnected);
        Assert.Equal(0ul, st.BridgeFailed);
        Assert.True(st.BridgeToHost > 50 && st.BridgeToGuest > 4);
        Assert.Contains(s.Host.Events, e => e.Event == ChipWifi.EvBridgeOpen && e.Status.BridgeGatewayPort == 1883);
    }

    // Arguments, and chips without the radio; no firmware.
    [Fact]
    public void Bridge_and_dns_arguments_are_checked()
    {
        var plain = CreateEx(0, BusAll);
        var w = CreateEx(0, BusAll | Cyw43);
        try
        {
            Assert.Equal(-1, Bridge(plain, 1883, "127.0.0.1", 1883));
            Assert.Contains("no CYW43439", Error(plain));
            Assert.Equal(-1, Dns(plain, "broker.local", "192.168.4.1"));
            Assert.Contains("no CYW43439", Error(plain));
            Assert.Equal(-1, Bridge(0, 1883, "127.0.0.1", 1883));
            Assert.Equal(-1, Dns(0, "broker.local", "192.168.4.1"));

            Assert.Equal(-1, Bridge(w, 0, "127.0.0.1", 1883));
            Assert.Contains("gateway_port", Error(w));
            Assert.Equal(-1, Bridge(w, 65536, "127.0.0.1", 1883));
            Assert.Equal(-1, Bridge(w, 1883, "127.0.0.1", -1));
            Assert.Equal(-1, Bridge(w, 1883, "127.0.0.1", 65536));
            Assert.Contains("port", Error(w));
            Assert.Equal(-1, Bridge(w, 1883, null, 1883));
            Assert.Contains("host", Error(w));
            Assert.Equal(-1, Bridge(w, 1883, "", 1883));
            Assert.Equal(-1, Bridge(w, 1883, "bad host", 1883));
            Assert.Contains("character", Error(w));
            Assert.Equal(-1, Bridge(w, 1883, new string('a', 254), 1883));
            Assert.Contains("253", Error(w));
            Assert.Equal(-1, Bridge(w, 1883, "[::1", 1883));
            Assert.Equal(-1, Bridge(w, 1883, "1::2::3", 1883));
            Assert.Equal(-1, Bridge(w, 1883, null, 0));                          // nothing to remove
            Assert.Contains("not bridged", Error(w));

            Assert.Equal(0, Bridge(w, 1883, "127.0.0.1", 1883));
            Assert.Equal(-1, Bridge(w, 1883, "localhost", 1884));
            Assert.Contains("already bridged", Error(w));
            Assert.Equal(0, Bridge(w, 8883, "[::1]", 8883));
            Assert.Equal(0, Bridge(w, 80, "example.invalid", 80));               // names resolve at each connection
            Assert.Equal(3u, WifiStatusOf(w).Bridges);
            Assert.Equal(0, Bridge(w, 80, null, 0));                             // removed (host ignored)
            Assert.Equal(2u, WifiStatusOf(w).Bridges);
            for (var p = 2; WifiStatusOf(w).Bridges < 16; p++) Assert.Equal(0, Bridge(w, 10000 + p, "127.0.0.1", 1));
            Assert.Equal(-1, Bridge(w, 9999, "127.0.0.1", 1));
            Assert.Contains("16", Error(w));
            Assert.Equal(0, Poll(w));                                            // nothing open

            Assert.Equal(0, Dns(w, "broker.local", "192.168.4.1"));
            Assert.Equal(0, Dns(w, "BROKER.local.", "192.168.4.7"));            // the same name: replaced
            Assert.Equal(0, Dns(w, "a_b-c.d9", "10.0.0.1"));
            Assert.Equal(-1, Dns(w, null, "1.2.3.4"));
            Assert.Contains("name", Error(w));
            Assert.Equal(-1, Dns(w, "", "1.2.3.4"));
            Assert.Equal(-1, Dns(w, "bad name", "1.2.3.4"));
            Assert.Equal(-1, Dns(w, "a..b", "1.2.3.4"));
            Assert.Contains("label", Error(w));
            Assert.Equal(-1, Dns(w, new string('a', 64) + ".x", "1.2.3.4"));
            Assert.Equal(-1, Dns(w, string.Join('.', Enumerable.Repeat("abc", 64)), "1.2.3.4"));   // 255 bytes
            Assert.Equal(-1, Dns(w, "x.local", "1.2.3"));
            Assert.Contains("IPv4", Error(w));
            Assert.Equal(-1, Dns(w, "x.local", "1.2.3.256"));
            Assert.Equal(-1, Dns(w, "x.local", "1.2.3.4 "));
            Assert.Equal(-1, Dns(w, "x.local", "::1"));
            Assert.Equal(-1, Dns(w, "x.local", null));                           // not there to remove
            Assert.Contains("not there", Error(w));
            Assert.Equal(0, Dns(w, "broker.local", null));                       // removed
            Assert.Equal(0, Dns(w, "a_b-c.d9", ""));
            for (var i = 0; i < 64; i++) Assert.Equal(0, Dns(w, "n" + i, "1.1.1.1"));
            Assert.Equal(-1, Dns(w, "n64", "1.1.1.1"));
            Assert.Contains("64", Error(w));
            Assert.Equal(0, Dns(w, "n3", "2.2.2.2"));                           // replacing still works when full

            var st = WifiStatusOf(w);
            Assert.Equal(208u, st.Size);
            Assert.Equal(16u, st.Bridges);
            Assert.Equal(0ul, st.BridgeConnected + st.BridgeFailed);
            Assert.Equal(208, sizeof(WifiStatus));
            Assert.Equal(160, (int)Marshal.OffsetOf<WifiStatus>(nameof(WifiStatus.Bridges)));
            Assert.Equal(168, (int)Marshal.OffsetOf<WifiStatus>(nameof(WifiStatus.BridgeConnected)));
            Assert.Equal(200, (int)Marshal.OffsetOf<WifiStatus>(nameof(WifiStatus.BridgeGatewayPort)));
            Assert.Equal(206, (int)Marshal.OffsetOf<WifiStatus>(nameof(WifiStatus.BridgeError)));
            var old = new WifiStatus { Size = 160 };                             // a host built before the bridges
            Assert.Equal(0, GetStatus(w, &old));
            Assert.Equal(160u, old.Size);
            Assert.Equal(0u, old.Bridges);
            Run(w, 20_000);
            Assert.Equal(0, Status(w));
            Assert.Equal(0, Status(plain));
        }
        finally { Destroy(plain); Destroy(w); }
    }
}

/// <summary>A TCP echo server on 127.0.0.1: every byte comes back, until the client closes.</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public int Accepted;
    public long Bytes;

    public EchoServer()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                for (;;)
                {
                    var c = await _l.AcceptTcpClientAsync();
                    Interlocked.Increment(ref Accepted);
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            int n;
                            try
                            {
                                while ((n = await s.ReadAsync(buf)) > 0)
                                {
                                    Interlocked.Add(ref Bytes, n);
                                    await s.WriteAsync(buf.AsMemory(0, n));
                                }
                            }
                            catch (IOException) { /* reset */ }
                        }
                    });
                }
            }
            catch (Exception) { /* stopped */ }
        });
    }

    public void Dispose() => _l.Stop();
}

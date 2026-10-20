using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SiliconTwin.Native;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Port forwarding (W2) through the C ABI: st_rp2040_wifi_forward_tcp listens on 127.0.0.1 and relays each connection to
/// a server inside MicroPython v1.28.0 RPI_PICO_W, while the test thread drives the chip as the Proteus model does
/// (run_until_edge(30000, 75, the symbol's pads) and st_rp2040_wifi_poll after each call). Real HttpClient / TcpClient
/// requests from the host: sequential, three in parallel, a POST that toggles GP15 (seen as a pad event), a response of
/// many segments, a request body of several segments, close and reopen, and the port free again after destroy.
/// </summary>
public unsafe class PicoWForwardTests(ITestOutputHelper output)
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
    private static readonly delegate* unmanaged[Cdecl]<nint, ulong> TimePs = &Exports.TimePs;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> SerialWrite = &Exports.SerialWrite;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>, nint, void> SetSerialCb = &Exports.SetSerialCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void>, nint, void> SetPinCb = &Exports.SetPinCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, ulong, WifiStatus*, void>, nint, void> SetWifiCb = &Exports.SetWifiCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int> SetAp = &Exports.WifiSetAp;
    private static readonly delegate* unmanaged[Cdecl]<nint, WifiStatus*, int> GetStatus = &Exports.WifiStatusOf;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, int, int> Forward = &Exports.WifiForwardTcp;
    private static readonly delegate* unmanaged[Cdecl]<nint, int> Poll = &Exports.WifiPoll;

    private static string Error(nint h) => Marshal.PtrToStringAnsi((nint)LastError(h))!;

    private static WifiStatus WifiStatusOf(nint h)
    {
        var st = new WifiStatus { Size = (uint)sizeof(WifiStatus) };
        Assert.Equal(0, GetStatus(h, &st));
        return st;
    }

    internal const string Server =
        "import network,time,socket,machine\n" +
        "w=network.WLAN(network.STA_IF)\nw.active(True)\nw.connect('Proteus','proteus-pass')\n" +
        "while not w.isconnected():\n time.sleep_ms(20)\n" +
        "led=machine.Pin(15,machine.Pin.OUT,value=0)\n" +
        "s=socket.socket()\ns.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)\ns.bind(('0.0.0.0',80))\ns.listen(4)\n" +
        "print('LISTENING',w.ifconfig()[0])\n" +
        "n=0\n" +
        "while True:\n" +
        " c,a=s.accept()\n" +
        " n+=1\n" +
        " r=b''\n" +
        " while b'\\r\\n\\r\\n' not in r:\n" +
        "  d=c.recv(1024)\n" +
        "  if not d: break\n" +
        "  r+=d\n" +
        " h,_,b=r.partition(b'\\r\\n\\r\\n')\n" +
        " ln=0\n" +
        " for l in h.split(b'\\r\\n'):\n" +
        "  if l.lower().startswith(b'content-length:'): ln=int(l[15:])\n" +
        " while len(b)<ln:\n" +
        "  d=c.recv(1024)\n" +
        "  if not d: break\n" +
        "  b+=d\n" +
        " p=h.split(b' ')[1] if h.count(b' ')>1 else b'/'\n" +
        " if p==b'/led':\n" +
        "  led.value(1 if b'on' in b else 0)\n" +
        "  body='LED %d' % led.value()\n" +
        " elif p==b'/big':\n" +
        "  body='0123456789'*2000\n" +
        " elif p==b'/echo':\n" +
        "  body='LEN %d SUM %d' % (len(b), sum(b))\n" +
        " else:\n" +
        "  body='Hello #%d from %s' % (n, a[0])\n" +
        " c.write(('HTTP/1.1 200 OK\\r\\nContent-Type: text/plain\\r\\nContent-Length: %d\\r\\nConnection: close\\r\\n\\r\\n' % len(body)).encode())\n" +
        " c.write(body.encode())\n" +
        " c.close()\n";

    /// <summary>A chip booted to a listening server, driven like Proteus on the test thread.</summary>
    private sealed class Session : IDisposable
    {
        public readonly Host Host = new();
        private readonly GCHandle _gc;
        public nint H;
        public int Port;
        public long Calls;

        public Session(byte[] uf2, int hostPort)
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
            Port = Forward(H, hostPort, 80);
            Assert.True(Port > 0, Error(H));
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

        public T Drive<T>(Task<T> task, double wallSeconds = 60)
        {
            RunUntil(() => task.IsCompleted, wallSeconds, "a host request");
            return task.GetAwaiter().GetResult();
        }

        public void Write(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            fixed (byte* p = b) SerialWrite(H, 2, p, (nuint)b.Length);
        }

        public void Boot()
        {
            RunUntil(() => Cdc.Contains(">>> "), 60, "the REPL");
            Write("\x01");
            RunUntil(() => Cdc.Contains("raw REPL"), 30, "the raw REPL");
            Write(Server + "\x04");
            RunUntil(() => Cdc.Contains("LISTENING 192.168.4.2"), 60, "the server");
        }

        public void Dispose()
        {
            if (H != 0) Destroy(H);
            H = 0;
            if (_gc.IsAllocated) _gc.Free();
        }
    }

    internal static byte[] PicoWImage()
    {
        var path = RP2040Sharp.Native.Tests.Firmware.MicroPythonPicoW();
        Skip.If(path is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        return File.ReadAllBytes(path!);
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };

    [SkippableFact]
    public void Browser_requests_reach_a_micropython_server_through_the_forward()
    {
        var uf2 = PicoWImage();
        int port;
        using (var s = new Session(uf2, 0))
        {
            port = s.Port;
            var bootWall = Stopwatch.StartNew();
            s.Boot();
            output.WriteLine($"boot + join + listen: {bootWall.Elapsed.TotalSeconds:F2} s wall, {TimePs(s.H) * 1e-12:F3} s simulated");
            using var http = Client();
            var url = $"http://127.0.0.1:{port}";

            // Sequential: each request a new connection (the server closes), latency in wall time.
            var lat = new List<double>();
            for (var i = 1; i <= 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var sim0 = TimePs(s.H);
                var body = s.Drive(http.GetStringAsync(url + "/"));
                lat.Add(sw.Elapsed.TotalMilliseconds);
                output.WriteLine($"GET / #{i}: {sw.Elapsed.TotalMilliseconds:F0} ms wall, {(TimePs(s.H) - sim0) * 1e-9:F1} ms simulated");
                Assert.Equal($"Hello #{i} from 192.168.4.1", body);
            }
            output.WriteLine("GET / latency (ms wall): " + string.Join(", ", lat.Select(l => l.ToString("F0"))));

            // Three in parallel: three connections, served one after the other by the guest's single accept loop.
            var sw3 = Stopwatch.StartNew();
            var par = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => http.GetStringAsync(url + "/x")));
            var bodies = s.Drive(par);
            output.WriteLine($"3 parallel GETs: {sw3.Elapsed.TotalMilliseconds:F0} ms wall");
            Assert.All(bodies, b => Assert.StartsWith("Hello #", b));
            Assert.Equal(3, bodies.Distinct().Count());

            // A POST that switches GP15: the pad event reaches the pin callback.
            var on = s.Drive(http.PostAsync(url + "/led", new StringContent("on")));
            Assert.Equal("LED 1", s.Drive(on.Content.ReadAsStringAsync()));
            Assert.Contains((Led, 1), s.Host.Pads);
            var off = s.Drive(http.PostAsync(url + "/led", new StringContent("off")));
            Assert.Equal("LED 0", s.Drive(off.Content.ReadAsStringAsync()));
            Assert.Equal((Led, 0), s.Host.Pads[^1]);

            // A response of many segments, and a request body of several.
            var swBig = Stopwatch.StartNew();
            var simBig = TimePs(s.H);
            var big = s.Drive(http.GetStringAsync(url + "/big"));
            output.WriteLine($"GET /big (20000 bytes): {swBig.Elapsed.TotalMilliseconds:F0} ms wall, {(TimePs(s.H) - simBig) * 1e-9:F1} ms simulated");
            Assert.Equal(string.Concat(Enumerable.Repeat("0123456789", 2000)), big);
            var payload = Enumerable.Range(0, 6000).Select(i => (byte)(i * 7)).ToArray();
            var echo = s.Drive(http.PostAsync(url + "/echo", new ByteArrayContent(payload)));
            Assert.Equal($"LEN 6000 SUM {payload.Sum(b => (int)b)}", s.Drive(echo.Content.ReadAsStringAsync()));

            // A raw client: connect, request, read to the server's close, close; then again on a new connection.
            for (var round = 0; round < 2; round++)
            {
                var text = s.Drive(RawHttpW.Get(port));
                Assert.StartsWith("HTTP/1.1 200 OK", text);
                Assert.Contains("Hello #", text);
            }

            // Everything closed again; the status counts it all.
            s.RunUntil(() => WifiStatusOf(s.H).TcpOpen == 0, 30, "the connections to finish");
            var st = WifiStatusOf(s.H);
            output.WriteLine($"accepted {st.TcpAccepted}, failed {st.TcpFailed}, to guest {st.TcpToGuest} B, from guest {st.TcpFromGuest} B, " +
                             $"{s.Calls} run calls; events: " + string.Join(",", s.Host.Events.Select(e => e.Event)));
            Assert.Equal(160u, st.Size);
            Assert.Equal(1u, st.TcpForwards);
            Assert.Equal(0u, st.TcpFailed);
            Assert.True(st.TcpAccepted >= 12, "every request was a connection");
            Assert.True(st.TcpToGuest > 6000 && st.TcpFromGuest > 20000);
            var opens = s.Host.Events.Count(e => e.Event == ChipWifi.EvTcpOpen);
            Assert.Equal(opens, s.Host.Events.Count(e => e.Event == ChipWifi.EvTcpClose));
            Assert.True(opens >= 12);
            var ev = s.Host.Events.First(e => e.Event == ChipWifi.EvTcpOpen).Status;
            Assert.Equal(port, ev.TcpHostPort);
            Assert.Equal(80, ev.TcpGuestPort);
            Assert.NotEqual(0, ev.TcpPeerPort);
        }

        // Destroyed: the port is closed, and a new chip can forward it again (a Proteus restart).
        Assert.ThrowsAny<SocketException>(() => { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); });
        using (var again = new Session(uf2, port)) Assert.Equal(port, again.Port);
    }

    // Arguments, and chips without the radio; no firmware.
    [Fact]
    public void Forward_arguments_are_checked_and_destroy_frees_the_port()
    {
        var plain = CreateEx(0, BusAll);
        var w = CreateEx(0, BusAll | Cyw43);
        var w2 = CreateEx(0, BusAll | Cyw43);
        try
        {
            Assert.Equal(-1, Forward(plain, 0, 80));
            Assert.Contains("no CYW43439", Error(plain));
            Assert.Equal(-1, Poll(plain));
            Assert.Equal(-1, Forward(0, 0, 80));
            Assert.Equal(-1, Poll(0));
            Assert.Equal(0, Poll(w));                                            // nothing forwarded: nothing open
            Assert.Equal(-1, Forward(w, -1, 80));
            Assert.Contains("host_port", Error(w));
            Assert.Equal(-1, Forward(w, 65536, 80));
            Assert.Equal(-1, Forward(w, 0, 70000));
            Assert.Equal(-1, Forward(w, 8080, 0));                               // not forwarded: nothing to remove
            Assert.Contains("not forwarded", Error(w));
            var p = Forward(w, 0, 80);
            Assert.True(p > 0, Error(w));
            Assert.Equal(-1, Forward(w, p, 81));
            Assert.Contains("already forwarded", Error(w));
            Assert.Equal(-1, Forward(w2, p, 80));                                // another chip, the same port
            Assert.Contains("in use", Error(w2));
            using (var c = new TcpClient()) c.Connect(IPAddress.Loopback, p);    // listening on loopback
            // No station yet: the connection waits for the guest; the client giving up ends it.
            var sw = Stopwatch.StartNew();
            while (WifiStatusOf(w).TcpAccepted == 0 || WifiStatusOf(w).TcpOpen != 0)
            {
                Assert.True(Poll(w) >= 0, Error(w));
                Assert.True(sw.Elapsed.TotalSeconds < 10, "the closed client's connection never ended");
                Thread.Sleep(1);
            }
            var st = WifiStatusOf(w);
            Assert.Equal(1u, st.TcpForwards);
            Assert.Equal(1ul, st.TcpAccepted);
            Assert.Equal(0ul, st.TcpFailed);
            Assert.Equal(0, Forward(w, p, 0));                                   // removed
            Assert.Equal(p, Forward(w2, p, 80));                                 // free again
            Destroy(w2);
            w2 = 0;
            Assert.Equal(p, Forward(w, p, 80));                                  // destroy freed it
            var q = Forward(w, 0, 81);
            Assert.True(q > 0 && q != p);
            Assert.Equal(2u, WifiStatusOf(w).TcpForwards);
            Assert.Equal(0, Status(w));
        }
        finally { Destroy(plain); Destroy(w); if (w2 != 0) Destroy(w2); }
    }
}

/// <summary>A minimal HTTP client on a raw socket (async code cannot live in the unsafe test class).</summary>
internal static class RawHttpW
{
    internal static async Task<string> Get(int port)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var ns = c.GetStream();
        await ns.WriteAsync("GET /raw HTTP/1.1\r\nHost: pico\r\n\r\n"u8.ToArray());
        using var ms = new MemoryStream();
        await ns.CopyToAsync(ms);   // to the server's FIN
        return Encoding.ASCII.GetString(ms.ToArray());
    }
}

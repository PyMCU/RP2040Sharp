using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SiliconTwin.Native;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The Pico W's virtual access point through the C ABI (st_rp2040_wifi_set_ap / _wifi_status / _set_wifi_callback),
/// with MicroPython v1.28.0 RPI_PICO_W driven as the Proteus model drives it: run_until_edge at guard 75 from power-on,
/// the symbol's pads watched. The host types a script over the USB CDC that scans, connects and prints ifconfig().
/// Every export is called through an unmanaged function pointer, as a C host calls it.
/// </summary>
public unsafe class PicoWWifiTests(ITestOutputHelper output)
{
    private const uint Cyw43 = 0x100, BusAll = 7;

    private sealed class Host
    {
        public readonly StringBuilder Cdc = new();
        public readonly List<(int Event, ulong Cycle, WifiStatus Status)> Events = [];
        public readonly Stopwatch Wall = Stopwatch.StartNew();
        public double LeaseWallSeconds = -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSerial(nint user, int channel, byte* data, nuint len)
    {
        var h = (Host)GCHandle.FromIntPtr(user).Target!;
        if (channel == 2) h.Cdc.Append(Encoding.Latin1.GetString(data, (int)len));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWifi(nint user, int ev, ulong cycle, WifiStatus* status)
    {
        var h = (Host)GCHandle.FromIntPtr(user).Target!;
        h.Events.Add((ev, cycle, *status));
        if (ev == ChipWifi.EvLease && h.LeaseWallSeconds < 0) h.LeaseWallSeconds = h.Wall.Elapsed.TotalSeconds;
    }

    private static readonly delegate* unmanaged[Cdecl]<uint, uint, nint> CreateEx = &Exports.CreateEx;
    private static readonly delegate* unmanaged[Cdecl]<nint, void> Destroy = &Exports.Destroy;
    private static readonly delegate* unmanaged[Cdecl]<nint, int> Status = &Exports.Status;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*> LastError = &Exports.LastError;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> LoadUf2 = &Exports.LoadUf2;
    private static readonly delegate* unmanaged[Cdecl]<nint, uint, uint, uint, ulong> RunUntilEdge = &Exports.RunUntilEdge;
    private static readonly delegate* unmanaged[Cdecl]<nint, ulong, ulong> CycleToPs = &Exports.CycleToPs;
    private static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> SerialWrite = &Exports.SerialWrite;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>, nint, void> SetSerialCb = &Exports.SetSerialCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, ulong, WifiStatus*, void>, nint, void> SetWifiCb = &Exports.SetWifiCallback;
    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int, int> SetAp = &Exports.WifiSetAp;
    private static readonly delegate* unmanaged[Cdecl]<nint, WifiStatus*, int> GetStatus = &Exports.WifiStatusOf;

    private static int WifiSetAp(nint h, string? ssid, string? pass, int channel = 0, int rssi = 0)
    {
        var s = ssid is null ? null : Encoding.UTF8.GetBytes(ssid + "\0");
        var p = pass is null ? null : Encoding.UTF8.GetBytes(pass + "\0");
        fixed (byte* ps = s) fixed (byte* pp = p) return SetAp(h, ps, pp, channel, rssi);
    }

    private static WifiStatus WifiStatusOf(nint h)
    {
        var st = new WifiStatus { Size = (uint)sizeof(WifiStatus) };
        Assert.Equal(0, GetStatus(h, &st));
        return st;
    }

    private static string Error(nint h) => Marshal.PtrToStringAnsi((nint)LastError(h))!;
    private static string Ip(byte* b) => $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
    private static string Ssid(WifiStatus s) => Marshal.PtrToStringUTF8((nint)s.Ssid)!;
    private static string Lease(WifiStatus s) => Ip(s.Ipv4);

    private static string Script(string ssid, string? pass) =>
        "exec(\"import network,time\\nw=network.WLAN(network.STA_IF)\\nw.active(True)\\n" +
        "print('SCAN', [(s[0].decode(), s[2], s[3]) for s in w.scan()])\\n" +
        $"w.connect('{ssid}'" + (pass is null ? "" : $", '{pass}'") + ")\\n" +
        "for _ in range(400):\\n if w.isconnected() or w.status() < 0: break\\n time.sleep_ms(20)\\n" +
        "print('CONN', w.isconnected(), w.status(), w.ifconfig())\")\r";

    private sealed record Outcome(string Transcript, List<(int Event, ulong Cycle, WifiStatus Status)> Events, WifiStatus Final,
        double LeaseWall, double TotalWall, double LeaseSimSeconds, double ReplSimSeconds);

    /// <summary>Power-on to the script's CONN line, every call run_until_edge(30000, 75, the symbol's pads).</summary>
    private static Outcome Session(byte[] uf2, string apSsid, string? apPass, string joinSsid, string? joinPass)
    {
        var host = new Host();
        var gc = GCHandle.Alloc(host);
        var h = CreateEx(0, BusAll | Cyw43);
        Assert.NotEqual(0, h);
        try
        {
            SetSerialCb(h, &OnSerial, GCHandle.ToIntPtr(gc));
            SetWifiCb(h, &OnWifi, GCHandle.ToIntPtr(gc));
            Assert.Equal(0, WifiSetAp(h, apSsid, apPass));                  // before the firmware, as the model does
            fixed (byte* p = uf2) Assert.True(LoadUf2(h, p, (nuint)uf2.Length) > 1000);
            double replAt = -1, leaseAt = -1;
            bool sent = false;
            ulong cycles = 0;
            for (long calls = 0; ; calls++)
            {
                cycles = RunUntilEdge(h, 30_000, 75, PicoWIdleTests.ProteusWatch);
                Assert.True(Status(h) == 0, Error(h));
                Assert.True(cycles < 3_000_000_000UL, "no CONN line: " + host.Cdc);
                var t = host.Cdc.ToString();
                if (!sent && t.Contains(">>> "))
                {
                    replAt = CycleToPs(h, cycles) * 1e-12;
                    var line = Encoding.ASCII.GetBytes(Script(joinSsid, joinPass));
                    fixed (byte* p = line) SerialWrite(h, 2, p, (nuint)line.Length);
                    sent = true;
                }
                if (leaseAt < 0 && host.Events.Exists(e => e.Event == ChipWifi.EvLease))
                    leaseAt = CycleToPs(h, host.Events.Find(e => e.Event == ChipWifi.EvLease).Cycle) * 1e-12;
                var conn = t.IndexOf("CONN ", StringComparison.Ordinal);
                if (conn >= 0 && t.IndexOf(">>> ", conn, StringComparison.Ordinal) >= 0) break;
            }
            return new Outcome(host.Cdc.ToString(), host.Events, WifiStatusOf(h), host.LeaseWallSeconds,
                host.Wall.Elapsed.TotalSeconds, leaseAt, replAt);
        }
        finally
        {
            Destroy(h);
            gc.Free();
        }
    }

    private static byte[] PicoWImage()
    {
        var path = Firmware.MicroPythonPicoW();
        Skip.If(path is null, "MicroPython RPI_PICO_W v1.28.0 UF2 not found (set RP2040_MICROPYTHON_PICOW_UF2)");
        return File.ReadAllBytes(path!);
    }

    private void Print(Outcome o)
    {
        output.WriteLine(o.Transcript.Replace("\r", "").Replace("\n", " | "));
        foreach (var (ev, cycle, s) in o.Events)
            output.WriteLine($"event {ev} at cycle {cycle}: state {s.State} flags {s.Flags} ssid '{Ssid(s)}' ip {Lease(s)} rssi {s.Rssi}");
        output.WriteLine($"REPL at {o.ReplSimSeconds:F3} s simulated; lease at {o.LeaseSimSeconds:F3} s simulated, " +
                         $"{o.LeaseWall:F2} s wall from create (whole session {o.TotalWall:F2} s wall)");
    }

    [SkippableFact]
    public void Wpa2_connect_gets_a_dhcp_lease_and_the_status_says_joined()
    {
        var o = Session(PicoWImage(), "Proteus", "proteus-pass", "Proteus", "proteus-pass");
        Print(o);
        Assert.Contains("SCAN [('Proteus', 6, -50)]", o.Transcript);
        Assert.Contains("CONN True 3 ('192.168.4.2', '255.255.255.0', '192.168.4.1', ", o.Transcript);
        var s = o.Final;
        Assert.Equal(ChipWifi.StateJoined, s.State);
        Assert.Equal(ChipWifi.FlagPowered | ChipWifi.FlagLeased, s.Flags);
        Assert.Equal("192.168.4.2", Ip(s.Ipv4));
        Assert.Equal("192.168.4.1", Ip(s.Gateway));
        Assert.Equal("255.255.255.0", Ip(s.Netmask));
        Assert.Equal("Proteus", Ssid(s));
        Assert.Equal(-50, s.Rssi);
        Assert.Equal(112u, s.Size);
        Assert.True(s.TxBytes > 0 && s.RxBytes > 0 && s.TxFrames > 0 && s.RxFrames > 0, "DHCP moved frames both ways");
        Assert.Equal([ChipWifi.EvScan, ChipWifi.EvJoin, ChipWifi.EvLease], o.Events.Select(e => e.Event));
        Assert.True(o.Events.Zip(o.Events.Skip(1)).All(p => p.First.Cycle <= p.Second.Cycle), "events in cycle order");
        Assert.Equal("192.168.4.2", Lease(o.Events[2].Status));
    }

    [SkippableFact]
    public void Wrong_passphrase_is_bad_auth()
    {
        var o = Session(PicoWImage(), "Proteus", "proteus-pass", "Proteus", "not-the-pass");
        Print(o);
        Assert.Contains("CONN False -3 ('0.0.0.0'", o.Transcript);            // STAT_WRONG_PASSWORD
        Assert.Equal(ChipWifi.StateBadAuth, o.Final.State);
        Assert.Equal(ChipWifi.FlagPowered, o.Final.Flags);
        Assert.Equal("Proteus", Ssid(o.Final));
        Assert.Contains(o.Events, e => e.Event == ChipWifi.EvBadAuth);
        Assert.DoesNotContain(o.Events, e => e.Event is ChipWifi.EvJoin or ChipWifi.EvLease);
    }

    [SkippableFact]
    public void Open_network_joins_without_a_passphrase()
    {
        var o = Session(PicoWImage(), "OpenLab", null, "OpenLab", null);
        Print(o);
        Assert.Contains("SCAN [('OpenLab', 6, -50)]", o.Transcript);
        Assert.Contains("CONN True 3 ('192.168.4.2'", o.Transcript);
        Assert.Equal(ChipWifi.StateJoined, o.Final.State);
        Assert.Equal("192.168.4.2", Lease(o.Final));
    }

    [SkippableFact]
    public void Unknown_ssid_is_no_network()
    {
        var o = Session(PicoWImage(), "Proteus", null, "Elsewhere", null);
        Print(o);
        Assert.Contains("CONN False -2 ('0.0.0.0'", o.Transcript);            // STAT_NO_AP_FOUND
        Assert.Equal(ChipWifi.StateNoNetwork, o.Final.State);
        Assert.Equal("Elsewhere", Ssid(o.Final));
    }

    // Arguments and chips without the radio, no firmware needed.
    [Fact]
    public void Arguments_are_checked_and_a_plain_pico_has_no_wifi()
    {
        var plain = CreateEx(0, BusAll);
        var w = CreateEx(0, BusAll | Cyw43);
        try
        {
            Assert.Equal(-1, WifiSetAp(plain, "Proteus", null));
            Assert.Contains("no CYW43439", Error(plain));
            var st = new WifiStatus { Size = (uint)sizeof(WifiStatus) };
            Assert.Equal(-1, GetStatus(plain, &st));
            Assert.Equal(-1, GetStatus(0, &st));
            Assert.Equal(-1, WifiSetAp(0, "Proteus", null));

            Assert.Equal(0, WifiSetAp(w, "Proteus", null));
            Assert.Equal(0, WifiSetAp(w, "Proteus", "12345678", 11, -70));
            Assert.Equal(0, WifiSetAp(w, "Proteus", new string('a', 63)));
            Assert.Equal(0, WifiSetAp(w, "Proteus", new string('F', 64)));
            Assert.Equal(0, WifiSetAp(w, null, null));                          // no AP
            Assert.Equal(0, WifiSetAp(w, "", ""));
            Assert.Equal(0, WifiSetAp(w, "Ñandú 📶", ""));                       // UTF-8, 12 bytes
            Assert.Equal(-1, WifiSetAp(w, new string('s', 33), null));
            Assert.Contains("longer than 32", Error(w));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", "short"));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", new string('a', 65)));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", new string('g', 64)));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", "pass\tword"));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", null, 15));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", null, -1));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", null, 6, 5));
            Assert.Equal(-1, WifiSetAp(w, "Proteus", null, 6, -121));
            var bad = new byte[] { 0xC3, 0x28, 0 };
            fixed (byte* p = bad) Assert.Equal(-1, SetAp(w, p, null, 0, 0));
            Assert.Contains("UTF-8", Error(w));

            Assert.Equal(-1, GetStatus(w, null));
            var small = new WifiStatus { Size = 4 };
            Assert.Equal(-1, GetStatus(w, &small));
            // A host that knows only the first 16 bytes gets 16.
            var head = new WifiStatus { Size = 16, Rssi = 77 };
            Assert.Equal(0, GetStatus(w, &head));
            Assert.Equal(16u, head.Size);
            Assert.Equal(0, head.Rssi);
            Assert.Equal(0u, head.Gateway[0]);                                    // past the caller's size: untouched
            var big = new WifiStatus { Size = 4096 };                                // bigger than ours: ours
            Assert.Equal(0, GetStatus(w, &big));
            Assert.Equal(112u, big.Size);
            Assert.Equal(ChipWifi.StateDown, big.State);
            Assert.Equal(0u, big.Flags);
            Assert.Equal("192.168.4.1", Ip(big.Gateway));
            Assert.Equal(0, Status(w));
            Assert.Equal(0, Status(plain));
            SetWifiCb(plain, null, 0);                                              // NULL is fine without a radio
            Assert.Equal(112, sizeof(WifiStatus));
        }
        finally { Destroy(plain); Destroy(w); }
    }
}

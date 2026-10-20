using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RP2040.Peripherals.Gpio;
using RP2040Sharp.Wireless.Cyw43;

namespace SiliconTwin.Native;

/// <summary>st_rp2040_wifi_info (silicontwin_rp2040.h): 160 bytes (112 before the port forwarding fields), the same layout on
/// x86 and x64. Grown only at the end; the caller's <c>size</c> says how much of it the caller knows (docs/wireless.md).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WifiStatus
{
    public uint Size;
    public int State;
    public uint Flags;
    public int Rssi;
    public fixed byte Ipv4[4];
    public fixed byte Gateway[4];
    public fixed byte Netmask[4];
    public fixed byte Mac[6];
    public fixed byte Reserved0[2];
    public fixed byte Ssid[33];
    public fixed byte Reserved1[3];
    public ulong TxBytes, RxBytes, TxFrames, RxFrames;
    public uint Changes, Reserved2;
    // ---- 112: port forwarding (W2) ----
    public uint TcpForwards, TcpOpen;
    public ulong TcpAccepted, TcpFailed, TcpToGuest, TcpFromGuest;
    public ushort TcpHostPort, TcpGuestPort, TcpPeerPort, Reserved3;
}

/// <summary>
/// The Pico W's radio as a C host sees it (ST_RP2040_CYW43): the CYW43439 on its pads, a virtual LAN behind it (a
/// gateway with DHCP on 192.168.4.0/24, <see cref="VirtualNet"/>) and the access point the host configures with
/// st_rp2040_wifi_set_ap. It follows what the station does (scan, join, wrong passphrase, unknown SSID, leave, DHCP
/// lease, WL_ON power) into a status a host can poll and events it delivers after each run, never from inside one.
/// Protocol level only: no RF. The host's network is reached only through the forwards the host asks for
/// (st_rp2040_wifi_forward_tcp): listeners on 127.0.0.1, relayed to the station's servers (docs/wireless.md).
/// </summary>
internal sealed unsafe class ChipWifi : IDisposable
{
    public const int StateDown = 0, StateScanning = 1, StateJoined = 2, StateBadAuth = 3, StateNoNetwork = 4;
    public const uint FlagPowered = 1, FlagLeased = 2;
    public const int EvScan = 1, EvJoin = 2, EvLease = 3, EvLeave = 4, EvBadAuth = 5, EvNoNetwork = 6;
    public const int EvTcpOpen = 7, EvTcpClose = 8, EvTcpRefused = 9;
    /// <summary>Simulated seconds per core-0 cycle for the forwarder's timers: the nominal clk_sys is close enough.</summary>
    private const double NominalHz = 125_000_000;
    /// <summary>Host time between two pumps of the forwards from inside the run calls.</summary>
    private static readonly long PumpInterval = Stopwatch.Frequency / 1000;
    public const int DefaultChannel = 6, DefaultRssi = -50;

    /// <summary>The virtual AP's BSSID: the gateway's MAC, the AP and the router being one box.</summary>
    private static readonly byte[] ApBssid = [0x02, 0x00, 0x5E, 0x00, 0x04, 0x01];

    // Events wait here for the end of the run that raised them. A guest scanning in a tight loop between two runs
    // cannot grow it: past the cap events are dropped (the change counter still counts them).
    private const int MaxPending = 64;

    public readonly Cyw43439Device Device;
    public readonly VirtualNet Net;
    private readonly Func<ulong> _cycles;
    private readonly Queue<(int Event, ulong Cycle, WifiStatus Status)> _pending = new();

    private int _state = StateDown;
    private string _ssid = "";
    private int _rssi;
    private byte[]? _lease;
    private bool _powered;
    private uint _changes;
    private TcpForwarder? _fwd;
    private TcpForwarder.ConnInfo _lastConn;
    private long _nextPump;

    public delegate* unmanaged[Cdecl]<nint, int, ulong, WifiStatus*, void> Cb;
    public nint User;

    public ChipWifi(IoBank0Peripheral io, Func<ulong> cycles)
    {
        _cycles = cycles;
        Device = new Cyw43439Device(io);
        Net = new VirtualNet(Device.Sdpcm, gatewayMac: ApBssid) { StationOnly = true };
        Device.Sdpcm.OnStaEvent += OnStation;
        Net.OnDhcpLeased += OnLease;
        var power = Device.Bus.OnPower;   // the device's own WL_ON handler stays first
        Device.Bus.OnPower = on => { power?.Invoke(on); OnPower(on); };
    }

    /// <summary>Replace the visible AP (null: none, scans come back empty and joins fail with NO_NETWORK). An
    /// association in progress keeps going; the next scan or join sees the new AP.</summary>
    public void SetAp(string? ssid, string? passphrase, int channel, int rssi)
    {
        var aps = Device.Sdpcm.VisibleAps;
        aps.Clear();
        if (ssid is not null)
            aps.Add(new Sdpcm.VirtualAp(ssid, ApBssid, channel, rssi, Secured: passphrase is not null, Passphrase: passphrase));
    }

    /// <summary>Validate st_rp2040_wifi_set_ap's arguments: null when they are fine, else the reason.</summary>
    public static string? ParseAp(byte* ssid, byte* passphrase, int channel, int rssi,
                                  out string? s, out string? p, out int ch, out int r)
    {
        s = p = null;
        ch = channel == 0 ? DefaultChannel : channel;
        r = rssi == 0 ? DefaultRssi : rssi;
        if (ssid != null && ssid[0] != 0)
        {
            int n = CStrLen(ssid, 33);
            if (n > 32) return "ssid is longer than 32 bytes";
            try { s = new UTF8Encoding(false, true).GetString(ssid, n); }
            catch (DecoderFallbackException) { return "ssid is not valid UTF-8"; }
        }
        if (passphrase != null && passphrase[0] != 0)
        {
            int n = CStrLen(passphrase, 65);
            if (n < 8 || n > 64) return "passphrase must be 8..63 characters, or 64 hex digits (WPA2-PSK), or NULL / \"\" for an open network";
            for (int i = 0; i < n; i++)
            {
                byte c = passphrase[i];
                bool ok = n == 64 ? Uri.IsHexDigit((char)c) : c >= 0x20 && c <= 0x7E;
                if (!ok) return n == 64 ? "a 64-character passphrase must be hex digits (a raw PSK)" : "passphrase must be printable ASCII";
            }
            p = Encoding.ASCII.GetString(passphrase, n);
        }
        if (ch < 1 || ch > 14) return "channel " + channel + " out of range 1..14 (0 = 6)";
        if (r < -120 || r > -1) return "rssi " + rssi + " out of range -120..-1 dBm (0 = -50)";
        return null;
    }

    private static int CStrLen(byte* s, int max)
    {
        int n = 0;
        while (n < max && s[n] != 0) n++;
        return n;
    }

    // ---- what the station did ----------------------------------------------------------------------------------

    private void OnStation(Sdpcm.StaEvent ev, string? ssid)
    {
        switch (ev)
        {
            case Sdpcm.StaEvent.Scan:
                if (_state != StateJoined) _state = StateScanning;
                Push(EvScan);
                break;
            case Sdpcm.StaEvent.Joined:
                _state = StateJoined;
                _ssid = ssid ?? "";
                _rssi = Device.Sdpcm.VisibleAps.Find(a => a.Ssid == ssid)?.Rssi ?? 0;
                _lease = null;
                Push(EvJoin);
                break;
            case Sdpcm.StaEvent.BadAuth:
            case Sdpcm.StaEvent.NoNetwork:
                _state = ev == Sdpcm.StaEvent.BadAuth ? StateBadAuth : StateNoNetwork;
                _ssid = ssid ?? "";
                _rssi = 0;
                _lease = null;
                Push(ev == Sdpcm.StaEvent.BadAuth ? EvBadAuth : EvNoNetwork);
                break;
            case Sdpcm.StaEvent.Left:
                Drop();
                break;
        }
    }

    private void OnLease(byte[] ip)
    {
        if (_state != StateJoined || (_lease is not null && _lease.AsSpan().SequenceEqual(ip))) return;   // a renewal
        _lease = ip.ToArray();
        Push(EvLease);
    }

    private void OnPower(bool on)
    {
        if (on == _powered) return;
        _powered = on;
        if (on) return;
        if (_state == StateJoined) Drop();   // WLAN.active(False) / deinit: the association goes with the chip
        else _state = StateDown;
    }

    private void Drop()
    {
        if (_state != StateJoined) return;
        _state = StateDown;
        _rssi = 0;
        _lease = null;
        Push(EvLeave);
    }

    private void Push(int ev)
    {
        _changes++;
        if (Cb == null || _pending.Count >= MaxPending) return;   // no listener: nothing is kept for a later one
        _pending.Enqueue((ev, _cycles(), Snapshot()));
    }

    /// <summary>The next event raised since the last call, oldest first.</summary>
    public bool TryTake(out int ev, out ulong cycle, out WifiStatus status)
    {
        if (_pending.Count == 0) { ev = 0; cycle = 0; status = default; return false; }
        (ev, cycle, status) = _pending.Dequeue();
        return true;
    }

    public void SetCallback(delegate* unmanaged[Cdecl]<nint, int, ulong, WifiStatus*, void> cb, nint user)
    {
        Cb = cb;
        User = user;
        if (cb == null) _pending.Clear();
    }

    // ---- port forwarding (W2) ----------------------------------------------------------------------------------

    /// <summary>st_rp2040_wifi_forward_tcp: listen on 127.0.0.1:<paramref name="hostPort"/> (0: a free port) for the
    /// station's <paramref name="guestPort"/>; guestPort 0 removes the forward of hostPort. Returns the port listened on
    /// (0 for a removal), or null with <paramref name="why"/>.</summary>
    public int? Forward(int hostPort, int guestPort, out string? why)
    {
        why = null;
        if (hostPort < 0 || hostPort > 65535) { why = "host_port " + hostPort + " out of range 0..65535"; return null; }
        if (guestPort < 0 || guestPort > 65535) { why = "guest_port " + guestPort + " out of range 0..65535 (0 removes the forward)"; return null; }
        if (guestPort == 0)
        {
            if (hostPort != 0 && _fwd is not null && _fwd.Unlisten((ushort)hostPort)) return 0;
            why = "127.0.0.1:" + hostPort + " is not forwarded";
            return null;
        }
        if (_fwd is null)
        {
            _fwd = Net.Forwarder;
            _fwd.Clock = () => _cycles() / NominalHz;
            _fwd.OnEvent = OnForward;
        }
        try { return _fwd.Listen((ushort)hostPort, (ushort)guestPort); }
        catch (InvalidOperationException e) { why = e.Message; return null; }
    }

    private void OnForward(TcpForwarder.Event ev, TcpForwarder.ConnInfo info)
    {
        _lastConn = info;
        Push(ev switch { TcpForwarder.Event.Open => EvTcpOpen, TcpForwarder.Event.Close => EvTcpClose, _ => EvTcpRefused });
    }

    /// <summary>st_rp2040_wifi_poll: move what the host sockets delivered into the guest now. Returns the open connections.</summary>
    public int Poll()
    {
        var f = _fwd;
        if (f is null) return 0;
        f.Pump();
        _nextPump = Stopwatch.GetTimestamp() + PumpInterval;
        return f.OpenConnections;
    }

    /// <summary>From the run calls: pump the forwards at most once per <see cref="PumpInterval"/> of host time, and not at
    /// all on a chip that forwards nothing (one null check on the idle fast path).</summary>
    public void PumpIfDue()
    {
        var f = _fwd;
        if (f is null || (f.Forwards == 0 && !f.Busy)) return;
        var t = Stopwatch.GetTimestamp();
        if (t < _nextPump) return;
        _nextPump = t + PumpInterval;
        f.Pump();
    }

    /// <summary>Close the listeners and reset the connections (st_rp2040_destroy): the host ports are free again.</summary>
    public void Dispose() => _fwd?.Dispose();

    // ---- status ------------------------------------------------------------------------------------------------

    public WifiStatus Snapshot()
    {
        var s = new WifiStatus
        {
            Size = (uint)sizeof(WifiStatus),
            State = _state,
            Flags = (_powered ? FlagPowered : 0) | (_lease is not null ? FlagLeased : 0),
            Rssi = _rssi,
            Changes = _changes,
        };
        var sd = Device.Sdpcm;
        s.TxBytes = (ulong)sd.TxBytes;
        s.RxBytes = (ulong)sd.RxBytes;
        s.TxFrames = (ulong)sd.TxFrames;
        s.RxFrames = (ulong)sd.RxFrames;
        for (int i = 0; i < 4; i++)
        {
            s.Ipv4[i] = _lease is null ? (byte)0 : _lease[i];
            s.Gateway[i] = Net.GatewayIp[i];
            s.Netmask[i] = Net.SubnetMask[i];
        }
        var mac = sd.MacAddress;
        for (int i = 0; i < 6 && i < mac.Length; i++) s.Mac[i] = mac[i];
        var ssid = Encoding.UTF8.GetBytes(_ssid);
        for (int i = 0; i < ssid.Length && i < 32; i++) s.Ssid[i] = ssid[i];
        if (_fwd is { } f)
        {
            s.TcpForwards = (uint)f.Forwards;
            s.TcpOpen = (uint)f.OpenConnections;
            s.TcpAccepted = (ulong)f.Accepted;
            s.TcpFailed = (ulong)f.Failed;
            s.TcpToGuest = (ulong)f.ToGuestBytes;
            s.TcpFromGuest = (ulong)f.FromGuestBytes;
            s.TcpHostPort = _lastConn.HostPort;
            s.TcpGuestPort = _lastConn.GuestPort;
            s.TcpPeerPort = _lastConn.PeerPort;
        }
        return s;
    }

    /// <summary>Copy the status into the caller's struct, as much of it as the caller's <c>size</c> says it knows;
    /// <c>size</c> comes back as the bytes written.</summary>
    public void Fill(WifiStatus* dst, uint size)
    {
        var s = Snapshot();
        uint n = Math.Min(size, (uint)sizeof(WifiStatus));
        s.Size = n;
        Buffer.MemoryCopy(&s, dst, n, n);
    }
}

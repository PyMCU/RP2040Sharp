// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using System.Collections.Generic;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// A minimal virtual network behind the emulated radio: an L2/L3 gateway that answers ARP and serves
/// DHCP so a CYW43439 STA that associates actually gets an IP (link status UP), the prerequisite for
/// real sockets / Microdot. Ethernet frames the guest transmits arrive via <see cref="Sdpcm.OnHostEthernet"/>;
/// replies go back through <see cref="Sdpcm.EnqueueEthernet"/>. This is the "virtualise behind the
/// register boundary" layer — the guest's TCP/IP stack runs unmodified; only the wire is synthetic.
/// </summary>
public sealed class VirtualNet
{
    private readonly Sdpcm _sdpcm;

    public byte[] GatewayMac { get; }
    public byte[] GatewayIp { get; }
    public byte[] ClientIp { get; }
    public byte[] SubnetMask { get; } = [255, 255, 255, 0];
    public uint LeaseSeconds { get; set; } = 86400;

    /// <summary>Diagnostics: a frame the guest transmitted (itf, ethertype, length).</summary>
    public Action<int, ushort, int>? OnGuestFrame;
    /// <summary>Diagnostics: the DHCP lease has been issued (the client IP).</summary>
    public Action<byte[]>? OnDhcpLeased;

    // ── Minimal active-open TCP client (to exercise a server running on the guest, e.g. Microdot) ──
    private int _tcpItf;
    private ushort _tcpClientPort, _tcpServerPort;
    private uint _tcpSeq, _tcpAck;       // our next seq to send; next byte we expect from the server
    private byte[]? _tcpRequest;         // payload to send once the connection is established
    private bool _tcpEstablished, _tcpRequestSent, _tcpClosed;
    private readonly List<byte> _tcpRx = new();
    private const byte TCP_FIN = 0x01, TCP_SYN = 0x02, TCP_RST = 0x04, TCP_PSH = 0x08, TCP_ACK = 0x10;

    /// <summary>The bytes the guest server sent back on the TCP connection (e.g. the HTTP response).</summary>
    public byte[] TcpResponse => _tcpRx.ToArray();
    /// <summary>Fired when the connection closes (guest FIN), with the full received payload.</summary>
    public Action<byte[]>? OnTcpClosed;
    /// <summary>Diagnostics: a TCP segment arrived from the guest (flags, seq, ack, payloadLen).</summary>
    public Action<byte, uint, uint, int>? OnTcpSegment;

    /// <summary>Open a TCP connection from the virtual gateway to a server on the guest (the leased
    /// client IP) and send <paramref name="request"/> once established. Drives a minimal active-open
    /// handshake; the response accumulates in <see cref="TcpResponse"/>.</summary>
    public void TcpConnect(int itf, ushort serverPort, byte[] request, ushort clientPort = 50000)
    {
        _tcpItf = itf; _tcpServerPort = serverPort; _tcpClientPort = clientPort;
        _tcpRequest = request; _tcpSeq = 1000; _tcpAck = 0;
        _tcpEstablished = _tcpRequestSent = _tcpClosed = false;
        _tcpRx.Clear();
        SendTcp(TCP_SYN, []);              // SYN occupies seq 1000; advanced to 1001 on SYN-ACK
    }

    /// <summary>Re-send the SYN while the connection is still opening. Call periodically: it covers
    /// the race where the SYN reaches the guest before its server has returned to <c>accept()</c>
    /// (the segment is dropped and, without this, never retried).</summary>
    public void TcpPoke()
    {
        if (_tcpRequest != null && !_tcpEstablished && !_tcpClosed)
            SendTcp(TCP_SYN, []);
    }

    // Connected stations. The first device keeps the legacy ClientIp; further devices get sequential
    // leases. Frames between stations are L2-switched; broadcasts also reach the gateway (ARP/DHCP).
    private readonly List<Sdpcm> _devices = new();
    private readonly Dictionary<string, byte[]> _leases = new();   // device MAC → leased IP
    private readonly Dictionary<string, Sdpcm> _macToDevice = new(); // learned MAC → owning station
    private int _nextLease = 2;

    public VirtualNet(Sdpcm sdpcm, byte[]? gatewayIp = null, byte[]? clientIp = null, byte[]? gatewayMac = null)
    {
        _sdpcm = sdpcm;
        GatewayIp = gatewayIp ?? [192, 168, 4, 1];
        ClientIp = clientIp ?? [192, 168, 4, 2];
        GatewayMac = gatewayMac ?? [0x02, 0x00, 0x5E, 0x00, 0x04, 0x01];
        AddDevice(sdpcm);
    }

    /// <summary>Attach another station (a second Pico 2 W) to the same virtual LAN. It gets its own
    /// DHCP lease and can exchange frames with the others through the L2 switch.</summary>
    public void AddDevice(Sdpcm sdpcm)
    {
        _devices.Add(sdpcm);
        sdpcm.OnHostEthernet += (itf, frame) => HandleGuestFrame(sdpcm, itf, frame);
    }

    private static ushort Be16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
    private static string MacKey(byte[] b, int o) => $"{b[o]:x2}{b[o + 1]:x2}{b[o + 2]:x2}{b[o + 3]:x2}{b[o + 4]:x2}{b[o + 5]:x2}";
    private static bool IsBroadcastOrMulticast(byte[] dst) => (dst[0] & 1) != 0; // multicast/broadcast bit

    private void HandleGuestFrame(Sdpcm from, int itf, byte[] frame)
    {
        if (frame.Length < 14) return;
        _macToDevice[MacKey(frame, 6)] = from;   // learn source MAC → station
        _serverMac ??= frame[6..12];             // first learned MAC = single-device TCP client target
        var ethertype = Be16(frame, 12);
        OnGuestFrame?.Invoke(itf, ethertype, frame.Length);

        // L2 switch: deliver unicast frames addressed to another station directly.
        var dstKey = MacKey(frame, 0);
        if (!IsBroadcastOrMulticast(frame[0..6]) && MacKey(GatewayMac, 0) != dstKey)
        {
            if (_macToDevice.TryGetValue(dstKey, out var dest) && dest != from)
                dest.EnqueueEthernet(itf, frame);
            return; // unicast to a peer (or unknown) — gateway doesn't process it
        }
        // Broadcast/multicast: flood to the other stations, and let the gateway answer ARP/DHCP.
        if (IsBroadcastOrMulticast(frame[0..6]))
            foreach (var d in _devices) if (d != from) d.EnqueueEthernet(itf, frame);

        switch (ethertype)
        {
            case 0x0806: HandleArp(itf, frame); break;
            case 0x0800: HandleIpv4(itf, frame); break;
        }
    }

    // ── ARP ──────────────────────────────────────────────────────────────
    private void HandleArp(int itf, byte[] f)
    {
        // ARP packet at offset 14: htype(2) ptype(2) hlen(1) plen(1) op(2) sha(6) spa(4) tha(6) tpa(4)
        const int a = 14;
        if (f.Length < a + 28) return;
        var op = Be16(f, a + 6);
        if (op != 1) return;                       // only answer requests
        var targetIp = f[(a + 24)..(a + 28)];
        if (!IpEquals(targetIp, GatewayIp)) return; // we only own the gateway IP
        var senderMac = f[(a + 8)..(a + 14)];
        var senderIp = f[(a + 14)..(a + 18)];

        var reply = new byte[14 + 28];
        WriteEthHeader(reply, senderMac, GatewayMac, 0x0806);
        Array.Copy(f, a, reply, 14, 6);            // htype/ptype/hlen/plen copied from request
        reply[14 + 6] = 0; reply[14 + 7] = 2;      // op = reply
        Array.Copy(GatewayMac, 0, reply, 14 + 8, 6);
        Array.Copy(GatewayIp, 0, reply, 14 + 14, 4);
        Array.Copy(senderMac, 0, reply, 14 + 18, 6);
        Array.Copy(senderIp, 0, reply, 14 + 24, 4);
        _macToDevice.TryGetValue(MacKey(senderMac, 0), out var dev);
        (dev ?? _sdpcm).EnqueueEthernet(itf, reply);
    }

    // ── IPv4 / UDP / DHCP ──────────────────────────────────────────────────
    private void HandleIpv4(int itf, byte[] f)
    {
        const int ip = 14;
        if (f.Length < ip + 20) return;
        var ihl = (f[ip] & 0x0f) * 4;
        var proto = f[ip + 9];
        var l4 = ip + ihl;
        if (proto == 17)                           // UDP
        {
            if (f.Length < l4 + 8) return;
            if (Be16(f, l4 + 2) == 67) HandleDhcp(itf, f, l4 + 8); // DHCP server port
        }
        else if (proto == 6)                       // TCP
        {
            HandleTcp(itf, f, ip, l4);
        }
    }

    private void HandleDhcp(int itf, byte[] f, int d)
    {
        // DHCP: op(1) htype(1) hlen(1) hops(1) xid(4) secs(2) flags(2) ciaddr(4) yiaddr(4) siaddr(4)
        // giaddr(4) chaddr(16) sname(64) file(128) magic(4) options...
        if (f.Length < d + 240) return;
        var xid = f[(d + 4)..(d + 8)];
        var clientMac = f[(d + 28)..(d + 34)];     // chaddr (first 6 bytes)

        // find option 53 (DHCP message type)
        var msgType = 0;
        for (var o = d + 240; o + 1 < f.Length && f[o] != 255;)
        {
            if (f[o] == 0) { o++; continue; }      // pad
            var len = f[o + 1];
            if (f[o] == 53 && len >= 1) msgType = f[o + 2];
            o += 2 + len;
        }
        // DISCOVER(1) → OFFER(2); REQUEST(3) → ACK(5)
        var replyType = msgType switch { 1 => 2, 3 => 5, _ => 0 };
        if (replyType == 0) return;

        var lease = LeaseFor(clientMac);
        var dhcp = BuildDhcpReply(xid, clientMac, (byte)replyType, lease);
        var udp = BuildUdp(67, 68, dhcp);
        var ipPkt = BuildIpv4(17, GatewayIp, lease, udp);
        var frame = new byte[14 + ipPkt.Length];
        WriteEthHeader(frame, clientMac, GatewayMac, 0x0800);
        Array.Copy(ipPkt, 0, frame, 14, ipPkt.Length);
        _macToDevice.TryGetValue(MacKey(clientMac, 0), out var dev);
        (dev ?? _sdpcm).EnqueueEthernet(itf, frame);

        if (replyType == 5) OnDhcpLeased?.Invoke(lease);
    }

    /// <summary>Stable per-MAC lease: the first station keeps <see cref="ClientIp"/>, others get
    /// sequential addresses in the same /24.</summary>
    private byte[] LeaseFor(byte[] mac)
    {
        var key = MacKey(mac, 0);
        if (_leases.TryGetValue(key, out var ip)) return ip;
        byte[] lease = _leases.Count == 0
            ? ClientIp
            : [GatewayIp[0], GatewayIp[1], GatewayIp[2], (byte)_nextLease++];
        if (_leases.Count == 0) _nextLease = ClientIp[3] + 1;
        _leases[key] = lease;
        return lease;
    }

    // ── TCP client state machine ───────────────────────────────────────────
    private void HandleTcp(int itf, byte[] f, int ipOff, int tcpOff)
    {
        if (f.Length < tcpOff + 20) return;
        var srcPort = Be16(f, tcpOff);
        var dstPort = Be16(f, tcpOff + 2);
        if (dstPort != _tcpClientPort || srcPort != _tcpServerPort) return; // not our connection
        var seq = Be32(f, tcpOff + 4);
        var ackNo = Be32(f, tcpOff + 8);
        var flags = f[tcpOff + 13];
        var dataOff = (f[tcpOff + 12] >> 4) * 4;
        var ipTotal = Be16(f, ipOff + 2);
        var payloadOff = tcpOff + dataOff;
        var payloadLen = ipOff + ipTotal - payloadOff;
        OnTcpSegment?.Invoke(flags, seq, ackNo, payloadLen);

        // A RST before the connection is established just means the server isn't listening yet (we
        // raced its bind/listen). Ignore it and keep re-sending the SYN (TcpPoke); only treat a RST on
        // an established connection as a real close.
        if ((flags & TCP_RST) != 0) { if (_tcpEstablished) _tcpClosed = true; return; }

        if ((flags & TCP_SYN) != 0 && (flags & TCP_ACK) != 0 && !_tcpEstablished)
        {
            // SYN-ACK: acknowledge the server's ISN, then push the request. Our SYN occupied seq 1000,
            // so our data starts at 1001.
            _tcpAck = seq + 1;
            _tcpEstablished = true;
            _tcpSeq = 1001;
            SendTcp(TCP_ACK, []);
            if (_tcpRequest is { Length: > 0 } req)
            {
                SendTcp(TCP_PSH | TCP_ACK, req);
                _tcpSeq += (uint)req.Length;
                _tcpRequestSent = true;
            }
            return;
        }

        if (payloadLen > 0)
        {
            // In-order data: buffer it and acknowledge.
            if (seq == _tcpAck)
            {
                for (var i = 0; i < payloadLen; i++) _tcpRx.Add(f[payloadOff + i]);
                _tcpAck = seq + (uint)payloadLen;
            }
            SendTcp(TCP_ACK, []);
        }

        if ((flags & TCP_FIN) != 0)
        {
            _tcpAck = seq + (uint)payloadLen + 1; // FIN consumes one sequence number
            SendTcp(TCP_ACK | TCP_FIN, []);
            _tcpSeq += 1;
            if (!_tcpClosed) { _tcpClosed = true; OnTcpClosed?.Invoke(TcpResponse); }
        }
    }

    private void SendTcp(byte flags, byte[] payload)
    {
        var tcp = new byte[20 + payload.Length];
        tcp[0] = (byte)(_tcpClientPort >> 8); tcp[1] = (byte)_tcpClientPort;
        tcp[2] = (byte)(_tcpServerPort >> 8); tcp[3] = (byte)_tcpServerPort;
        WriteBe32(tcp, 4, _tcpSeq);
        WriteBe32(tcp, 8, _tcpAck);
        tcp[12] = 5 << 4;          // data offset = 5 words (no options)
        tcp[13] = flags;
        tcp[14] = 0x20; tcp[15] = 0x00;  // window = 8192
        Array.Copy(payload, 0, tcp, 20, payload.Length);
        // checksum over the TCP pseudo-header + segment (client=gateway IP → server=client/leased IP)
        var csum = TcpChecksum(GatewayIp, ClientIp, tcp);
        tcp[16] = (byte)(csum >> 8); tcp[17] = (byte)csum;

        var ipPkt = BuildIpv4(6, GatewayIp, ClientIp, tcp);
        var frame = new byte[14 + ipPkt.Length];
        WriteEthHeader(frame, _serverMac ?? new byte[6], GatewayMac, 0x0800);
        Array.Copy(ipPkt, 0, frame, 14, ipPkt.Length);
        _sdpcm.EnqueueEthernet(_tcpItf, frame);
    }

    private byte[]? _serverMac;  // learned guest MAC (from any guest frame) for unicast delivery

    private static ushort TcpChecksum(byte[] srcIp, byte[] dstIp, byte[] tcp)
    {
        uint sum = 0;
        sum += (uint)(srcIp[0] << 8 | srcIp[1]); sum += (uint)(srcIp[2] << 8 | srcIp[3]);
        sum += (uint)(dstIp[0] << 8 | dstIp[1]); sum += (uint)(dstIp[2] << 8 | dstIp[3]);
        sum += 6;                          // protocol
        sum += (uint)tcp.Length;           // TCP length
        for (var i = 0; i + 1 < tcp.Length; i += 2) sum += (uint)(tcp[i] << 8 | tcp[i + 1]);
        if ((tcp.Length & 1) != 0) sum += (uint)(tcp[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static uint Be32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static void WriteBe32(byte[] b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private byte[] BuildDhcpReply(byte[] xid, byte[] clientMac, byte type, byte[] yourIp)
    {
        var opts = new List<byte>
        {
            53, 1, type,                                       // message type
            54, 4, GatewayIp[0], GatewayIp[1], GatewayIp[2], GatewayIp[3],   // server id
            51, 4, (byte)(LeaseSeconds >> 24), (byte)(LeaseSeconds >> 16), (byte)(LeaseSeconds >> 8), (byte)LeaseSeconds, // lease
            1, 4, SubnetMask[0], SubnetMask[1], SubnetMask[2], SubnetMask[3], // subnet mask
            3, 4, GatewayIp[0], GatewayIp[1], GatewayIp[2], GatewayIp[3],     // router
            6, 4, GatewayIp[0], GatewayIp[1], GatewayIp[2], GatewayIp[3],     // dns
            255,                                                              // end
        };
        var msg = new byte[240 + opts.Count];
        msg[0] = 2; msg[1] = 1; msg[2] = 6; msg[3] = 0;        // op=reply, htype=eth, hlen=6
        Array.Copy(xid, 0, msg, 4, 4);
        Array.Copy(yourIp, 0, msg, 16, 4);                     // yiaddr
        Array.Copy(GatewayIp, 0, msg, 20, 4);                  // siaddr
        Array.Copy(clientMac, 0, msg, 28, 6);                  // chaddr
        msg[236] = 0x63; msg[237] = 0x82; msg[238] = 0x53; msg[239] = 0x63; // magic cookie
        opts.CopyTo(msg, 240);
        return msg;
    }

    private static byte[] BuildUdp(ushort srcPort, ushort dstPort, byte[] payload)
    {
        var u = new byte[8 + payload.Length];
        u[0] = (byte)(srcPort >> 8); u[1] = (byte)srcPort;
        u[2] = (byte)(dstPort >> 8); u[3] = (byte)dstPort;
        var len = (ushort)u.Length;
        u[4] = (byte)(len >> 8); u[5] = (byte)len;
        // checksum 0 = not computed (valid for IPv4 UDP)
        Array.Copy(payload, 0, u, 8, payload.Length);
        return u;
    }

    private static byte[] BuildIpv4(byte proto, byte[] src, byte[] dst, byte[] payload)
    {
        var ip = new byte[20 + payload.Length];
        ip[0] = 0x45;                                   // version 4, IHL 5
        var total = (ushort)ip.Length;
        ip[2] = (byte)(total >> 8); ip[3] = (byte)total;
        ip[8] = 64;                                     // TTL
        ip[9] = proto;
        Array.Copy(src, 0, ip, 12, 4);
        Array.Copy(dst, 0, ip, 16, 4);
        // header checksum
        uint sum = 0;
        for (var i = 0; i < 20; i += 2) sum += (uint)(ip[i] << 8 | ip[i + 1]);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        var csum = (ushort)~sum;
        ip[10] = (byte)(csum >> 8); ip[11] = (byte)csum;
        Array.Copy(payload, 0, ip, 20, payload.Length);
        return ip;
    }

    private static void WriteEthHeader(byte[] frame, byte[] dst, byte[] src, ushort ethertype)
    {
        Array.Copy(dst, 0, frame, 0, 6);
        Array.Copy(src, 0, frame, 6, 6);
        frame[12] = (byte)(ethertype >> 8); frame[13] = (byte)ethertype;
    }

    private static bool IpEquals(byte[] a, byte[] b) =>
        a.Length == 4 && b.Length == 4 && a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
}

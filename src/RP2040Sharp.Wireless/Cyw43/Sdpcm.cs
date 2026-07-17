// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using System.Collections.Generic;
using System.Text;
using RP2040.Core;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// SDPCM (Synchronous Data Packet Control Mechanism) transport for the CYW43439's WLAN function
/// (gSPI F2). This is the chip side of the protocol the cyw43-driver speaks once the firmware is
/// "running": the host wraps every WLC ioctl/iovar and every Ethernet frame in an SDPCM header and
/// pushes it over F2; the chip answers ioctls with a matching SDPCM control packet and pushes async
/// WLC events and inbound Ethernet frames the same way. Modelled at the packet boundary so it stays
/// independent of the firmware build — we synthesise plausible responses rather than execute firmware.
///
/// <para>Header layout (little-endian, from cyw43_ll.c):
/// <list type="bullet">
/// <item>sdpcm_header (12B): size, ~size, seq, channel, next_len, header_len, flow_ctrl, bus_credit, rsv[2]</item>
/// <item>ioctl_header (16B): cmd, len(low16=out len), flags(bits31:16=id, low=kind|iface&lt;&lt;12), status</item>
/// <item>bdc_header (4B): flags, priority, flags2(=itf), data_offset — for DATA/ASYNCEVENT channels</item>
/// </list></para>
/// </summary>
public sealed class Sdpcm
{
    public const int SdpcmHeaderLen = 12;
    public const int IoctlHeaderLen = 16;
    public const int BdcHeaderLen = 4;

    private const int ControlHeader = 0;
    private const int AsyncEventHeader = 1;
    private const int DataHeader = 2;

    // WLC ioctl ids (subset the bring-up + STA/AP paths touch).
    private const uint WLC_SET_SSID = 26;
    private const uint WLC_GET_VAR = 262;
    private const uint WLC_SET_VAR = 263;

    // Async WLC event types / statuses (cyw43_ll.h + cyw43_ctrl.c state machine).
    private const uint EV_SET_SSID = 0;
    private const uint EV_AUTH = 3;
    private const uint EV_LINK = 16;
    private const uint EV_ESCAN_RESULT = 69;
    private const uint STATUS_SUCCESS = 0;
    private const uint STATUS_NO_NETWORKS = 3; // EV_SET_SSID status: no matching SSID
    private const uint STATUS_PARTIAL = 8;

    /// <summary>Diagnostics/notification: the STA joined a network (matched SSID) or failed (null).</summary>
    public Action<string?>? OnStaJoin;

    /// <summary>A Wi-Fi network the emulated chip "sees" on the air. Populated by the virtual radio
    /// (Fase 5/6) so <c>scan()</c> returns real BSSes; an empty list yields an empty scan.</summary>
    public sealed record VirtualAp(string Ssid, byte[] Bssid, int Channel, int Rssi, bool Secured);

    /// <summary>Networks visible to this chip; the escan ioctl reports each as an ESCAN_RESULT event.</summary>
    public readonly List<VirtualAp> VisibleAps = new();

    /// <summary>Chip MAC (locally-administered). Per-device so multiple Pico 2 W instances differ.</summary>
    public byte[] MacAddress { get; set; } = [0x02, 0x12, 0x34, 0x00, 0x00, 0x01];

    /// <summary>Pending chip→host SDPCM packets, oldest first. The host drains these over F2 reads.</summary>
    private readonly Queue<byte[]> _txq = new();

    private byte _chipSeq;       // chip→host SDPCM sequence (informational; host matches by ioctl id)
    private byte _lastHostSeq;   // sequence of the most recent host packet (control or data)
    // Credit window granted ahead of the host's current sequence. The host stalls when its TX
    // sequence reaches the granted credit; granting seq+window keeps it flowing. The host only
    // accepts a credit advance of ≤20 per packet (cyw43_ll.c), so the window stays small.
    private const int CreditWindow = 4;
    private byte GrantedCredit => (byte)(_lastHostSeq + CreditWindow);

    /// <summary>Raised when a packet is queued for the host (drives the F2-packet-available IRQ);
    /// the arg is the byte length the host should read. Lowered when the queue drains.</summary>
    public Action<bool, int>? OnPacketReadyChanged;

    /// <summary>Diagnostics: (cmd, kind, varName, inLen) of each ioctl the host issued.</summary>
    public Action<uint, uint, string, int>? OnIoctl;

    /// <summary>Diagnostics: outbound Ethernet frame from the host (itf, payload).</summary>
    public Action<int, byte[]>? OnHostEthernet;

    public bool HasPacket => _txq.Count > 0;
    public int NextPacketLen => _txq.Count > 0 ? _txq.Peek().Length : 0;

    private static ushort Get16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    private static uint Get32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    private static void Put16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void Put32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static int Align4(int n) => (n + 3) & ~3;

    /// <summary>Host wrote an SDPCM packet over F2. Parse the channel and, for control packets,
    /// synthesise the ioctl response the host's <c>cyw43_do_ioctl</c> loop is waiting for.</summary>
    public void HostWrite(byte[] pkt)
    {
        if (pkt.Length < SdpcmHeaderLen) return;
        var size = Get16(pkt, 0);
        if (size < SdpcmHeaderLen || size > pkt.Length) return;
        var headerLen = pkt[7];
        var channel = pkt[5] & 0x0f;
        _lastHostSeq = pkt[4];  // track host TX sequence so we grant credit ahead of it

        var before = _txq.Count;
        switch (channel)
        {
            case ControlHeader:
                if (size < headerLen + IoctlHeaderLen) return;
                HandleIoctl(pkt, headerLen);
                break;
            case DataHeader:
                HandleEthernetOut(pkt, headerLen, size);
                break;
        }
        // The host advances its TX credit only from bus_data_credit in packets it receives. A host
        // packet we don't answer (e.g. an IPv6 frame we ignore) would otherwise never refresh the
        // credit and the host eventually stalls. Emit a header-only flow-control packet to top it up
        // — the driver updates the credit then discards it (sdpcm_process_rx_packet returns -4).
        if (_txq.Count == before) EnqueueFlowControl();
    }

    private void EnqueueFlowControl()
    {
        var buf = new byte[Align4(SdpcmHeaderLen)];
        Put16(buf, 0, SdpcmHeaderLen);
        Put16(buf, 2, ~SdpcmHeaderLen & 0xffff);
        buf[4] = _chipSeq++;
        buf[5] = ControlHeader;      // channel <3 so the host applies the credit update
        buf[7] = SdpcmHeaderLen;
        buf[9] = GrantedCredit;
        Enqueue(buf);
    }

    private void HandleIoctl(byte[] pkt, int headerLen)
    {
        var cmd   = Get32(pkt, headerLen + 0);
        var outLen = Get16(pkt, headerLen + 4);     // host's requested output length
        var flags = Get32(pkt, headerLen + 8);
        var kind  = flags & 0xf;                     // SDPCM_GET=0, SDPCM_SET=2
        var payloadOff = headerLen + IoctlHeaderLen;
        var inLen = Math.Max(0, Get16(pkt, 0) - payloadOff);
        var payload = inLen > 0 ? pkt[payloadOff..(payloadOff + inLen)] : [];

        var varName = "";
        if ((cmd == WLC_GET_VAR || cmd == WLC_SET_VAR) && payload.Length > 0)
        {
            var z = Array.IndexOf(payload, (byte)0);
            varName = Encoding.ASCII.GetString(payload, 0, z < 0 ? payload.Length : z);
        }
        OnIoctl?.Invoke(cmd, kind, varName, inLen);

        // Build the response payload. The host copies min(requested, returned) bytes back, so for
        // GETs we return `outLen` bytes (mostly zero) with known variables filled in; SETs need only
        // status=0 with an empty payload.
        var resp = BuildIoctlPayload(cmd, kind, varName, outLen);
        Enqueue(BuildControlPacket(cmd, flags, resp));

        // A scan request (escan iovar) is acked above; the results then arrive asynchronously as a
        // stream of ESCAN_RESULT events (status=PARTIAL per BSS) terminated by a SUCCESS event.
        if (cmd == WLC_SET_VAR && varName == "escan")
        {
            foreach (var ap in VisibleAps)
                EnqueueAsyncEvent(BuildEscanResultEvent(ap, STATUS_PARTIAL));
            EnqueueAsyncEvent(BuildEscanResultEvent(null, STATUS_SUCCESS));
        }
        // STA join: connect() ends with WLC_SET_SSID (or the "join" iovar) carrying le32(ssid_len)+ssid.
        else if (cmd == WLC_SET_SSID)
            HandleJoin(payload, 0);
        else if (cmd == WLC_SET_VAR && varName == "join")
            HandleJoin(payload, varName.Length + 1);
        // SoftAP SSID: cyw43_ll_wifi_ap_init sends iovar "bsscfg:ssid" = u32(ap iface) u32(ssid_len) ssid[32].
        // Capture it so the virtual air can advertise this guest's own AP to other stations' scans.
        else if (cmd == WLC_SET_VAR && varName == "bsscfg:ssid")
        {
            var v = varName.Length + 1;
            if (payload.Length >= v + 8)
            {
                var slen = (int)Get32(payload, v + 4);
                if (slen is >= 0 and <= 32 && payload.Length >= v + 8 + slen)
                    ApSsid = Encoding.UTF8.GetString(payload, v + 8, slen);
            }
        }
        // AP bring-up: cyw43_ll_wifi_ap_set_up sends iovar "bss" = u32(bsscfg idx) u32(up). When the
        // AP bsscfg (idx 1) is set up, the AP interface link must come up so lwIP runs the AP netif
        // (and its DHCP server). Emit EV_LINK(up, interface=AP).
        else if (cmd == WLC_SET_VAR && varName == "bss" && payload.Length >= 12)
        {
            var idx = Get32(payload, 4);
            var up = Get32(payload, 8);
            if (idx == 1)
            {
                EnqueueAsyncEvent(BuildBcmEvent(EV_LINK, STATUS_SUCCESS, (ushort)(up != 0 ? 1 : 0), 0, 1));
                OnApUp?.Invoke(up != 0);
            }
        }
        // Any other iovar SET is accepted (ACKed above) but its effect is not modelled — usually benign
        // radio/config (ampdu, mfp, country, …), but recorded so the unmodelled surface stays visible.
        else if (cmd == WLC_SET_VAR && varName.Length > 0)
            EmuStrict.Note("cyw43.setvar.ignored", varName);
    }

    /// <summary>Diagnostics/notification: the AP interface went up (true) or down (false).</summary>
    public Action<bool>? OnApUp;

    /// <summary>The SSID this chip's guest configured for its own SoftAP (from the "bsscfg:ssid" iovar),
    /// or null if the guest never brought up an AP. Used by the virtual air to advertise the guest-AP.</summary>
    public string? ApSsid { get; private set; }

    /// <summary>Drive the STA association state machine for a join request. For a visible open network
    /// the chip reports the success chain the host's join state machine needs to reach JOIN_STATE_ALL:
    /// SET_SSID(0) → AUTH(0) → LINK(up). An unknown SSID reports SET_SSID(status=NO_NETWORKS).</summary>
    private void HandleJoin(byte[] payload, int ssidFieldOffset)
    {
        if (payload.Length < ssidFieldOffset + 4) { EmitJoinFail(); return; }
        var ssidLen = (int)Math.Min(32u, Get32(payload, ssidFieldOffset));
        if (payload.Length < ssidFieldOffset + 4 + ssidLen) { EmitJoinFail(); return; }
        var ssid = Encoding.UTF8.GetString(payload, ssidFieldOffset + 4, ssidLen);

        var ap = VisibleAps.Find(a => a.Ssid == ssid);
        if (ap == null) { EmitJoinFail(ssid); return; }

        // Open-network association chain. (WPA/PSK would add an EV_PSK_SUP(WLC_SUP_KEYED) step.)
        EnqueueAsyncEvent(BuildBcmEvent(EV_SET_SSID, STATUS_SUCCESS, 0, 0, 0));
        EnqueueAsyncEvent(BuildBcmEvent(EV_AUTH, STATUS_SUCCESS, 0, 0, 0));
        EnqueueAsyncEvent(BuildBcmEvent(EV_LINK, STATUS_SUCCESS, 1, 0, 0)); // flags&1 = link up
        OnStaJoin?.Invoke(ssid);
    }

    private void EmitJoinFail(string? ssid = null)
    {
        EnqueueAsyncEvent(BuildBcmEvent(EV_SET_SSID, STATUS_NO_NETWORKS, 0, 0, 0));
        OnStaJoin?.Invoke(null);
    }

    private static void Put16Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

    /// <summary>Build a minimal BCM-event Ethernet frame (no scan payload) carrying event_type/status/
    /// flags/reason/interface at the offsets the cyw43-driver reads after its realign.</summary>
    private byte[] BuildBcmEvent(uint eventType, uint status, ushort flags, uint reason, byte iface)
    {
        var frame = new byte[24 + 64];
        for (var i = 0; i < 6; i++) frame[i] = 0xFF;
        Array.Copy(MacAddress, 0, frame, 6, 6);
        frame[12] = 0x88; frame[13] = 0x6c;
        frame[19] = 0x00; frame[20] = 0x10; frame[21] = 0x18;
        var ev = 24;
        Put16Be(frame, ev + 2, flags);
        Put32Be(frame, ev + 4, eventType);
        Put32Be(frame, ev + 8, status);
        Put32Be(frame, ev + 12, reason);
        frame[ev + 46] = iface;
        return frame;
    }

    private static void Put32Be(byte[] b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    /// <summary>Build the BCM-event Ethernet frame for one ESCAN_RESULT. The cyw43-driver overlays a
    /// <c>wl_bss_info</c> at event-message offset 60 and a parallel scan_result view; the field
    /// offsets here (bssid@68, ssid_len@78, ssid@79, chanspec@132, rssi@138, ie_offset@176,
    /// ie_length@180, all relative to the 24-byte eth/BCM prefix) match that dual layout exactly.</summary>
    private byte[] BuildEscanResultEvent(VirtualAp? ap, uint status)
    {
        const int prefix = 24;          // eth dst/src + ethertype(0x886c) + BCM hdr(OUI 00:10:18)
        var frame = new byte[prefix + 256];
        // Ethernet header: broadcast dst, chip src, BCM ethertype + Broadcom OUI.
        for (var i = 0; i < 6; i++) frame[i] = 0xFF;
        Array.Copy(MacAddress, 0, frame, 6, 6);
        frame[12] = 0x88; frame[13] = 0x6c;
        frame[19] = 0x00; frame[20] = 0x10; frame[21] = 0x18;

        var ev = prefix;                // event-message base
        Put32Be(frame, ev + 4, EV_ESCAN_RESULT);
        Put32Be(frame, ev + 8, status);
        frame[ev + 46] = 0;             // interface = STA

        if (ap != null)
        {
            var bss = ev + 60;          // wl_bss_info overlay
            Put16(frame, ev + 58, 1);   // bss_count = 1
            Put32(frame, bss + 4, 128); // bss.length
            Array.Copy(ap.Bssid, 0, frame, bss + 8, 6);
            if (ap.Secured) Put16(frame, bss + 16, 0x0010); // capability: PRIVACY → WEP/WPA hint
            var ssid = Encoding.UTF8.GetBytes(ap.Ssid);
            var sl = Math.Min(ssid.Length, 32);
            frame[bss + 18] = (byte)sl;
            Array.Copy(ssid, 0, frame, bss + 19, sl);
            Put16(frame, bss + 72, (ushort)ap.Channel);   // chanspec (driver masks &0xff)
            Put16(frame, bss + 78, (ushort)(short)ap.Rssi);
            Put16(frame, bss + 116, 128);  // ie_offset == length, ie_length 0 → no IEs to parse
            Put32(frame, bss + 120, 0);    // ie_length
        }
        return frame;
    }

    /// <summary>Synthesise the payload a GET ioctl returns. SETs return empty (status carries success).</summary>
    private byte[] BuildIoctlPayload(uint cmd, uint kind, string varName, int outLen)
    {
        const uint SDPCM_GET = 0;
        if (kind != SDPCM_GET) return [];

        var buf = new byte[Math.Max(outLen, 4)];
        if (cmd == WLC_GET_VAR)
        {
            switch (varName)
            {
                case "cur_etheraddr":
                    Array.Copy(MacAddress, buf, Math.Min(6, buf.Length));
                    break;
                case "clmver":
                    WriteString(buf, "CLM API: 12.2, Data: RP2040Sharp-virtual");
                    break;
                case "ver":
                    WriteString(buf, "wl0: RP2040Sharp virtual CYW43439 (emulated)");
                    break;
                // Intentionally zeroed (count 0). clmload_status=0 is DLOAD_STATUS_SUCCESS, which the
                // firmware needs after streaming the CLM blob; the event/mcast lists are legitimately empty.
                // Anything else the firmware reads back is an UNMODELLED iovar getting bogus zeros.
                case "bsscfg:event_msgs":
                case "mcast_list":
                case "clmload_status":
                    break;
                default:
                    EmuStrict.Note("cyw43.getvar.unhandled", varName);
                    break;
            }
        }
        return buf;
    }

    private static void WriteString(byte[] dst, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        var n = Math.Min(b.Length, dst.Length - 1);
        Array.Copy(b, dst, n);
        dst[n] = 0;
    }

    /// <summary>Wrap an ioctl response payload in SDPCM + ioctl headers (status=0, echoing the
    /// request flags so the host's id check matches), granting a fresh bus credit.</summary>
    private byte[] BuildControlPacket(uint cmd, uint flags, byte[] payload)
    {
        var size = SdpcmHeaderLen + IoctlHeaderLen + payload.Length;
        var buf = new byte[Align4(size)];
        Put16(buf, 0, size);
        Put16(buf, 2, ~size & 0xffff);
        buf[4] = _chipSeq++;
        buf[5] = ControlHeader;
        buf[6] = 0;                 // next_length
        buf[7] = SdpcmHeaderLen;    // header_length
        buf[8] = 0;                 // wireless flow control
        buf[9] = GrantedCredit;     // grant credit ahead of the host sequence so its next send doesn't stall
        // ioctl header
        Put32(buf, SdpcmHeaderLen + 0, cmd);
        Put32(buf, SdpcmHeaderLen + 4, (uint)payload.Length);
        Put32(buf, SdpcmHeaderLen + 8, flags);  // echo flags (carries the ioctl id the host matches)
        Put32(buf, SdpcmHeaderLen + 12, 0);     // status = success
        if (payload.Length > 0) Array.Copy(payload, 0, buf, SdpcmHeaderLen + IoctlHeaderLen, payload.Length);
        return buf;
    }

    private void HandleEthernetOut(byte[] pkt, int headerLen, int size)
    {
        // The driver sets header_length = SDPCM_HEADER_LEN + 2 for DATA frames, with the BDC header
        // placed AT header_length (the +2 pad sits between the SDPCM header and the BDC). The Ethernet
        // payload follows at bdc + BDC_HEADER_LEN + (data_offset<<2).
        var bdcOff = headerLen;
        if (size <= bdcOff + BdcHeaderLen) return;
        var itf = pkt[bdcOff + 2];
        var dataOff = pkt[bdcOff + 3] << 2;
        var payloadOff = bdcOff + BdcHeaderLen + dataOff;
        if (payloadOff >= size) return;
        OnHostEthernet?.Invoke(itf, pkt[payloadOff..size]);
    }

    /// <summary>The host is reading <paramref name="n"/> bytes from F2 — hand back the oldest queued
    /// packet (zero-padded/truncated to the read length). Drops it from the queue and updates the
    /// packet-ready signal.</summary>
    public byte[] HostRead(int n)
    {
        var outp = new byte[n];
        if (_txq.Count > 0)
        {
            var pkt = _txq.Dequeue();
            Array.Copy(pkt, outp, Math.Min(pkt.Length, n));
        }
        OnPacketReadyChanged?.Invoke(_txq.Count > 0, NextPacketLen);
        return outp;
    }

    /// <summary>Queue an async WLC event (ASYNCEVENT_HEADER) carrying a BCM event Ethernet frame.
    /// Used by the join/link state machine (Fase 5) to deliver WLC_E_* notifications.</summary>
    public void EnqueueAsyncEvent(byte[] bcmEventEthFrame)
    {
        // For a chip→host packet the receiver reads the BDC header at header_length (=12) and the
        // payload at bdc + BDC_HEADER_LEN + (data_offset<<2). So the BDC sits immediately after the
        // 12-byte SDPCM header (no send-side +2 pad), data_offset 0, then the BCM-event Ethernet frame.
        var size = SdpcmHeaderLen + BdcHeaderLen + bcmEventEthFrame.Length;
        var buf = new byte[Align4(size)];
        Put16(buf, 0, size);
        Put16(buf, 2, ~size & 0xffff);
        buf[4] = _chipSeq++;
        buf[5] = AsyncEventHeader;
        buf[6] = 0;
        buf[7] = SdpcmHeaderLen;
        buf[9] = GrantedCredit;
        var bdc = SdpcmHeaderLen;
        buf[bdc + 0] = 0x20;            // flags
        buf[bdc + 3] = 0;               // data_offset (in 4-byte units)
        Array.Copy(bcmEventEthFrame, 0, buf, bdc + BdcHeaderLen, bcmEventEthFrame.Length);
        Enqueue(buf);
    }

    /// <summary>Queue an inbound Ethernet frame for the host on the given interface (DATA_HEADER).
    /// The virtual network (DHCP/ARP/peer traffic) uses this to deliver frames to the STA/AP netif.</summary>
    public void EnqueueEthernet(int itf, byte[] ethFrame)
    {
        var size = SdpcmHeaderLen + BdcHeaderLen + ethFrame.Length;
        var buf = new byte[Align4(size)];
        Put16(buf, 0, size);
        Put16(buf, 2, ~size & 0xffff);
        buf[4] = _chipSeq++;
        buf[5] = DataHeader;
        buf[6] = 0;
        buf[7] = SdpcmHeaderLen;
        buf[9] = GrantedCredit;
        var bdc = SdpcmHeaderLen;
        buf[bdc + 0] = 0x20;       // flags
        buf[bdc + 2] = (byte)itf;  // flags2 = interface
        buf[bdc + 3] = 0;          // data_offset (4-byte units)
        Array.Copy(ethFrame, 0, buf, bdc + BdcHeaderLen, ethFrame.Length);
        Enqueue(buf);
    }

    private void Enqueue(byte[] pkt)
    {
        _txq.Enqueue(pkt);
        OnPacketReadyChanged?.Invoke(true, NextPacketLen);
    }
}

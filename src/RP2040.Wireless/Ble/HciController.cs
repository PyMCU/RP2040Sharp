// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using System.Collections.Generic;

namespace RP2040.Wireless.Ble;

/// <summary>
/// An emulated Bluetooth LE controller at the HCI boundary — the chip side of the host's HCI stack
/// (btstack/NimBLE). It answers the bring-up command sequence (Reset, Read BD_ADDR, LE buffer sizes,
/// …) with the Command Complete/Status events the host expects, and drives the LE link layer:
/// advertising, scanning, and connections. Everything above the link layer (L2CAP/ATT/GATT) runs
/// unmodified in the guests; this controller only relays ACL data between connected peers over the
/// <see cref="VirtualBleRadio"/>, exactly as the Wi-Fi side relays Ethernet frames.
/// </summary>
public sealed class HciController
{
    // HCI command opcodes (OGF &lt;&lt; 10 | OCF).
    private const ushort Reset = 0x0C03, SetEventMask = 0x0C01, SetEventMaskPage2 = 0x0C63;
    private const ushort ReadLocalVersion = 0x1001, ReadLocalSupportedCommands = 0x1002,
                         ReadLocalSupportedFeatures = 0x1003, ReadBufferSize = 0x1005, ReadBdAddr = 0x1009;
    private const ushort LeSetEventMask = 0x2001, LeReadBufferSize = 0x2002, LeReadLocalFeatures = 0x2003,
                         LeSetRandomAddress = 0x2005, LeSetAdvParams = 0x2006, LeReadAdvTxPower = 0x2007,
                         LeSetAdvData = 0x2008, LeSetScanRspData = 0x2009, LeSetAdvEnable = 0x200A,
                         LeSetScanParams = 0x200B, LeSetScanEnable = 0x200C, LeCreateConn = 0x200D,
                         LeCreateConnCancel = 0x200E, LeReadWhiteListSize = 0x200F, LeClearWhiteList = 0x2010,
                         LeReadSupportedStates = 0x201C, LeRand = 0x2018, LeReadBufferSizeV2 = 0x2060;
    private const ushort LeConnUpdate = 0x2013, LeReadRemoteFeatures = 0x2016, LeSetDataLength = 0x2022;
    private const ushort Disconnect = 0x0406, ReadRssi = 0x1405;

    // HCI event codes.
    private const byte EvtDisconnComplete = 0x05, EvtCommandComplete = 0x0E, EvtCommandStatus = 0x0F,
                       EvtNumCompletedPackets = 0x13, EvtLeMeta = 0x3E;
    // LE meta sub-events.
    private const byte LeConnComplete = 0x01, LeAdvReport = 0x02, LeConnUpdateComplete = 0x03,
                       LeReadRemoteFeaturesComplete = 0x04, LeEnhancedConnComplete = 0x0A;

    /// <summary>Send an HCI packet (type, payload) to the host via the shared bus. Wired by BtSharedBus.</summary>
    public Action<byte, byte[]>? SendToHost;

    /// <summary>This controller's public BD_ADDR (6 bytes, little-endian on the wire).</summary>
    public byte[] BdAddr { get; set; } = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];

    /// <summary>The shared virtual radio linking this controller to peers (advertising/scan/connect/ACL).</summary>
    public VirtualBleRadio? Radio;

    /// <summary>Diagnostics: a command was handled (opcode), an LE event emitted, etc.</summary>
    public Action<string>? OnTrace;

    // Link-layer state.
    internal bool Advertising;
    internal bool Scanning;
    internal byte[] AdvData = [];
    internal byte AdvType;             // 0=ADV_IND, 2=ADV_SCAN_IND, 3=ADV_NONCONN_IND
    internal byte OwnAddrType;
    private ushort _nextHandle = 0x0040;
    private readonly Dictionary<ushort, BleLinkHolder> _links = new(); // connection handle → link
    private readonly Dictionary<ushort, Action<byte[]>> _externalLinks = new(); // handle → ACL relay to peer

    // ── Cross-assembly bridging seam ──────────────────────────────────────────────────────────────
    // The in-process VirtualBleRadio links controllers that live in THIS assembly. To link a controller
    // to a peer in a DIFFERENT assembly (e.g. an RP2040 ↔ an RP2350 in one test harness), an external
    // coordinator drives the same link layer through this public surface instead of via Radio.

    /// <summary>This controller's host has advertising enabled.</summary>
    public bool IsAdvertising => Advertising;
    /// <summary>This controller's host has scanning enabled.</summary>
    public bool IsScanning => Scanning;
    /// <summary>The advertising PDU type (0=ADV_IND, 2=ADV_SCAN_IND, 3=ADV_NONCONN_IND).</summary>
    public byte AdvertisingType => AdvType;
    /// <summary>The advertising payload the host set.</summary>
    public byte[] AdvertisingData => AdvData;
    /// <summary>This controller's own address type (0=public, 1=random).</summary>
    public byte LocalAddrType => OwnAddrType;

    /// <summary>Advertising was enabled/disabled by the host.</summary>
    public Action? OnAdvertisingChanged;
    /// <summary>Scanning was enabled by the host (an external radio should deliver current advertisers).</summary>
    public Action? OnScanningChanged;
    /// <summary>The host issued LE Create Connection: (peerAddr, asCentral). An external radio links it.</summary>
    public Action<byte[], bool>? OnHostCreateConnection;

    /// <summary>Deliver an advertising report to this (scanning) controller's host.</summary>
    public void DeliverAdvReport(byte[] peerAddr, byte peerAddrType, byte advType, byte[] advData, sbyte rssi)
        => EmitAdvReport(peerAddr, peerAddrType, advType, advData, rssi);

    /// <summary>Open a connection driven by an external radio: reserve a handle, register the relay that
    /// forwards this side's ACL to the peer, and emit LE Connection Complete. Returns the local handle.</summary>
    public ushort OpenExternalLink(byte role, byte[] peerAddr, byte peerAddrType, Action<byte[]> aclToPeer)
    {
        var h = _nextHandle++;
        _externalLinks[h] = aclToPeer;
        EmitConnectionComplete(h, role, peerAddr, peerAddrType);
        return h;
    }

    /// <summary>Deliver an ACL packet (already handle-rewritten for this side) up to this host.</summary>
    public void DeliverAclToHost(byte[] acl) => DeliverAcl(acl);

    /// <summary>Tear down an external-radio link and notify this host.</summary>
    public void CloseExternalLink(ushort handle, byte reason)
    {
        _externalLinks.Remove(handle);
        CloseLink(handle, reason);
    }

    public void HandleHostPacket(byte type, byte[] p)
    {
        switch (type)
        {
            case BtSharedBus.HciCommand: HandleCommand(p); break;
            case BtSharedBus.HciAcl: HandleAcl(p); break;
        }
    }

    private void HandleCommand(byte[] p)
    {
        if (p.Length < 3) return;
        var opcode = (ushort)(p[0] | p[1] << 8);
        var plen = p[2];
        var args = p.Length > 3 ? p[3..] : [];
        OnTrace?.Invoke($"CMD 0x{opcode:X4} len{plen}");

        switch (opcode)
        {
            // ── bring-up: Command Complete with status 0 + the parameters the host validates ──
            case ReadBdAddr: CommandComplete(opcode, Concat([0x00], BdAddr)); break;
            case ReadLocalVersion:
                // status, HCI version(5.3=12), HCI rev, LMP version(12), manufacturer(0x000F Broadcom), LMP subver
                CommandComplete(opcode, [0x00, 12, 0x00, 0x00, 12, 0x0F, 0x00, 0x00, 0x00]); break;
            case ReadLocalSupportedCommands:
                // The Supported Commands bitmap gates btstack's init: a zero map makes it believe the
                // controller supports nothing, so it SKIPS Read Buffer Size / LE Read Buffer Size and
                // ends up with zero ACL credits — connections still form, but no ATT/GATT ACL can ever
                // be sent. Advertise everything supported so btstack runs full init and learns its
                // ACL buffer budget. (status + 64-byte all-ones bitmap.)
                CommandComplete(opcode, Concat([0x00], Filled(64, 0xFF))); break;
            case ReadLocalSupportedFeatures:
                CommandComplete(opcode, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40]); break; // LE supported (byte4 bit6)
            case ReadBufferSize: CommandComplete(opcode, [0x00, 0xFB, 0x00, 0x01, 0x08, 0x00, 0x08, 0x00]); break;
            case LeReadBufferSize: CommandComplete(opcode, [0x00, 0xFB, 0x00, 0x08]); break;     // ACL len 251, 8 packets
            case LeReadBufferSizeV2: CommandComplete(opcode, [0x00, 0xFB, 0x00, 0x08, 0xFB, 0x00, 0x08]); break;
            case LeReadLocalFeatures: CommandComplete(opcode, Concat([0x00], new byte[8])); break;
            case LeReadSupportedStates: CommandComplete(opcode, Concat([0x00], new byte[8])); break;
            case LeReadWhiteListSize: CommandComplete(opcode, [0x00, 0x08]); break;
            case LeReadAdvTxPower: CommandComplete(opcode, [0x00, 0x09]); break;
            case ReadRssi: CommandComplete(opcode, Concat(Concat([0x00], U16(args.Length >= 2 ? (ushort)(args[0] | args[1] << 8) : (ushort)0)), [unchecked((byte)-50)])); break;
            case LeRand: CommandComplete(opcode, Concat([0x00], new byte[8])); break;

            // ── LE addresses / data ──
            case LeSetRandomAddress: if (args.Length >= 6) BdAddr = args[..6]; CommandComplete(opcode, [0x00]); break;
            case LeSetAdvParams: if (args.Length >= 15) AdvType = args[4]; CommandComplete(opcode, [0x00]); break;
            case LeSetAdvData:
                if (args.Length >= 1) { var n = Math.Min(args[0], (byte)31); AdvData = args.Length >= 1 + n ? args[1..(1 + n)] : []; }
                CommandComplete(opcode, [0x00]); break;
            case LeSetScanRspData: CommandComplete(opcode, [0x00]); break;

            // ── advertising / scanning enable ──
            case LeSetAdvEnable:
                Advertising = args.Length >= 1 && args[0] != 0;
                OnTrace?.Invoke($"ADV {(Advertising ? "ON" : "OFF")}");
                CommandComplete(opcode, [0x00]);
                OnAdvertisingChanged?.Invoke();
                break;
            case LeSetScanParams: CommandComplete(opcode, [0x00]); break;
            case LeSetScanEnable:
                Scanning = args.Length >= 1 && args[0] != 0;
                OnTrace?.Invoke($"SCAN {(Scanning ? "ON" : "OFF")}");
                CommandComplete(opcode, [0x00]);
                if (Scanning) Radio?.RequestAdvertisements(this);   // deliver any current advertisers' reports
                OnScanningChanged?.Invoke();
                break;

            // ── connection ──
            case LeCreateConn:
                CommandStatus(opcode, 0x00);
                // args: scan_interval(2) scan_window(2) filter(1) peer_addr_type(1) peer_addr(6) own_addr_type(1) ...
                if (args.Length >= 12)
                {
                    Radio?.CreateConnection(this, args[6..12], asCentral: true);
                    OnHostCreateConnection?.Invoke(args[6..12], true);
                }
                break;
            case LeCreateConnCancel: CommandComplete(opcode, [0x00]); break;
            // Post-connection setup btstack runs before it will start ATT/GATT. Each is a "status now,
            // result later" command: a Command Status, then the matching LE Meta completion event. The
            // host blocks waiting for that event, so a bare Command Complete (the default) stalls GATT.
            case LeReadRemoteFeatures:
                CommandStatus(opcode, 0x00);
                if (args.Length >= 2)
                {
                    var h = (ushort)(args[0] | args[1] << 8);
                    // LE Read Remote Features Complete: subevent, status, handle(2), 8 feature bytes.
                    var ev = new List<byte> { LeReadRemoteFeaturesComplete, 0x00, (byte)h, (byte)(h >> 8) };
                    ev.AddRange(new byte[8]);
                    LeMeta(ev.ToArray());
                }
                break;
            case LeConnUpdate:
                CommandStatus(opcode, 0x00);
                if (args.Length >= 14)
                {
                    var h = (ushort)(args[0] | args[1] << 8);
                    // LE Connection Update Complete: subevent, status, handle(2), interval(2), latency(2), timeout(2).
                    LeMeta([LeConnUpdateComplete, 0x00, (byte)h, (byte)(h >> 8),
                            args[2], args[3], args[6], args[7], args[8], args[9]]);
                }
                break;
            case Disconnect:
                if (args.Length >= 2) { var h = (ushort)(args[0] | args[1] << 8); CommandStatus(opcode, 0x00); CloseLink(h, 0x16); }
                else CommandStatus(opcode, 0x12);
                break;

            // ── everything else the bring-up touches: accept with status 0 ──
            default: CommandComplete(opcode, [0x00]); break;
        }
    }

    // ── ACL data: relay between connected peers ──
    private void HandleAcl(byte[] acl)
    {
        if (acl.Length < 4) return;
        var handle = (ushort)((acl[0] | acl[1] << 8) & 0x0FFF);
        if (_links.TryGetValue(handle, out var link))
        {
            // Acknowledge the host's TX (flow control), then relay the payload to the peer's handle.
            SendNumberOfCompletedPackets(handle, 1);
            link.DeliverAclToPeer(this, acl);
        }
        else if (_externalLinks.TryGetValue(handle, out var relay))
        {
            // Same relay, but to a peer controller in another assembly (cross-assembly bridge).
            SendNumberOfCompletedPackets(handle, 1);
            relay(acl);
        }
    }

    // ── events the radio/links drive ──
    internal void EmitConnectionComplete(ushort handle, byte role, byte[] peerAddr, byte peerAddrType)
    {
        // LE Connection Complete: subevent, status, handle(2), role, peer_addr_type, peer_addr(6),
        // conn_interval(2), conn_latency(2), supervision_timeout(2), master_clock_accuracy(1)
        var ev = new List<byte> { LeConnComplete, 0x00, (byte)handle, (byte)(handle >> 8), role, peerAddrType };
        ev.AddRange(peerAddr);
        ev.AddRange(new byte[] { 0x18, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00 }); // 30ms interval, 0 latency, 420ms timeout
        LeMeta(ev.ToArray());
    }

    internal void EmitAdvReport(byte[] peerAddr, byte peerAddrType, byte advType, byte[] advData, sbyte rssi)
    {
        // LE Advertising Report: subevent, num_reports=1, event_type, addr_type, addr(6), data_len, data, rssi
        var ev = new List<byte> { LeAdvReport, 0x01, advType, peerAddrType };
        ev.AddRange(peerAddr);
        ev.Add((byte)advData.Length);
        ev.AddRange(advData);
        ev.Add((byte)rssi);
        LeMeta(ev.ToArray());
    }

    internal void DeliverAcl(byte[] acl) => SendToHost?.Invoke(BtSharedBus.HciAcl, acl);

    internal ushort RegisterLinkDeferred(BleLinkHolder link) { var h = _nextHandle++; _links[h] = link; return h; }

    internal void CloseLink(ushort handle, byte reason)
    {
        if (_links.Remove(handle, out var link))
        {
            link.NotifyClosed(this);
            // Disconnection Complete: status, handle(2), reason
            SendToHost?.Invoke(BtSharedBus.HciEvent, [EvtDisconnComplete, 0x04, 0x00, (byte)handle, (byte)(handle >> 8), reason]);
        }
    }

    // ── HCI event encoders ──
    private void CommandComplete(ushort opcode, byte[] returnParams)
    {
        var ev = new byte[3 + returnParams.Length];
        ev[0] = 0x01; ev[1] = (byte)opcode; ev[2] = (byte)(opcode >> 8);
        Array.Copy(returnParams, 0, ev, 3, returnParams.Length);
        SendToHost?.Invoke(BtSharedBus.HciEvent, Concat([EvtCommandComplete, (byte)ev.Length], ev));
    }

    private void CommandStatus(ushort opcode, byte status) =>
        SendToHost?.Invoke(BtSharedBus.HciEvent, [EvtCommandStatus, 0x04, status, 0x01, (byte)opcode, (byte)(opcode >> 8)]);

    private void LeMeta(byte[] sub) =>
        SendToHost?.Invoke(BtSharedBus.HciEvent, Concat([EvtLeMeta, (byte)sub.Length], sub));

    private void SendNumberOfCompletedPackets(ushort handle, ushort count) =>
        SendToHost?.Invoke(BtSharedBus.HciEvent,
            [EvtNumCompletedPackets, 0x05, 0x01, (byte)handle, (byte)(handle >> 8), (byte)count, (byte)(count >> 8)]);

    private static byte[] Filled(int n, byte v) { var b = new byte[n]; Array.Fill(b, v); return b; }
    private static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
    private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Array.Copy(a, r, a.Length); Array.Copy(b, 0, r, a.Length, b.Length); return r; }
}

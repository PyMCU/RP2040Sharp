// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System.Collections.Generic;

namespace RP2040Sharp.Wireless.Ble;

/// <summary>
/// The virtual BLE air linking several emulated controllers (each a Pico 2 W). It carries the link
/// layer the controllers share: advertising packets reach scanners as Advertising Reports, a central's
/// Create Connection finds the matching advertiser and establishes a <see cref="BleLink"/> (both sides
/// get LE Connection Complete), and ACL traffic is relayed peer-to-peer. The host BLE stacks
/// (btstack/NimBLE, GATT/ATT) run unmodified above it.
/// </summary>
public sealed class VirtualBleRadio
{
    private readonly List<HciController> _devices = new();
    // How many advertising reports a scanner has had from each advertiser in the current scan session.
    // A real advertiser repeats every interval, but we cap delivery so a fast host poll loop can't be
    // flooded with thousands of identical reports (which exhausts the guest's heap).
    private readonly Dictionary<(HciController, HciController), int> _reportCount = new();
    private const int MaxReportsPerSession = 8;

    /// <summary>Diagnostics: a connection was established (central addr, peripheral addr).</summary>
    public System.Action<byte[], byte[]>? OnConnected;

    public void AddDevice(HciController c) { _devices.Add(c); c.Radio = this; }

    /// <summary>Deliver, to <paramref name="scanner"/>, an Advertising Report for every other device
    /// that is currently advertising (rate-limited per scan session). Called on scan enable and from
    /// <see cref="Poll"/>.</summary>
    public void RequestAdvertisements(HciController scanner)
    {
        if (!scanner.Scanning)
        {
            // Scan stopped: forget this scanner's deliveries so the next scan rediscovers advertisers.
            foreach (var key in new List<(HciController, HciController)>(_reportCount.Keys))
                if (key.Item1 == scanner) _reportCount.Remove(key);
            return;
        }
        foreach (var d in _devices)
        {
            if (d == scanner || !d.Advertising) continue;
            var key = (scanner, d);
            _reportCount.TryGetValue(key, out var n);
            if (n >= MaxReportsPerSession) continue;
            _reportCount[key] = n + 1;
            scanner.EmitAdvReport(d.BdAddr, d.OwnAddrType, d.AdvType, d.AdvData, rssi: -50);
        }
    }

    /// <summary>Periodic tick: deliver advertising reports to every active scanner so a scan that
    /// starts before an advertiser, or runs for a window, still discovers it (bounded per session).</summary>
    public void Poll()
    {
        foreach (var s in _devices)
            RequestAdvertisements(s);
    }

    /// <summary>A central is connecting to <paramref name="peerAddr"/>. Find the advertiser with that
    /// address, register a link on both controllers, and emit LE Connection Complete to each.</summary>
    public void CreateConnection(HciController central, byte[] peerAddr, bool asCentral)
    {
        HciController? peripheral = null;
        foreach (var d in _devices)
            if (d != central && d.Advertising && AddrEquals(d.BdAddr, peerAddr)) { peripheral = d; break; }
        if (peripheral == null) return; // no advertiser — the host's create-connection simply times out

        peripheral.Advertising = false; // a connectable advertiser stops advertising once connected

        // Reserve handles, wire the link, then notify both ends (central role=0, peripheral role=1).
        var link = new BleLinkHolder();
        var hC = central.RegisterLinkDeferred(link);
        var hP = peripheral.RegisterLinkDeferred(link);
        link.Bind(central, hC, peripheral, hP);

        central.EmitConnectionComplete(hC, role: 0x00, peripheral.BdAddr, peripheral.OwnAddrType);
        peripheral.EmitConnectionComplete(hP, role: 0x01, central.BdAddr, central.OwnAddrType);
        OnConnected?.Invoke(central.BdAddr, peripheral.BdAddr);
    }

    private static bool AddrEquals(byte[] a, byte[] b)
    {
        if (a.Length < 6 || b.Length < 6) return false;
        for (var i = 0; i < 6; i++) if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>Late-bound <see cref="BleLink"/> so both handles can be reserved before the link exists.</summary>
public sealed class BleLinkHolder
{
    private BleLink? _link;
    public void Bind(HciController a, ushort ha, HciController b, ushort hb) => _link = new BleLink(a, ha, b, hb);
    public void DeliverAclToPeer(HciController from, byte[] acl) => _link?.DeliverAclToPeer(from, acl);
    public void NotifyClosed(HciController from) => _link?.NotifyClosed(from);
}

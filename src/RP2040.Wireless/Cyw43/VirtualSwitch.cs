// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System.Collections.Generic;

namespace RP2040.Wireless.Cyw43;

/// <summary>
/// A pure layer-2 switch joining several emulated CYW43439 stations on one virtual air — no gateway
/// services of its own. Used for the STA+AP topology where one Pico runs in AP mode (and provides its
/// own DHCP) while another joins as a STA: frames are forwarded between chips by destination MAC, with
/// each device delivered on the interface it actually uses (STA=0 / AP=1), so the guest TCP/IP stacks
/// see traffic on the correct netif.
/// </summary>
public sealed class VirtualSwitch
{
    private sealed record Port(Sdpcm Sdpcm, int Itf);

    private readonly List<Port> _ports = new();
    private readonly Dictionary<string, Port> _macToPort = new();

    /// <summary>Diagnostics: (sourceItf, ethertype, length) of a frame entering the switch.</summary>
    public System.Action<int, ushort, int>? OnFrame;

    /// <summary>Attach a station to the switch. <paramref name="itf"/> is the SDPCM interface the
    /// guest uses for this network (STA = 0, AP = 1).</summary>
    public void AddDevice(Sdpcm sdpcm, int itf)
    {
        var port = new Port(sdpcm, itf);
        _ports.Add(port);
        sdpcm.OnHostEthernet += (srcItf, frame) => Forward(port, frame);
    }

    private static string MacKey(byte[] b, int o) =>
        $"{b[o]:x2}{b[o + 1]:x2}{b[o + 2]:x2}{b[o + 3]:x2}{b[o + 4]:x2}{b[o + 5]:x2}";

    private void Forward(Port from, byte[] frame)
    {
        if (frame.Length < 14) return;
        _macToPort[MacKey(frame, 6)] = from;                 // learn source MAC → port
        OnFrame?.Invoke(from.Itf, (ushort)(frame[12] << 8 | frame[13]), frame.Length);

        var broadcastOrMulticast = (frame[0] & 1) != 0;
        if (broadcastOrMulticast)
        {
            foreach (var p in _ports)
                if (p != from) p.Sdpcm.EnqueueEthernet(p.Itf, frame);  // flood to all other ports
            return;
        }
        if (_macToPort.TryGetValue(MacKey(frame, 0), out var dest) && dest != from)
            dest.Sdpcm.EnqueueEthernet(dest.Itf, frame);              // unicast to the owning port
    }
}

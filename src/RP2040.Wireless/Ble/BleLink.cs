// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

namespace RP2040.Wireless.Ble;

/// <summary>
/// An established LE connection between two emulated controllers. Each side has its own connection
/// handle; ACL data sent by one is relayed to the other with the peer's handle rewritten in, so the
/// guests' L2CAP/ATT/GATT layers talk to each other unmodified over the virtual radio.
/// </summary>
public sealed class BleLink
{
    private readonly HciController _a, _b;
    private readonly ushort _handleA, _handleB;

    public BleLink(HciController a, ushort handleA, HciController b, ushort handleB)
    {
        _a = a; _handleA = handleA; _b = b; _handleB = handleB;
    }

    /// <summary>Relay an ACL packet from <paramref name="from"/> to the peer, rewriting the 12-bit
    /// connection handle to the peer's and setting the packet-boundary flag for the controller→host
    /// direction.</summary>
    public void DeliverAclToPeer(HciController from, byte[] acl)
    {
        var (peer, peerHandle) = from == _a ? (_b, _handleB) : (_a, _handleA);
        var copy = (byte[])acl.Clone();
        // The host sends with PB=0b00 (host→controller, non-flushable). On the wire to the *receiving*
        // host this is a controller→host packet, which must carry PB=0b10 ("first fragment / start of
        // an L2CAP PDU") — btstack's receive path drops anything else as "invalid boundary flags".
        // Our links always relay a complete L2CAP PDU in one ACL, so 0b10 is always correct.
        var hdr = (ushort)((peerHandle & 0x0FFF) | (0x2 << 12)); // PB=0b10, BC=0b00
        copy[0] = (byte)hdr; copy[1] = (byte)(hdr >> 8);
        peer.DeliverAcl(copy);
    }

    /// <summary>One side closed; tear down the peer's handle too.</summary>
    public void NotifyClosed(HciController from)
    {
        var (peer, peerHandle) = from == _a ? (_b, _handleB) : (_a, _handleA);
        peer.CloseLink(peerHandle, 0x16); // connection terminated by local host
    }
}

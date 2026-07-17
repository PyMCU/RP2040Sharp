// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using RP2040Sharp.Wireless.Cyw43;

namespace RP2040Sharp.Wireless.Ble;

/// <summary>
/// The CYW43439's Bluetooth "shared bus": HCI transported over the same gSPI/SDIO backplane as Wi-Fi,
/// through a pair of circular buffers in chip RAM plus a few control registers (cybt_shared_bus). The
/// host downloads BT firmware, raises SW_RDY, waits for the chip's FW_RDY, then reads/writes HCI
/// packets via the H2B (host→BT) and B2H (BT→host) rings. We model the chip side: parse the host's HCI
/// commands/ACL out of H2B and hand them to the <see cref="HciController"/>, and serialise the
/// controller's HCI events/ACL back into B2H, signalling the host through I_HMB_FC_CHANGE.
/// </summary>
public sealed class BtSharedBus
{
    // Backplane registers the cybt driver pokes (chip addresses).
    private const uint BtCtrlReg      = 0x18000C7C; // chip→host: FW_RDY(b24), BT_AWAKE(b8), DATA_VALID(b1)
    private const uint HostCtrlReg    = 0x18000D6C; // host→chip: SW_RDY(b24), WAKE_BT(b17), DATA_VALID(b1)
    private const uint WlanRamBaseReg = 0x18000D68; // chip tells host where the BT RAM/rings live
    private const uint SdioIntStatusAddr = 0x18002020;

    private const uint FwRdy = 1u << 24, BtAwake = 1u << 8;          // BT_CTRL bits we drive
    private const uint SwRdy = 1u << 24;                            // HOST_CTRL bit the host sets
    private const uint IHmbFcChange = 0x20;                         // "BT has work" in SDIO_INT_STATUS

    // BT RAM layout (cybt_shared_bus_driver): rings + their in/out indices at fixed offsets from base.
    private const uint WlanRamBase = 0x19100000;
    private const uint FwBufSize   = 0x1000;
    private const uint H2bBuf  = WlanRamBase + 0x0000;   // host→BT data ring (host writes)
    private const uint B2hBuf  = WlanRamBase + 0x1000;   // BT→host data ring (we write)
    private const uint H2bIn   = WlanRamBase + 0x2000;
    private const uint H2bOut  = WlanRamBase + 0x2004;
    private const uint B2hIn   = WlanRamBase + 0x2008;
    private const uint B2hOut  = WlanRamBase + 0x200C;

    // HCI H4 packet types (the 4th header byte in each ring entry).
    public const byte HciCommand = 0x01, HciAcl = 0x02, HciEvent = 0x04;

    private readonly Backplane _chip;
    private readonly HciController _hci;
    private uint _hostCtrl;
    private bool _swReady;

    /// <summary>Raised whenever the chip→host work state may have changed (a B2H packet was queued or
    /// the host drained the ring). The device ORs this into the gSPI host-wake line (GPIO24) so an idle
    /// guest is woken to run <c>cyw43_poll</c> → <c>cyw43_ll_bt_has_work</c>, exactly as the Wi-Fi F2
    /// path does — without it a peripheral sitting at the REPL never notices an inbound HCI packet.</summary>
    public Action? OnWorkChanged;

    /// <summary>Diagnostics: low-level trace of BT shared-bus register/ring activity.</summary>
    public Action<string>? OnDiag;
    /// <summary>Diagnostics: a host→chip HCI packet (type, payload) was extracted from H2B.</summary>
    public Action<byte, byte[]>? OnHostPacket;
    /// <summary>Diagnostics: an HCI packet (type, payload) was queued chip→host into B2H.</summary>
    public Action<byte, byte[]>? OnChipPacket;

    public BtSharedBus(Backplane chip, HciController hci)
    {
        _chip = chip;
        _hci = hci;
        _hci.SendToHost = (type, payload) => QueueB2H(type, payload);
        _chip.Bt = this;
    }

    /// <summary>Whether <paramref name="addr"/> is one of the BT control registers (4 bytes each).</summary>
    public bool OwnsRegister(uint addr) =>
        (addr >= BtCtrlReg && addr < BtCtrlReg + 4) ||
        (addr >= HostCtrlReg && addr < HostCtrlReg + 4) ||
        (addr >= WlanRamBaseReg && addr < WlanRamBaseReg + 4);

    public byte ReadRegByte(uint addr)
    {
        // NB: WLAN_RAM_BASE (0xD68) sits *below* HOST_CTRL (0xD6C), so each register must be matched by
        // its exact 4-byte range — an ordered "addr < HOST_CTRL+4" chain would wrongly swallow 0xD68.
        uint reg, baseAddr;
        if (addr >= BtCtrlReg && addr < BtCtrlReg + 4)        { baseAddr = BtCtrlReg;      reg = BtCtrlValue(); }
        else if (addr >= HostCtrlReg && addr < HostCtrlReg + 4) { baseAddr = HostCtrlReg;  reg = _hostCtrl; }
        else { baseAddr = WlanRamBaseReg; reg = WlanRamBase; if (addr == WlanRamBaseReg) OnDiag?.Invoke($"read WLAN_RAM_BASE=0x{reg:X8}"); }
        return (byte)(reg >> (int)((addr - baseAddr) * 8));
    }

    public void WriteRegByte(uint addr, byte value)
    {
        if (addr >= HostCtrlReg && addr < HostCtrlReg + 4)
        {
            var shift = (int)((addr - HostCtrlReg) * 8);
            _hostCtrl = (_hostCtrl & ~(0xFFu << shift)) | ((uint)value << shift);
            if ((_hostCtrl & SwRdy) != 0) _swReady = true;   // host signalled ready → chip reports FW_RDY
            if (addr == HostCtrlReg + 3) OnDiag?.Invoke($"HOSTCTRL=0x{_hostCtrl:X8} h2bIn={_chip.MemReadU32(H2bIn)} h2bOut={_chip.MemReadU32(H2bOut)}");
            // Any host-control write may accompany an H2B push (the driver toggles DATA_VALID after
            // writing a packet); drain whatever the host has queued.
            DrainH2B();
        }
        // BT_CTRL / WLAN_RAM_BASE are chip-driven (read-only to the host); ignore writes.
    }

    // The host downloads the BT firmware, then polls BT_CTRL for FW_RDY *before* it sets SW_RDY, so
    // the emulated controller reports ready as soon as it's queried (the firmware "boots" instantly),
    // and is always awake. (_swReady is tracked only to gate H2B draining onced the host is up.)
    private uint BtCtrlValue() => FwRdy | BtAwake | (_b2hPending ? 2u : 0u);

    // I_HMB_FC_CHANGE is a write-1-to-clear interrupt *latch*, NOT the live ring state. The chip raises
    // it when it pushes a B2H packet; the host clears it in cyw43_ll_bt_has_work and only THEN — at
    // thread level, via btstack's run loop — drains the ring. If we instead derived "work" from the
    // live ring (B2H_IN != B2H_OUT), the bit would stay set until the drain, but the drain can't run
    // because the still-asserted level-high host-wake keeps PendSV/cyw43_poll spinning and starves the
    // thread: a deadlock. Latching it (set on queue, cleared on the host's write-1-to-clear) lets the
    // pin drop the moment the host acknowledges, so the thread runs and reads the ring — as on silicon.
    private bool _b2hPending;
    /// <summary>Whether an inbound B2H interrupt is latched (drives SDIO_INT_STATUS and the host-wake line).</summary>
    public bool HasWork => _b2hPending;

    /// <summary>SDIO_INT_STATUS as the host's <c>cyw43_ll_bt_has_work</c> reads it.</summary>
    public uint SdioIntStatus() => _b2hPending ? IHmbFcChange : 0u;
    public void ClearSdioInt(int mask) { if ((mask & IHmbFcChange) != 0) { _b2hPending = false; OnWorkChanged?.Invoke(); } }

    // ── H2B (host → controller) ────────────────────────────────────────────
    private void DrainH2B()
    {
        // Read complete HCI packets out of the circular H2B buffer (each prefixed by a 4-byte header:
        // 3-byte little-endian payload length + 1-byte H4 packet type) until in==out.
        for (var guard = 0; guard < 64; guard++)
        {
            var inIdx = _chip.MemReadU32(H2bIn) & (FwBufSize - 1);
            var outIdx = _chip.MemReadU32(H2bOut) & (FwBufSize - 1);
            var avail = (inIdx - outIdx) & (FwBufSize - 1);
            if (avail < 4) return;

            var hdr = ReadRing(H2bBuf, outIdx, 4);
            var len = (uint)(hdr[0] | hdr[1] << 8 | hdr[2] << 16);
            var type = hdr[3];
            // The host pads each ring entry up to a 4-byte boundary, so consume the rounded length or
            // the next packet's OUT pointer drifts out of alignment.
            var total = (4 + len + 3u) & ~3u;
            if (((inIdx - outIdx) & (FwBufSize - 1)) < total) return; // packet not fully written yet

            var payload = ReadRing(H2bBuf, (outIdx + 4) & (FwBufSize - 1), len);
            _chip.MemWriteU32(H2bOut, (outIdx + total) & (FwBufSize - 1));
            OnHostPacket?.Invoke(type, payload);
            _hci.HandleHostPacket(type, payload);
        }
    }

    // ── B2H (controller → host) ──────────────────────────────────────────────
    private void QueueB2H(byte type, byte[] payload)
    {
        var inIdx = _chip.MemReadU32(B2hIn) & (FwBufSize - 1);
        var outIdx = _chip.MemReadU32(B2hOut) & (FwBufSize - 1);
        var total = (4 + (uint)payload.Length + 3u) & ~3u;     // entries are 4-byte aligned, like the host expects
        // Drop if the ring can't hold the packet (the host hasn't drained yet) rather than wrap over
        // unread data — a real controller would back-pressure; corrupting the ring crashes the host.
        var space = (outIdx - (inIdx + 4)) & (FwBufSize - 1);
        if (total > space) return;
        var hdr = new byte[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), type };
        WriteRing(B2hBuf, inIdx, hdr);
        WriteRing(B2hBuf, (inIdx + 4) & (FwBufSize - 1), payload);
        _chip.MemWriteU32(B2hIn, (inIdx + total) & (FwBufSize - 1));
        _b2hPending = true;                  // latch I_HMB_FC_CHANGE + assert host-wake so the host polls
        OnWorkChanged?.Invoke();
        OnChipPacket?.Invoke(type, payload);
    }

    private byte[] ReadRing(uint ringBase, uint start, uint len)
    {
        var b = new byte[len];
        for (uint i = 0; i < len; i++) b[i] = _chip.MemReadByte(ringBase + ((start + i) & (FwBufSize - 1)));
        return b;
    }

    private void WriteRing(uint ringBase, uint start, byte[] data)
    {
        for (uint i = 0; i < data.Length; i++) _chip.MemWriteByte(ringBase + ((start + i) & (FwBufSize - 1)), data[i]);
    }
}

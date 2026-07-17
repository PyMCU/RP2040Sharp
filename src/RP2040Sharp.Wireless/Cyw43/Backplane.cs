// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System.Collections.Generic;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// The CYW43439's internal address space as reached through the SDIO/gSPI F1 backplane window:
/// ChipCommon, the core wrappers (ARM CM3 / SOCSRAM, reachable through AI_IOCTRL/AI_RESETCTRL) and
/// the SOCRAM the host downloads firmware/NVRAM/CLM into. Modelled storage-backed and sparse — the
/// firmware blob is accepted and parked (never executed; there is no RF), and the few status
/// registers the bring-up code polls read back as already-settled so the host advances. This is the
/// same "intercept the MMIO the init waits on" technique the ESP32Sharp radio model uses.
/// </summary>
public sealed class Backplane
{
    // Core/clock register addresses the bring-up code touches (cyw43_ll.c).
    private const uint SdioBase    = 0x18002000;
    private const uint SdioIntStatus = SdioBase + 0x20; // polled, expects "no error" (0)
    private const uint ChipCommon  = 0x18000000;

    // AI (ARM-interconnect) core wrappers. cyw43_ll.c: get_core_address() = base + WRAPPER_OFFSET,
    // then it pokes AI_IOCTRL (+0x408) and AI_RESETCTRL (+0x800). At power-on both cores sit in
    // reset, so RESETCTRL must read back AIRC_RESET (bit 0) until the host clears it — that is the
    // exact handshake disable_device_core()/reset_device_core()/device_core_is_up() walk.
    private const uint WlanArmWrapper = 0x18003000 + 0x100000; // CORE_WLAN_ARM
    private const uint SocsramWrapper = 0x18004000 + 0x100000; // CORE_SOCRAM
    private const uint AiIoctrlOffset    = 0x408;
    private const uint AiResetctrlOffset = 0x800;
    private const byte  AircReset = 0x01;

    private readonly Dictionary<uint, byte> _mem = new(4096);

    /// <summary>True once the host has taken the WLAN-ARM core out of reset (RESETCTRL←0). The
    /// downloaded firmware would now be "running", so the F2 (WLAN data) channel reports ready.</summary>
    public bool WlanCoreUp { get; private set; }

    public Backplane()
    {
        // Cores come out of power-on in reset.
        _mem[WlanArmWrapper + AiResetctrlOffset] = AircReset;
        _mem[SocsramWrapper + AiResetctrlOffset] = AircReset;
    }

    /// <summary>Total firmware/NVRAM/CLM bytes written into SOCRAM (diagnostics).</summary>
    public long BytesDownloaded { get; private set; }

    /// <summary>The Bluetooth shared-bus transport (HCI over the backplane), if BLE is wired. It owns
    /// the BT control/host-control registers and the H2B/B2H rings; the backplane routes the relevant
    /// chip addresses to it. Null when only Wi-Fi is in use.</summary>
    public Ble.BtSharedBus? Bt;

    public byte ReadByte(uint addr)
    {
        // Bluetooth shared-bus registers + the "BT has work" interrupt bit live in the backplane.
        if (Bt != null)
        {
            if (Bt.OwnsRegister(addr)) return Bt.ReadRegByte(addr);
            if (addr >= SdioIntStatus && addr < SdioIntStatus + 4)
                return (byte)(Bt.SdioIntStatus() >> (int)((addr - SdioIntStatus) * 8));
        }
        // Known status registers read back settled; everything else is plain (downloaded) storage or 0.
        if (addr >= SdioIntStatus && addr < SdioIntStatus + 4) return 0; // no pending/error bits
        return _mem.TryGetValue(addr, out var v) ? v : (byte)0;
    }

    public void WriteByte(uint addr, byte value)
    {
        if (Bt != null)
        {
            if (Bt.OwnsRegister(addr)) { Bt.WriteRegByte(addr, value); return; }
            if (addr >= SdioIntStatus && addr < SdioIntStatus + 4) { Bt.ClearSdioInt(value << (int)((addr - SdioIntStatus) * 8)); return; }
        }
        _mem[addr] = value;
        // Taking WLAN-ARM out of reset (RESETCTRL bit0 cleared) is the moment the firmware starts;
        // surface it so the bus layer can flip the F2-ready status the bring-up loop waits on.
        if (addr == WlanArmWrapper + AiResetctrlOffset && (value & AircReset) == 0)
            WlanCoreUp = true;
        // SOCRAM downloads land below the I/O cores (< 0x18000000): count them as firmware bytes.
        if (addr < ChipCommon) BytesDownloaded++;
    }

    /// <summary>Raw byte access to the chip address space (used by the BT shared bus for its rings).</summary>
    public byte MemReadByte(uint addr) => _mem.TryGetValue(addr, out var v) ? v : (byte)0;
    public void MemWriteByte(uint addr, byte value) => _mem[addr] = value;
    public uint MemReadU32(uint addr) =>
        (uint)(MemReadByte(addr) | MemReadByte(addr + 1) << 8 | MemReadByte(addr + 2) << 16 | MemReadByte(addr + 3) << 24);
    public void MemWriteU32(uint addr, uint v)
    { MemWriteByte(addr, (byte)v); MemWriteByte(addr + 1, (byte)(v >> 8)); MemWriteByte(addr + 2, (byte)(v >> 16)); MemWriteByte(addr + 3, (byte)(v >> 24)); }
}

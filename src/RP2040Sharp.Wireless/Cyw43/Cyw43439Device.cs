// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using RP2040.Peripherals.Gpio;
using RP2040Sharp.Wireless.Ble;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// Emulated Infineon CYW43439 Wi-Fi chip, modelled at its host-visible gSPI register/protocol
/// boundary (firmware-version independent). This is the growing root: today it answers the F0 bus
/// layer — the byte-swapped startup handshake (the SPI_READ_TEST_REGISTER → 0xFEEDBEAD probe and the
/// SPI_BUS_CONTROL mode switch). F1 backplane, firmware download and the F2/SDPCM/WLC layers attach
/// on top of the same command decode.
/// </summary>
public sealed class Cyw43439Device
{
    // F0 (bus function) registers — cyw43_spi.h.
    private const uint SPI_BUS_CONTROL = 0x00;
    private const uint SPI_RESPONSE_DELAY = 0x01;
    private const uint SPI_STATUS_ENABLE = 0x02;
    private const uint SPI_INTERRUPT_REGISTER = 0x04; // 16-bit
    private const uint SPI_STATUS_REGISTER = 0x08;
    private const uint SPI_READ_TEST_REGISTER = 0x14;
    private const uint TEST_PATTERN = 0xFEEDBEAD;

    // Interrupt-register bit the SPI poll loop checks for an inbound F2/WLAN packet.
    private const uint F2_PACKET_AVAILABLE_INT = 0x0020;

    /// <summary>SDPCM/WLC transport over the WLAN function (F2). Exposed so the radio/join state
    /// machine (Fase 5) can push async events and inbound Ethernet frames.</summary>
    public readonly Sdpcm Sdpcm = new();

    // SPI status register bits (cyw43_spi.h). The bring-up loop polls SPI_STATUS_REGISTER for
    // F2 (WLAN data channel) readiness after the firmware download + WLAN-core reset; the SDPCM
    // layer later reads F2_PKT_AVAILABLE/LEN here to learn an inbound packet is waiting.
    private const uint STATUS_F2_RX_READY      = 0x00000020;
    private const uint STATUS_F2_PKT_AVAILABLE = 0x00000100;
    private const int  STATUS_F2_PKT_LEN_SHIFT = 9;

    public readonly GSpiSlave Bus;

    private bool _powered;
    // The bus boots in 16-bit, byte-swapped word mode; the driver writes SPI_BUS_CONTROL with
    // WORD_LENGTH_32|ENDIAN_BIG to switch to 32-bit. Both the command and the data are swapped on
    // the wire until then (cyw43_put_swap32 / cyw43_get_swap32).
    private bool _word32;

    private readonly byte[] _f0 = new byte[0x20];

    /// <summary>Diagnostics: every decoded command (write, fn, addr, sz).</summary>
    public Action<bool, int, uint, uint>? OnCommand;

    /// <summary>Diagnostics: a read phase began — (host bytes clocked, response bytes driven).</summary>
    public Action<byte[], byte[]>? OnReadDebug;

    public Cyw43439Device(IoBank0Peripheral io)
    {
        BtBus = new BtSharedBus(_chip, Ble);  // wires the BT shared bus into the backplane
        Bus = new GSpiSlave(io);
        Bus.OnPower = on => _powered = on;
        Bus.OnRead = HandleRead;
        Bus.OnWrite = HandleWrite;
        // An SDPCM packet queued for the host drives the F2-packet-available IRQ (DATA-line host-wake)
        // and the F2 length the status register reports; draining the queue lowers both.
        Sdpcm.OnPacketReadyChanged = (ready, len) => { _f2PacketLen = ready ? len : 0; UpdateHostWake(); };
        // BT shares the same host-wake line: a queued HCI/ACL packet (or the host draining the B2H ring)
        // re-evaluates it, so an idle peripheral is woken to run cyw43_poll exactly like an inbound F2 frame.
        BtBus.OnWorkChanged = UpdateHostWake;
        _f0[SPI_READ_TEST_REGISTER + 0] = (byte)(TEST_PATTERN & 0xFF);
        _f0[SPI_READ_TEST_REGISTER + 1] = (byte)((TEST_PATTERN >> 8) & 0xFF);
        _f0[SPI_READ_TEST_REGISTER + 2] = (byte)((TEST_PATTERN >> 16) & 0xFF);
        _f0[SPI_READ_TEST_REGISTER + 3] = (byte)((TEST_PATTERN >> 24) & 0xFF);
    }

    public bool Powered => _powered;
    public bool Word32 => _word32;

    /// <summary>Bytes the host has downloaded into SOCRAM (firmware/NVRAM/CLM) — accepted and parked.</summary>
    public long FirmwareBytes => _chip.BytesDownloaded;

    /// <summary>Diagnostics: raw F1 control register byte (offset &amp; 0x1F).</summary>
    public byte DebugF1Ctrl(uint off) => _f1ctrl[off & 0x1F];
    /// <summary>Diagnostics: which path each command took.</summary>
    public Action<string>? OnPath;

    // The command is the first 4 host bytes. The driver lays them out with cyw43_put_swap32 (16-bit
    // word swap) until it switches the bus to 32-bit mode, then little-endian. Rather than track the
    // exact switch point across the half-duplex framing, decode whichever byte order yields a valid
    // command (fn ≤ 2, plausible size) — self-correcting across the swap→le32 transition.
    private static (bool write, int fn, uint addr, uint sz) Unpack(uint cmd)
    {
        var write = (cmd & 0x8000_0000u) != 0;
        var fn    = (int)((cmd >> 28) & 0x3);
        var addr  = (cmd >> 11) & 0x1FFFF;
        var sz    = cmd & 0x7FF;
        return (write, fn, addr, sz);
    }

    private bool _pendingWord32 = false;

    private (bool write, int fn, uint addr, uint sz, bool swapped) DecodeCommand(byte[] b)
    {
        // The SPI_BUS_CONTROL write (WORD_LENGTH_32) is the last byte-swapped command; everything after
        // it is little-endian. Latch the switch on the command that follows it.
        if (_pendingWord32) { _word32 = true; _pendingWord32 = false; }

        var swapped = !_word32;
        uint cmd = swapped ? (uint)(b[1] | b[0] << 8 | b[3] << 16 | b[2] << 24)   // swap32 (startup)
                           : (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);  // le32
        var c = Unpack(cmd);
        if (swapped && c.write && c.fn == 0 && c.addr == SPI_BUS_CONTROL) _pendingWord32 = true;
        OnCommand?.Invoke(c.write, c.fn, c.addr, c.sz);
        return (c.write, c.fn, c.addr, c.sz, swapped);
    }

    // ── F1 backplane (SDIO function 1) ──
    // The F1 address map splits at 0x10000: offsets < 0x10000 are windowed accesses into the chip's
    // internal address space (chip_addr = window | (off & 0x7FFF); the 0x8000 "SB access" bit is part
    // of that encoding, NOT a control-register marker), while offsets >= 0x10000 are the SDIO function
    // control registers (the backplane-window bytes, the chip-clock CSR, the watermark, CCCR…).
    private const uint SDIO_BACKPLANE_ADDRESS_LOW = 0x1000A, SDIO_BACKPLANE_ADDRESS_MID = 0x1000B,
                       SDIO_BACKPLANE_ADDRESS_HIGH = 0x1000C;
    private readonly byte[] _f1ctrl = new byte[0x20]; // control regs 0x10000-0x1001F (offset & 0x1F)
    private uint _window;                              // backplane window base (addr & ~0x7FFF)
    private readonly Backplane _chip = new();

    /// <summary>The emulated Bluetooth LE controller (HCI). Wire its <see cref="HciController.Radio"/>
    /// to a <see cref="VirtualBleRadio"/> to advertise/scan/connect against other devices.</summary>
    public readonly HciController Ble = new();
    /// <summary>The Bluetooth HCI-over-gSPI shared-bus transport (routes BT regs/rings in the backplane).</summary>
    public readonly BtSharedBus BtBus;

    private const int BackplaneReadPad = 16; // CYW43_BACKPLANE_READ_PAD_LEN_BYTES (SPI)
    private static bool IsF1Control(uint off) => off >= 0x10000;
    private uint F1ChipAddr(uint off) => _window | (off & 0x7FFF);

    /// <summary>Diagnostics: current backplane window base.</summary>
    public uint Window => _window;
    /// <summary>Diagnostics: whether the WLAN-ARM core has been taken out of reset.</summary>
    public bool DebugWlanCoreUp => _chip.WlanCoreUp;
    /// <summary>Diagnostics: resolved (offset, chipAddr, firstByte) of recent F1 windowed reads.</summary>
    public readonly List<(uint off, uint chip, byte val)> ChipReads = new();

    private const uint SDIO_CHIP_CLOCK_CSR = 0x1000E;
    private const uint SDIO_SLEEP_CSR = 0x1001F;
    private const byte ALP_AVAIL_REQ = 0x08, HT_AVAIL_REQ = 0x10, ALP_AVAIL = 0x40, HT_AVAIL = 0x80, FORCE_HT = 0x02;
    private const byte SLPCSR_KEEP_SDIO_ON = 0x01, SLPCSR_DEVICE_ON = 0x02;

    private byte ReadF1(uint off)
    {
        if (!IsF1Control(off)) return _chip.ReadByte(F1ChipAddr(off));
        if (off == SDIO_SLEEP_CSR)
        {
            // KSO (keep-SDIO-on) handshake: once the host requests KEEP_SDIO_ON, the chip reports
            // both KEEP_SDIO_ON and DEVICE_ON so cyw43_kso_set() sees the device awake.
            var v = _f1ctrl[off & 0x1F];
            if ((v & SLPCSR_KEEP_SDIO_ON) != 0) v |= SLPCSR_DEVICE_ON;
            return v;
        }
        if (off == SDIO_CHIP_CLOCK_CSR)
        {
            // Requested clocks are available instantly: reflect ALP/HT-avail for whatever was requested.
            var v = _f1ctrl[off & 0x1F];
            if ((v & (ALP_AVAIL_REQ | FORCE_HT)) != 0) v |= ALP_AVAIL;
            if ((v & (HT_AVAIL_REQ | FORCE_HT)) != 0) v |= HT_AVAIL;
            // Once the WLAN core is running its firmware brings up the HT (high-throughput) PLL on
            // its own — the bring-up loop polls for HT_AVAIL with no further request bit set, so
            // surface both clocks as settled the moment the core leaves reset.
            if (_chip.WlanCoreUp) v |= ALP_AVAIL | HT_AVAIL;
            return v;
        }
        return _f1ctrl[off & 0x1F];
    }

    /// <summary>Diagnostics: count + last window of F1 windowed (chip-memory) writes.</summary>
    public long WindowedWrites { get; private set; }
    public uint LastWindow { get; private set; }

    private void WriteF1(uint off, byte v)
    {
        if (!IsF1Control(off))
        { WindowedWrites++; LastWindow = _window; _chip.WriteByte(F1ChipAddr(off), v); return; }
        _f1ctrl[off & 0x1F] = v;
        switch (off)
        {
            case SDIO_BACKPLANE_ADDRESS_LOW:  _window = (_window & ~0x0000FF00u) | ((uint)v << 8);  break; // bits[15:8]
            case SDIO_BACKPLANE_ADDRESS_MID:  _window = (_window & ~0x00FF0000u) | ((uint)v << 16); break; // bits[23:16]
            case SDIO_BACKPLANE_ADDRESS_HIGH: _window = (_window & ~0xFF000000u) | ((uint)v << 24); break; // bits[31:24]
        }
        _window &= ~0x7FFFu;
    }

    private byte[] HandleRead(byte[] hostBytes)
    {
        if (hostBytes.Length < 4) return [];
        var (write, fn, addr, sz, swapped) = DecodeCommand(hostBytes);
        // The host releases DATA at the end of every transaction — reads AND writes — so this fires for
        // both. A write carries its data in hostBytes[4..]; apply it here (HandleWrite/FlushWrite only
        // runs for the rare transaction the host never turns the line around on).
        if (write) { ApplyWrite(fn, addr, hostBytes, swapped); UpdateHostWake(); return []; }
        var n = (int)(sz == 0 ? 4 : sz);
        // F1/backplane reads carry a fixed response delay: the chip drives BackplaneReadPad dummy
        // bytes (the host's SPI_RESP_DELAY_F1 turnaround) before the data. F0 reads have no pad.
        var pad = fn == 1 ? BackplaneReadPad : 0;
        var resp = new byte[pad + n];
        if (fn == 0)
        {
            if (addr == SPI_STATUS_REGISTER) WriteF0Status();
            if (addr == SPI_INTERRUPT_REGISTER) WriteF0Interrupt();
            for (var i = 0; i < n; i++) resp[i] = _f0[(addr + (uint)i) & 0x1F];
        }
        else if (fn == 2)
        {
            // WLAN function: hand back the next queued SDPCM packet (no backplane pad on F2).
            var pkt = Sdpcm.HostRead(n);
            Array.Copy(pkt, resp, Math.Min(pkt.Length, resp.Length));
            OnReadDebug?.Invoke(hostBytes, resp);
            return resp;
        }
        else if (fn == 1)
        {
            var data = new byte[n];
            for (var i = 0; i < n; i++) data[i] = ReadF1(addr + (uint)i);
            if (!IsF1Control(addr) && ChipReads.Count < 300)
                ChipReads.Add((addr, F1ChipAddr(addr), data[0]));
            if (swapped) data = Swap32Bytes(data);
            Array.Copy(data, 0, resp, pad, n);
            OnReadDebug?.Invoke(hostBytes, resp);
            return resp;
        }
        if (swapped) resp = Swap32Bytes(resp); // wire byte order mirrors the command
        OnReadDebug?.Invoke(hostBytes, resp);
        return resp;
    }

    /// <summary>Compose the F0 SPI_STATUS_REGISTER word from current chip state, into the F0 file so
    /// the normal read path returns it. F2 reports ready once the WLAN core is running; a queued
    /// inbound SDPCM frame adds F2_PKT_AVAILABLE with its length (Fase 4 attaches the queue).</summary>
    private void WriteF0Status()
    {
        uint status = 0;
        if (_chip.WlanCoreUp) status |= STATUS_F2_RX_READY;
        if (_f2PacketLen > 0) status |= STATUS_F2_PKT_AVAILABLE | ((uint)_f2PacketLen << STATUS_F2_PKT_LEN_SHIFT);
        _f0[SPI_STATUS_REGISTER + 0] = (byte)status;
        _f0[SPI_STATUS_REGISTER + 1] = (byte)(status >> 8);
        _f0[SPI_STATUS_REGISTER + 2] = (byte)(status >> 16);
        _f0[SPI_STATUS_REGISTER + 3] = (byte)(status >> 24);
    }

    /// <summary>Compose the F0 SPI_INTERRUPT_REGISTER (u16): F2_PACKET_AVAILABLE while an SDPCM
    /// packet is queued. The host clears it write-1-to-clear, but it re-asserts as long as the
    /// packet is still pending (level-style), which the poll loop tolerates.</summary>
    private void WriteF0Interrupt()
    {
        uint intr = _f2PacketLen > 0 ? F2_PACKET_AVAILABLE_INT : 0u;
        _f0[SPI_INTERRUPT_REGISTER + 0] = (byte)intr;
        _f0[SPI_INTERRUPT_REGISTER + 1] = (byte)(intr >> 8);
    }

    // Length (bytes) of the next inbound F2/SDPCM packet the host can read, 0 = none. Driven by the
    // SDPCM layer; F2_PKT_AVAILABLE/LEN live in the F0 status register and the int register.
    private int _f2PacketLen;

    // The gSPI host-wake line (GPIO24) is shared by both transports: hold it asserted while either has
    // work pending for the host (an inbound F2 frame or an unread B2H/HCI packet), so an idle guest is
    // woken to poll. Re-evaluated after every transaction (which lowers it once the host drains) and
    // when a packet is queued — but only the *transitions* touch the line, so re-checking it on every
    // gSPI transaction stays cheap and never re-pulses a steady level (which would keep the level-high
    // IRQ firing and spin the guest in cyw43_poll instead of letting it idle).
    private bool _hostWake;
    private void UpdateHostWake()
    {
        var want = _f2PacketLen > 0 || BtBus.HasWork;
        if (want == _hostWake) return;
        _hostWake = want;
        Bus.SetInterrupt(want);
    }

    private void ApplyWrite(int fn, uint addr, byte[] hostBytes, bool swapped)
    {
        OnWriteDebug?.Invoke(fn, addr, hostBytes);
        var data = hostBytes.Length > 4 ? hostBytes[4..] : [];
        if (swapped) data = Swap32Bytes(data);
        if (fn == 0)
            for (var i = 0; i < data.Length; i++) _f0[(addr + (uint)i) & 0x1F] = data[i];
        else if (fn == 1)
            for (var i = 0; i < data.Length; i++) WriteF1(addr + (uint)i, data[i]);
        else if (fn == 2)
            Sdpcm.HostWrite(data);  // WLAN function: an SDPCM packet (ioctl request or Ethernet frame)
    }

    /// <summary>Diagnostics: a decoded write — (fn, addr, raw host bytes).</summary>
    public Action<int, uint, byte[]>? OnWriteDebug;

    private void HandleWrite(byte[] hostBytes)
    {
        if (hostBytes.Length < 4) return;
        var (write, fn, addr, sz, swapped) = DecodeCommand(hostBytes);
        if (write) { ApplyWrite(fn, addr, hostBytes, swapped); UpdateHostWake(); }
    }

    private static byte[] Swap32Bytes(byte[] src)
    {
        var n = (src.Length + 3) & ~3;
        var dst = new byte[n];
        Array.Copy(src, dst, src.Length);
        for (var i = 0; i + 3 < n; i += 4)
            (dst[i], dst[i + 1], dst[i + 2], dst[i + 3]) = (dst[i + 1], dst[i], dst[i + 3], dst[i + 2]);
        return dst;
    }
}

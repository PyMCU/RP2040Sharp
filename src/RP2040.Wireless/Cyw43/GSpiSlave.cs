// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using RP2040.Peripherals.Gpio;

namespace RP2040.Wireless.Cyw43;

/// <summary>
/// Pin-level gSPI slave for the CYW43439 as wired on the Pico 2 W: CLK (GPIO29), DATA (GPIO24,
/// one half-duplex line), CS (GPIO25, active low), WL_REG_ON (GPIO23, power).
///
/// <para><b>Framing.</b> gSPI uses CS only to <i>start</i> a transaction — the host bit-bangs the
/// clock through PIO+DMA and typically deasserts CS again only a few bits in (the CPU runs far
/// ahead of the PIO), so the chip latches on clock edges and counts bits rather than gating on CS.
/// This slave therefore resets on a CS falling edge and then samples <b>every</b> CLK rising edge
/// (regardless of CS level): the first 32 bits are the command word (host drives DATA, MSB-first);
/// when the host flips DATA to an input the slave decodes the command and drives the read response.
/// The wire byte order equals the driver's buffer order (the pico-sdk DMA byte-swap cancels the
/// PIO's MSB-first shift).</para>
/// </summary>
public sealed class GSpiSlave
{
    public const int PinRegOn = 23;
    public const int PinData  = 24;
    public const int PinCs    = 25;
    public const int PinClk   = 29;

    private readonly IoBank0Peripheral _io;

    /// <summary>Decode the read command (the host-driven bytes clocked before DATA went input) and
    /// return the bytes the slave must drive back, MSB-first. Called the instant the host releases DATA.</summary>
    public Func<byte[], byte[]>? OnRead;

    /// <summary>A completed write transaction: the host-driven bytes (command + write data). Fired when a
    /// new transaction starts (CS re-asserts) or the line goes idle.</summary>
    public Action<byte[]>? OnWrite;

    /// <summary>WL_REG_ON transitioned — the chip power/reset gate.</summary>
    public Action<bool>? OnPower;

    private bool _clk;
    private bool _regOn;
    private bool _started;
    private bool _reading;

    // Host-wake / F2-packet IRQ. On the Pico 2 W gSPI the interrupt line IS the DATA pin (GPIO24 =
    // WL_HOST_WAKE), active-high: when a packet is pending and the bus is idle the chip drives DATA
    // high so the host's `read_host_interrupt_pin` poll sees it. We hold this level on DATA after a
    // read response is exhausted (and after a write's empty read phase), which is exactly when the
    // host releases DATA to an input and samples the pin between transactions.
    private bool _irq;
    public void SetInterrupt(bool on)
    {
        _irq = on;
        // Never disturb the DATA line while a gSPI transaction is in flight. During a READ the host has
        // released DATA and the chip is driving the response on it; asserting the host-wake here (e.g. a
        // packet another device enqueues mid-read, common with two boards on one virtual LAN) would
        // overwrite a response bit and corrupt the read. Leave _irq set; EndTransaction re-applies the
        // level once the bus is idle. Real hardware likewise only drives WL_HOST_WAKE between transactions.
        if (_started) return;
        if (HostDrivesData()) return;
        // The cyw43 driver arms a RISING-edge IRQ on the host-wake pin to wake from WFI while it waits
        // for a packet (e.g. a blocking accept()). A previous gSPI read may have left DATA driven high,
        // so simply re-asserting high produces no edge and never wakes the guest. Force a clean
        // low→high transition so the edge actually fires; otherwise the guest only notices the packet
        // on its next unrelated wake-up, which can be many simulated seconds away.
        if (on)
        {
            _io.SetExternalInput(PinData, false);
            _io.SetExternalInput(PinData, true);
        }
        else
        {
            _io.SetExternalInput(PinData, false);
        }
    }

    private readonly List<byte> _host = new(72);
    private int _hostBits;
    private byte _hostByte;

    private byte[] _response = [];
    private int _respIndex;
    private byte _respByte;
    private int _respBitsLeft;

    /// <summary>Diagnostics: (host bytes before read phase, read bits the host clocked, response length).</summary>
    public Action<int, int, int>? OnReadStats;
    private int _readBitsClocked2;
    /// <summary>Diagnostics: read bits the host has clocked in the in-progress/last read phase.</summary>
    public int LastReadBits => _readBitsClocked2;

    public GSpiSlave(IoBank0Peripheral io)
    {
        _io = io;
        _io.PadChanged += OnPad;
        _clk   = _io.GetPadOutputLevel(PinClk);
        _regOn = _io.GetPadOutputLevel(PinRegOn);
    }

    private bool HostDrivesData() => _io.GetPadOutputEnable(PinData);

    private void OnPad(int pin)
    {
        switch (pin)
        {
            case PinRegOn:
                var on = _io.GetPadOutputLevel(PinRegOn);
                if (on != _regOn) { _regOn = on; OnPower?.Invoke(on); }
                break;

            case PinCs:
                if (!_io.GetPadOutputLevel(PinCs)) StartTransaction(); // CS low = asserted = new transaction
                else EndTransaction();                                 // CS high = deasserted = transaction done
                break;

            case PinData:
                // Host released DATA mid-transaction → read phase begins. Decode and present bit 0 now.
                if (_started && !_reading && !HostDrivesData()) BeginReadPhase();
                break;

            case PinClk:
                var clk = _io.GetPadOutputLevel(PinClk);
                if (clk == _clk) break;
                var rising = !_clk && clk;
                _clk = clk;
                if (!_started) break;
                if (rising) OnRising(); else OnFalling();
                break;
        }
    }

    private void StartTransaction()
    {
        if (_started)
        {
            if (_reading) OnReadStats?.Invoke(_lastReadHostBytes, _readBitsClocked2, _response.Length);
            FlushWrite();
        }
        _started = true;
        _reading = false;
        _host.Clear(); _hostBits = 0; _hostByte = 0;
        _response = []; _respIndex = 0; _respBitsLeft = 0;
    }

    private int _lastReadHostBytes;

    /// <summary>The host deasserted CS (transaction over). Flush a trailing write, then — crucially —
    /// hold the host-wake level on DATA (GPIO24) while the bus is idle, so the firmware's host-wake poll
    /// (<c>gpio_get(WL_HOST_WAKE)</c>, e.g. cyw43_ll's SDPCM do_ioctl loop) sees it. PresentNextBit only
    /// drives the held level when a read phase is clocked; a write that ends with no trailing CLK edge
    /// (no read phase) would otherwise leave DATA stale and the F2 SDPCM response would never be read.</summary>
    private void EndTransaction()
    {
        if (_started)
        {
            if (_reading) OnReadStats?.Invoke(_lastReadHostBytes, _readBitsClocked2, _response.Length);
            FlushWrite();
        }
        _started = false;
        _reading = false;
        // Apply the host-wake level now that the bus is idle (the host samples DATA as WL_HOST_WAKE
        // between transactions). No forced edge here: SetInterrupt drives the wake edge when the bus is
        // already idle; doing it again per-transaction injects a spurious DATA pulse between the cyw43
        // KSO retry reads that the host can latch as the register value.
        if (!HostDrivesData()) _io.SetExternalInput(PinData, _irq);
    }

    private void FlushWrite()
    {
        if (!_reading && _host.Count > 0) OnWrite?.Invoke(_host.ToArray());
    }

    /// <summary>Diagnostics: counts rising edges during a read where the host was (wrongly) still
    /// driving DATA (OE on) — i.e. the line never turned around for the chip to respond.</summary>
    public int ReadEdgesHostDriving { get; private set; }

    private void OnRising()
    {
        if (_reading)
        {
            _readBitsClocked2++;
            if (HostDrivesData()) ReadEdgesHostDriving++;
            return;
        }
        if (!HostDrivesData()) return;
        _hostByte = (byte)((_hostByte << 1) | (_io.GetPadOutputLevel(PinData) ? 1 : 0));
        if (++_hostBits == 8) { _host.Add(_hostByte); _hostBits = 0; _hostByte = 0; }
    }

    private void OnFalling()
    {
        if (_reading) PresentNextBit(); // present the next read bit for the host's rising-edge sample
    }

    private void BeginReadPhase()
    {
        _reading = true;
        _lastReadHostBytes = _host.Count; _readBitsClocked2 = 0; BitsDriven = 0;
        _response = OnRead?.Invoke(_host.ToArray()) ?? [];
        _respIndex = 0; _respBitsLeft = 0;
        // The read phase begins on the same instruction that drives CLK low and flips DATA to input
        // (`set pindirs,0 side 0`); the CLK and DATA PadChanged events can arrive in either order.
        // Present bit 0 here only if CLK already fell (DATA event arrived second); otherwise the
        // upcoming CLK falling edge presents it. This drives each bit exactly once per CLK-low period
        // regardless of event order — without it the first bit is sampled twice and the response shifts.
        if (!_clk) PresentNextBit();
    }

    /// <summary>Diagnostics: bits this slave has driven in the current/last read phase.</summary>
    public int BitsDriven { get; private set; }

    private void PresentNextBit()
    {
        BitsDriven++;
        if (_respBitsLeft == 0)
        {
            // Real response bytes while they last; once exhausted, pad with 0x00. The host-wake level is
            // NOT driven here: it belongs to the idle bus (EndTransaction applies it between transactions,
            // which is when the firmware samples DATA as WL_HOST_WAKE). Driving the host-wake 0xFF as a
            // read's trailing fill makes an over-clocked register read (e.g. cyw43 KSO under heavy
            // cross-device packet traffic, when a packet is persistently pending) come back 0xFF — which
            // the driver explicitly rejects (read_value != 0xff) and then logs "kso_set: failed".
            _respByte = _respIndex < _response.Length ? _response[_respIndex++] : (byte)0x00;
            _respBitsLeft = 8;
        }
        var bit = (_respByte & 0x80) != 0;
        _respByte <<= 1;
        _respBitsLeft--;
        _io.SetExternalInput(PinData, bit);
    }
}

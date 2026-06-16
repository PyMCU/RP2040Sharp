using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Usb;

/// <summary>
/// RP2040 USB Controller (USBCTRL) — device mode + CDC enumeration support.
/// Memory map (AHB slot 1, bits [27:20] = 0x01):
///   0x50100000 - 0x50100FFF : DPRAM (4 KB)
///   0x50110000 - 0x50110FFF : Controller registers (USBCTRL_REGS)
///
/// The device-side endpoint FSM is sufficient to drive TinyUSB through enumeration
/// and bulk CDC-ACM transfers. Companion host driver lives in <see cref="UsbCdcHost"/>.
/// Equivalent to rp2040js: src/peripherals/usb.ts (device mode subset).
/// </summary>
public sealed class UsbPeripheral : IMemoryMappedDevice, IHandlesAtomicAliases, ITickable
{
    private const int USB_IRQ = 5;

    // ── DPRAM (4 KB) ──────────────────────────────────────────────────────
    private const uint DPRAM_BASE = 0x50100000u;
    private const uint DPRAM_SIZE = 0x1000u;

    // ── REGS base offset from the AHB slot base ───────────────────────────
    private const uint REGS_OFFSET = 0x10000u;

    // ── DPRAM offsets ─────────────────────────────────────────────────────
    private const uint EP1_IN_CONTROL          = 0x008;
    private const uint EP0_IN_BUFFER_CONTROL   = 0x080;
    private const uint EP0_OUT_BUFFER_CONTROL  = 0x084;
    private const uint EP15_OUT_BUFFER_CONTROL = 0x0FC;
    private const uint EP0_BUFFER              = 0x100;

    // EP buffer-control bits
    private const uint USB_BUF_CTRL_AVAILABLE = 1u << 10;
    private const uint USB_BUF_CTRL_FULL      = 1u << 15;
    private const uint USB_BUF_CTRL_LEN_MASK  = 0x3FFu;

    // INTR bits (subset)
    private const uint INTR_BUFF_STATUS  = 1u << 4;
    private const uint INTR_BUS_RESET    = 1u << 12;
    private const uint INTR_DEV_CONN_DIS = 1u << 13;
    private const uint INTR_DEV_SUSPEND  = 1u << 14;
    private const uint INTR_DEV_RESUME   = 1u << 15;
    private const uint INTR_DEV_SOF      = 1u << 17;
    private const uint INTR_SETUP_REQ    = 1u << 16;

    // SIE_STATUS bits (subset)
    private const uint SIE_VBUS_DETECTED = 1u << 0;
    private const uint SIE_RESUME        = 1u << 11;
    private const uint SIE_CONNECTED     = 1u << 16;
    private const uint SIE_SETUP_REC     = 1u << 17;
    private const uint SIE_BUS_RESET     = 1u << 19;

    // MAIN_CTRL bits
    private const uint MAIN_CTRL_CONTROLLER_EN = 1u << 0;
    private const uint MAIN_CTRL_HOST_NDEVICE  = 1u << 1;

    // ── Register offsets within REGS region ──────────────────────────────
    private const uint R_ADDR_ENDP0     = 0x000;
    private const uint R_MAIN_CTRL      = 0x040;
    private const uint R_SOF_RW         = 0x044;
    private const uint R_SOF_RD         = 0x048;
    private const uint R_SIE_CTRL       = 0x04C;
    private const uint R_SIE_STATUS     = 0x050;
    private const uint R_INT_EP_CTRL    = 0x054;
    private const uint R_BUFF_STATUS    = 0x058;
    private const uint R_BUFF_CPU_SHOULD_HANDLE = 0x05C;
    private const uint R_EP_ABORT       = 0x060;
    private const uint R_EP_ABORT_DONE  = 0x064;
    private const uint R_EP_STALL_ARM   = 0x068;
    private const uint R_NAK_POLL       = 0x06C;
    private const uint R_EP_STATUS_STALL_NAK = 0x070;
    private const uint R_USB_MUXING     = 0x074;
    private const uint R_USB_PWR        = 0x078;
    private const uint R_USBPHY_DIRECT  = 0x07C;
    private const uint R_USBPHY_DIRECT_OVERRIDE = 0x080;
    private const uint R_USBPHY_TRIM    = 0x084;
    private const uint R_INTR           = 0x08C;
    private const uint R_INTE           = 0x090;
    private const uint R_INTF           = 0x094;
    private const uint R_INTS           = 0x098;

    // ── Fields ────────────────────────────────────────────────────────────
    private readonly CortexM0Plus? _cpu;
    private readonly byte[] _dpram = new byte[DPRAM_SIZE];

    private readonly uint[] _addrEndp = new uint[16];

    private uint _mainCtrl;
    private uint _sofRw;
    private uint _sofRd;
    private uint _sieCtrl;
    private uint _sieStatus;
    private uint _intEpCtrl;
    private uint _buffStatus;
    private uint _buffCpuShouldHandle;
    private uint _epAbort;
    private uint _epAbortDone;
    private uint _epStallArm;
    private uint _nakPoll;
    private uint _epStatusStallNak;
    private uint _usbMuxing;
    private uint _usbPwr;
    private uint _usbphyDirect;
    private uint _usbphyDirectOverride;
    private uint _usbphyTrim = 0x04040000u;
    private uint _intr;
    private uint _inte;
    private uint _intf;

    private bool _controllerEnabled;
    private bool _hostMode;
    private bool _prevSieConnected;

    public uint Size => 0x20000u;

    // ── Device-mode callbacks ────────────────────────────────────────────
    /// <summary>Fired the first time the firmware sets MAIN_CTRL.CONTROLLER_EN in device mode.</summary>
    public Action? OnUsbEnabled;
    /// <summary>Fired when firmware acknowledges a bus reset (writes SIE_BUS_RESET as W1C).</summary>
    public Action? OnResetReceived;
    /// <summary>Fired when firmware completes an IN transfer (data flowing device → host).</summary>
    public Action<int, byte[]>? OnEndpointWrite;
    /// <summary>Fired when firmware arms an OUT endpoint (host → device); host should call <see cref="EndpointReadDone"/>.</summary>
    public Action<int, int>? OnEndpointRead;
    /// <summary>Fired on every simulated Start-of-Frame (1 ms intervals when controller enabled in device mode).</summary>
    public Action<uint>? OnSof;

    public UsbPeripheral(CortexM0Plus? cpu = null)
    {
        _cpu = cpu;
    }

    /// <summary>
    /// Reset USB peripheral state (called when RESETS.RESET.USBCTRL is cycled).
    /// Clears all registers and re-arms the controller-enable trigger so that
    /// the next dcd_init() call fires OnUsbEnabled and restarts enumeration.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_dpram);
        Array.Clear(_addrEndp);
        _mainCtrl = 0;
        _sofRw = 0;
        _sieCtrl = 0;
        _sieStatus = 0;
        _intEpCtrl = 0;
        _buffStatus = 0;
        _buffCpuShouldHandle = 0;
        _epAbort = 0;
        _epAbortDone = 0;
        _epStallArm = 0;
        _nakPoll = 0;
        _epStatusStallNak = 0;
        _usbMuxing = 0;
        _usbPwr = 0;
        _usbphyDirect = 0;
        _usbphyDirectOverride = 0;
        _usbphyTrim = 0x04040000u;
        _intr = 0;
        _inte = 0;
        _intf = 0;
        _controllerEnabled = false;
        _hostMode = false;
        _prevSieConnected = false;
        _pendingWrites.Clear();
        _pendingReads.Clear();
        _setupPending = false;
    }

    // ── IMemoryMappedDevice ───────────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        var offset = address & 0x1FFFFu;

        if (offset < DPRAM_SIZE)
            return ReadDpramWord(offset);

        // Strip atomic-alias bits (12–13) — reads always return the base register value.
        var reg = (offset & ~0x3000u) - REGS_OFFSET;
        return reg switch
        {
            var r when r < 0x040 => _addrEndp[r >> 2],

            R_MAIN_CTRL      => _mainCtrl,
            R_SOF_RW         => _sofRw,
            R_SOF_RD         => _sofRd,
            R_SIE_CTRL       => _sieCtrl,
            R_SIE_STATUS     => _sieStatus,
            R_INT_EP_CTRL    => _intEpCtrl,
            R_BUFF_STATUS    => _buffStatus,
            R_BUFF_CPU_SHOULD_HANDLE => _buffCpuShouldHandle,
            R_EP_ABORT       => _epAbort,
            R_EP_ABORT_DONE  => _epAbortDone,
            R_EP_STALL_ARM   => _epStallArm,
            R_NAK_POLL       => _nakPoll,
            R_EP_STATUS_STALL_NAK => _epStatusStallNak,
            R_USB_MUXING     => _usbMuxing,
            R_USB_PWR        => _usbPwr,
            R_USBPHY_DIRECT  => _usbphyDirect,
            R_USBPHY_DIRECT_OVERRIDE => _usbphyDirectOverride,
            R_USBPHY_TRIM    => _usbphyTrim,
            R_INTR           => _intr,
            R_INTE           => _inte,
            R_INTF           => _intf,
            R_INTS           => (_intr | _intf) & _inte,
            _                => 0u,
        };
    }

    public ushort ReadHalfWord(uint address)
    {
        var offset = address & 0x1FFFFu;
        if (offset < DPRAM_SIZE)
        {
            var aligned = offset & ~1u;
            var shift = (int)(offset & 1) << 3;
            return (ushort)(ReadDpramWord(aligned & ~3u) >> shift);
        }
        return (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));
    }

    public byte ReadByte(uint address)
    {
        var offset = address & 0x1FFFFu;
        if (offset < DPRAM_SIZE)
            return _dpram[offset];
        return (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));
    }

    public void WriteWord(uint address, uint value)
    {
        var offset = address & 0x1FFFFu;

        if (offset < DPRAM_SIZE)
        {
            WriteDpramWord(offset & ~3u, value);
            if (offset >= EP0_IN_BUFFER_CONTROL && offset <= EP15_OUT_BUFFER_CONTROL)
                DpramUpdated(offset & ~3u, value);
            return;
        }

        // Extract and strip atomic-alias type (bits 12–13) from the offset.
        // UsbPeripheral implements IHandlesAtomicAliases, so the AHB bridge passes the
        // raw firmware value. We must apply the transform ourselves for R/W registers,
        // while W1C registers treat `value` as the bitmask of bits to clear regardless
        // of atomic type (both direct writes and atomic-clear alias use the same mask).
        var atomicType = (offset >> 12) & 0x3u;
        offset &= ~0x3000u;  // strip alias bits to get the base register offset

        var reg = offset - REGS_OFFSET;
        switch (reg)
        {
            case var r when r < 0x040:
                // R/W — apply atomic transform
                _addrEndp[r >> 2] = ApplyAtomic(_addrEndp[r >> 2], value, atomicType) & (0x07FF_0000u | 0xFFu);
                break;

            case R_MAIN_CTRL:
                {
                    var v = ApplyAtomic(_mainCtrl, value, atomicType) & 0xC0000003u;
                    _mainCtrl = v;
                    _hostMode = (v & MAIN_CTRL_HOST_NDEVICE) != 0;
                    if ((v & MAIN_CTRL_CONTROLLER_EN) != 0 && !_controllerEnabled)
                    {
                        _controllerEnabled = true;
                        if (!_hostMode) OnUsbEnabled?.Invoke();
                    }
                }
                break;
            case R_SOF_RW:         _sofRw    = ApplyAtomic(_sofRw, value, atomicType) & 0x7FFu; break;
            case R_SIE_CTRL:       _sieCtrl  = ApplyAtomic(_sieCtrl, value, atomicType); break;

            case R_SIE_STATUS:
                {
                    // W1C register: `value` is the raw firmware-written bitmask of bits to clear.
                    // Both direct writes and atomic-clear alias use the same semantics here.
                    var clearMask = value;
                    if ((clearMask & SIE_BUS_RESET) != 0 && !_hostMode)
                        OnResetReceived?.Invoke();
                    _sieStatus &= ~clearMask;
                    SieStatusUpdated();
                }
                break;

            case R_INT_EP_CTRL:    _intEpCtrl = ApplyAtomic(_intEpCtrl, value, atomicType); break;
            // W1C registers — value is always the bitmask of bits to clear.
            case R_BUFF_STATUS:    OnUsbEvent?.Invoke($"BS-CLR clr=0x{value:X} bs=0x{_buffStatus:X}->0x{_buffStatus & ~value:X}"); _buffStatus &= ~value; BuffStatusUpdated(); break;
            case R_BUFF_CPU_SHOULD_HANDLE: _buffCpuShouldHandle &= ~value; break;
            case R_EP_ABORT:
                {
                    var v = ApplyAtomic(_epAbort, value, atomicType);
                    _epAbort = v; _epAbortDone |= v;
                    OnUsbEvent?.Invoke($"EP_ABORT v=0x{v:X}");
                    // Aborting an endpoint cancels its in-flight transfer AND any pending buffer-status
                    // completion (EP_ABORT and BUFF_STATUS share the same per-buffer bit layout). Without
                    // dropping the BUFF_STATUS bit here, a completion the firmware already aborted (e.g.
                    // EP0-IN aborted by reset_ep0 when a new SETUP arrives) is still delivered to TinyUSB,
                    // which then panics "Can't continue xfer on inactive ep" — the Pico W boot blocker.
                    _buffStatus &= ~v;
                    // Also drop any deferred completion still queued for the aborted buffer(s), so a
                    // completion the firmware already cancelled can't fire later and re-raise
                    // BUFF_STATUS on an inactive endpoint. _pendingReads/_pendingWrites are keyed by the
                    // same per-buffer bit (OUT = endpoint*2+1, IN = endpoint*2).
                    for (var b = 0; b < 32; b++)
                        if ((v & (1u << b)) != 0) { _pendingReads.Remove(b); _pendingWrites.Remove(b); }
                    BuffStatusUpdated();
                }
                break;
            case R_EP_ABORT_DONE:  _epAbortDone &= ~value; break;  // W1C
            case R_EP_STALL_ARM:   _epStallArm  = ApplyAtomic(_epStallArm, value, atomicType); break;
            case R_NAK_POLL:       _nakPoll     = ApplyAtomic(_nakPoll, value, atomicType); break;
            case R_EP_STATUS_STALL_NAK: _epStatusStallNak &= ~value; break;  // W1C
            case R_USB_MUXING:
                {
                    var v = ApplyAtomic(_usbMuxing, value, atomicType);
                    _usbMuxing = v;
                    // pico-sdk hw_enumeration_fix waits for SIE_CONNECTED after rerouting muxing
                    if ((v & 0b0100) != 0 && (v & 0b0001) == 0)
                        _sieStatus |= SIE_CONNECTED;
                }
                break;
            case R_USB_PWR:
                {
                    var v = ApplyAtomic(_usbPwr, value, atomicType);
                    _usbPwr = v;
                    // VBUS detect override
                    if ((v & (1u << 2)) != 0)
                    {
                        if ((v & (1u << 3)) != 0) _sieStatus |= SIE_VBUS_DETECTED;
                        else                       _sieStatus &= ~SIE_VBUS_DETECTED;
                    }
                }
                break;
            case R_USBPHY_DIRECT:          _usbphyDirect = ApplyAtomic(_usbphyDirect, value, atomicType); break;
            case R_USBPHY_DIRECT_OVERRIDE:  _usbphyDirectOverride = ApplyAtomic(_usbphyDirectOverride, value, atomicType); break;
            case R_USBPHY_TRIM:            _usbphyTrim = ApplyAtomic(_usbphyTrim, value, atomicType); break;
            case R_INTR:           _intr &= ~value; CheckInterrupts(); break;  // W1C
            case R_INTE:           _inte = ApplyAtomic(_inte, value, atomicType); CheckInterrupts(); break;
            case R_INTF:           _intf = ApplyAtomic(_intf, value, atomicType); CheckInterrupts(); break;
        }
    }

    private static uint ApplyAtomic(uint current, uint value, uint atomicType) => atomicType switch
    {
        1u => current ^ value,  // XOR
        2u => current | value,  // SET
        3u => current & ~value, // CLR
        _  => value,            // normal write
    };

    public void WriteHalfWord(uint address, ushort value)
    {
        var offset = address & 0x1FFFFu;
        if (offset < DPRAM_SIZE)
        {
            _dpram[offset & ~1u]     = (byte)(value & 0xFF);
            _dpram[(offset & ~1u)+1] = (byte)(value >> 8);
            // EP buffer-control writes can be 16-bit too; route through the 32-bit path
            var alignedW = offset & ~3u;
            DpramUpdated(alignedW, ReadDpramWord(alignedW));
            return;
        }
        var aligned = address & ~3u;
        var shift   = (int)((address & 2) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var offset = address & 0x1FFFFu;
        if (offset < DPRAM_SIZE)
        {
            _dpram[offset] = value;
            var alignedW = offset & ~3u;
            DpramUpdated(alignedW, ReadDpramWord(alignedW));
            return;
        }
        var aligned = address & ~3u;
        var shift   = (int)((address & 3) << 3);
        var current = ReadWord(aligned);
        WriteWord(aligned, (current & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    // ── Public host-side helpers ─────────────────────────────────────────

    /// <summary>Simulate a bus reset received by the device. Sets BUS_RESET in SIE_STATUS.</summary>
    public void SignalBusReset()
    {
        _sieStatus |= SIE_BUS_RESET | SIE_CONNECTED;
        SieStatusUpdated();
    }

    /// <summary>
    /// Signal a host-initiated resume to the device. Sets SIE_STATUS.RESUME which maps to
    /// INTR.DEV_RESUME_FROM_HOST, clearing _usbd_dev.suspended in TinyUSB.
    /// </summary>
    public void SignalResume()
    {
        _sieStatus |= SIE_RESUME;
        SieStatusUpdated();
    }

    /// <summary>
    /// Signal a USB Start-of-Frame. Sets INTR.DEV_SOF directly (not via SIE_STATUS).
    /// Used to decrement TinyUSB's cdc_connected_flush_delay counter.
    /// </summary>
    public void SignalSof(uint frameNumber)
    {
        // SOF frame number is in SOF_RD register (R_SOF_RD offset 0x048)
        _sofRd = frameNumber & 0x7FF;
        _intr |= INTR_DEV_SOF;
        CheckInterrupts();
        OnSof?.Invoke(frameNumber);
    }

    /// <summary>Simulate an isolated SETUP_REC bit assertion (no payload).</summary>
    public void SignalSetupPacket()
    {
        _sieStatus |= SIE_SETUP_REC;
        SieStatusUpdated();
    }

    /// <summary>Inject an 8-byte SETUP packet into DPRAM[0..8] and raise SETUP_REC.</summary>
    public void SendSetupPacket(ReadOnlySpan<byte> setup)
    {
        if (setup.Length != 8) throw new ArgumentException("SETUP packet must be 8 bytes", nameof(setup));
        OnUsbEvent?.Invoke($"SETUP [{Convert.ToHexString(setup)}]");
        setup.CopyTo(_dpram.AsSpan(0, 8));
        // Raise SETUP_REC on the NEXT Tick, not now. A new SETUP triggers reset_ep0 in the device, which
        // aborts EP0 transfers (ep->active=0). If it lands while the device is still completing the prior
        // control transfer's status stage (mid hw_handle_buff_status, between the BUFF_STATUS clear and
        // hw_endpoint_xfer_continue), the in-flight EP0-IN completion then runs with active=0 and panics
        // "Can't continue xfer on inactive ep". On real USB the host wouldn't start a new control transfer
        // until the device ACKs the status; deferring one quantum gives the device that window.
        _setupPending = true;
    }

    private bool _setupPending;

    /// <summary>Provide data for an OUT endpoint that the firmware previously armed.</summary>
    public void EndpointReadDone(int endpoint, ReadOnlySpan<byte> data)
    {
        // Defer DPRAM write + IndicateBufferReady to Tick() after READ_DELAY_CYCLES.
        // Matches rp2040js endpointReadAlarms: without this delay, TinyUSB immediately re-arms
        // the endpoint from within the BUFF_STATUS ISR, creating a tight loop that eventually
        // fires BUFF_STATUS with ep->active=false and panics.
        var buffBit = endpoint * 2 + 1; // OUT = odd bit
        if (!_pendingReads.TryGetValue(buffBit, out var q))
            _pendingReads[buffBit] = q = new Queue<(int, byte[], long)>();
        q.Enqueue((endpoint, data.ToArray(), _totalCycles + READ_DELAY_CYCLES));
    }

    /// <summary>Copy <paramref name="data"/> into DPRAM at <paramref name="dpramOffset"/>.</summary>
    public void WriteDpram(uint dpramOffset, ReadOnlySpan<byte> data)
    {
        if (dpramOffset + data.Length > DPRAM_SIZE)
            throw new ArgumentOutOfRangeException(nameof(dpramOffset));
        data.CopyTo(_dpram.AsSpan((int)dpramOffset));
    }

    /// <summary>Read <paramref name="length"/> bytes from DPRAM at <paramref name="dpramOffset"/>.</summary>
    public byte[] ReadDpram(uint dpramOffset, int length)
    {
        var result = new byte[length];
        _dpram.AsSpan((int)dpramOffset, length).CopyTo(result);
        return result;
    }

    // Pending IN-transfer completions: keyed by BUFF_STATUS bit, queued to preserve ordering
    // when firmware sends multiple packets in the same CPU batch (e.g. 64-byte + 11-byte for a
    // 75-byte descriptor). We defer OnEndpointWrite until Tick() delivers after a short delay,
    // mimicking rp2040js's writeDelayMicroseconds alarm mechanism.
    private readonly Dictionary<int, Queue<(int endpoint, byte[] buffer, long deliverAt)>> _pendingWrites = new();
    private const long WRITE_DELAY_CYCLES = 625L;  // ~5 µs at 125 MHz, matching rp2040js default

    // Pending OUT-transfer completions: deferred to Tick() so IndicateBufferReady fires after
    // a delay rather than synchronously inside DpramUpdated. Matches rp2040js's readDelayMicroseconds
    // alarm, which prevents a tight re-arm loop that would cause ep->active=false panics in TinyUSB.
    private readonly Dictionary<int, Queue<(int endpoint, byte[] buffer, long deliverAt)>> _pendingReads = new();
    private const long READ_DELAY_CYCLES = 625L;  // ~5 µs at 125 MHz, matching rp2040js default

    private long _totalCycles;

    // ── DPRAM endpoint FSM ───────────────────────────────────────────────

    private void DpramUpdated(uint offset, uint value)
    {
        if (_hostMode) return;
        if ((value & USB_BUF_CTRL_AVAILABLE) == 0) return;
        if (offset < EP0_IN_BUFFER_CONTROL || offset > EP15_OUT_BUFFER_CONTROL) return;

        var endpoint = (int)((offset - EP0_IN_BUFFER_CONTROL) >> 3);
        var isOut = (offset & 4) != 0;
        var bufLen = (int)(value & USB_BUF_CTRL_LEN_MASK);
        var bufferOffset = GetEndpointBufferOffset(endpoint, isOut);
        OnUsbEvent?.Invoke($"ARM ep{endpoint}{(isOut ? "O" : "I")} len{bufLen} full{(value & USB_BUF_CTRL_FULL) != 0}");

        // Consume AVAILABLE flag
        value &= ~USB_BUF_CTRL_AVAILABLE;

        if (isOut)
        {
            WriteDpramWord(offset, value);
            OnEndpointRead?.Invoke(endpoint, bufLen);
        }
        else
        {
            // IN: data flows device → host. Capture buffer, clear FULL, indicate ready.
            value &= ~USB_BUF_CTRL_FULL;
            WriteDpramWord(offset, value);
            var buffer = new byte[bufLen];
            _dpram.AsSpan((int)bufferOffset, bufLen).CopyTo(buffer);
            // Store pending write to be delivered by Tick() after WRITE_DELAY_CYCLES.
            // This ensures the firmware ISR returns before the host responds with next SETUP.
            var buffBit = endpoint * 2; // IN = even bit
            if (!_pendingWrites.TryGetValue(buffBit, out var q))
                _pendingWrites[buffBit] = q = new Queue<(int, byte[], long)>();
            q.Enqueue((endpoint, buffer, _totalCycles + WRITE_DELAY_CYCLES));
            IndicateBufferReady(endpoint, isOut: false);
        }
    }

    private uint GetEndpointBufferOffset(int endpoint, bool out_)
    {
        if (endpoint == 0) return EP0_BUFFER;
        var ctrlOffset = EP1_IN_CONTROL + 8u * (uint)(endpoint - 1) + (out_ ? 4u : 0u);
        return ReadDpramWord(ctrlOffset) & 0xFFC0u;
    }

    /// <summary>Diagnostic: a BUFF_STATUS bit was set for (endpoint, isOut).</summary>
    public Action<int, bool>? OnBuffStatusDiag;
    /// <summary>Diagnostic: a textual USB event (SETUP/ARM/BS) for boot tracing.</summary>
    public Action<string>? OnUsbEvent;

    private void IndicateBufferReady(int endpoint, bool isOut)
    {
        OnBuffStatusDiag?.Invoke(endpoint, isOut);
        OnUsbEvent?.Invoke($"BS ep{endpoint}{(isOut ? "O" : "I")}");
        _buffStatus |= 1u << (endpoint * 2 + (isOut ? 1 : 0));
        BuffStatusUpdated();
    }

    private void BuffStatusUpdated()
    {
        if (_buffStatus != 0) _intr |= INTR_BUFF_STATUS;
        else                  _intr &= ~INTR_BUFF_STATUS;
        CheckInterrupts();
    }

    private void SieStatusUpdated()
    {
        // Map SIE_STATUS bits to INTR bits (device-mode subset).
        SyncIntrBit(SIE_SETUP_REC, INTR_SETUP_REQ);
        SyncIntrBit(SIE_BUS_RESET, INTR_BUS_RESET);
        // DEV_CONN_DIS is edge-triggered: pulse INTR only on the 0→1 transition of SIE_CONNECTED.
        // A level mapping causes the bit to re-assert after every W1C, creating an IRQ storm.
        var sieConnected = (_sieStatus & SIE_CONNECTED) != 0;
        if (sieConnected && !_prevSieConnected)
            _intr |= INTR_DEV_CONN_DIS;
        _prevSieConnected = sieConnected;
        SyncIntrBit(SIE_RESUME, INTR_DEV_RESUME);
        CheckInterrupts();
    }

    private void SyncIntrBit(uint sieBit, uint intrBit)
    {
        if ((_sieStatus & sieBit) != 0) _intr |= intrBit;
        else                            _intr &= ~intrBit;
    }

    private uint ReadDpramWord(uint byteOffset)
    {
        var i = (int)(byteOffset & ~3u);
        return (uint)(_dpram[i] | (_dpram[i+1] << 8) | (_dpram[i+2] << 16) | (_dpram[i+3] << 24));
    }

    private void WriteDpramWord(uint byteOffset, uint value)
    {
        var i = (int)(byteOffset & ~3u);
        _dpram[i]   = (byte) value;
        _dpram[i+1] = (byte)(value >>  8);
        _dpram[i+2] = (byte)(value >> 16);
        _dpram[i+3] = (byte)(value >> 24);
    }

    private void CheckInterrupts()
    {
        if (_cpu == null) return;
        var pending = ((_intr | _intf) & _inte) != 0;
        _cpu.SetInterrupt(USB_IRQ, pending);
    }

    /// <summary>
    /// Re-check interrupt state (called after NVIC_ICPR cleared the pending bit
    /// so that level-triggered USB IRQ can re-assert via NVIC_ISER enable).
    /// </summary>
    public void RecheckInterrupts() => CheckInterrupts();

    private const long SOF_PERIOD_CYCLES = 125_000L; // 1ms at 125 MHz (USB full-speed SOF rate)
    private long _lastSofCycles = long.MinValue / 2;
    private uint _sofFrameCount;

    /// <summary>Advance cycle counter; deliver any pending IN-transfer callbacks whose delay has elapsed.</summary>
    public void Tick(long deltaCycles)
    {
        _totalCycles += deltaCycles;

        if (_setupPending)   // deferred SETUP_REC: see SendSetupPacket
        {
            _setupPending = false;
            _sieStatus |= SIE_SETUP_REC;
            SieStatusUpdated();
        }

        // Auto-clear SOF bit after one tick so it doesn't re-trigger indefinitely.
        _intr &= ~INTR_DEV_SOF;

        // Emit periodic SOF when USB controller is enabled in device mode.
        // The host generates SOFs unconditionally regardless of whether the device has the SOF interrupt enabled.
        if (_controllerEnabled && !_hostMode)
        {
            if (_totalCycles - _lastSofCycles >= SOF_PERIOD_CYCLES)
            {
                _lastSofCycles = _totalCycles;
                _sofFrameCount = (_sofFrameCount + 1) & 0x7FF;
                SignalSof(_sofFrameCount);
            }
        }

        if (_pendingWrites.Count == 0 && _pendingReads.Count == 0) return;

        foreach (var (bit, queue) in _pendingWrites.ToArray())
        {
            while (queue.Count > 0 && _totalCycles >= queue.Peek().deliverAt)
            {
                var (ep, buf, _) = queue.Dequeue();
                if (queue.Count == 0) _pendingWrites.Remove(bit);
                OnEndpointWrite?.Invoke(ep, buf);
            }
        }

        foreach (var (bit, queue) in _pendingReads.ToArray())
        {
            while (queue.Count > 0 && _totalCycles >= queue.Peek().deliverAt)
            {
                var (ep, buf, _) = queue.Dequeue();
                if (queue.Count == 0) _pendingReads.Remove(bit);
                var bufCtrlReg = EP0_OUT_BUFFER_CONTROL + (uint)ep * 8;
                var bufCtrl = ReadDpramWord(bufCtrlReg);
                var requestedLen = (int)(bufCtrl & USB_BUF_CTRL_LEN_MASK);
                var newLen = Math.Min(buf.Length, requestedLen);
                var bufferOffset = GetEndpointBufferOffset(ep, out_: true);
                buf.AsSpan(0, newLen).CopyTo(_dpram.AsSpan((int)bufferOffset, newLen));
                bufCtrl |= USB_BUF_CTRL_FULL;
                bufCtrl = (bufCtrl & ~USB_BUF_CTRL_LEN_MASK) | ((uint)newLen & USB_BUF_CTRL_LEN_MASK);
                WriteDpramWord(bufCtrlReg, bufCtrl);
                IndicateBufferReady(ep, isOut: true);
            }
        }
    }
}


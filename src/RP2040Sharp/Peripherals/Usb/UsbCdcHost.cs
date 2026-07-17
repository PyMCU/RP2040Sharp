// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
namespace RP2040.Peripherals.Usb;

/// <summary>
/// Minimal host-side driver that walks an RP2040 device through USB
/// enumeration and CDC-ACM activation.  Equivalent to rp2040js
/// src/usb/cdc.ts (USBCDC). Once the device is configured, bytes pushed
/// via <see cref="SendSerialByte"/> are delivered to the device's bulk-OUT
/// endpoint, and bytes the firmware writes to the bulk-IN endpoint are
/// surfaced through <see cref="OnSerialData"/>.
///
/// <see cref="OnConfigurationComplete"/> is fired once SET_CONFIGURATION is
/// acknowledged.
/// </summary>
public sealed class UsbCdcHost
{
    private const byte CDC_REQUEST_SET_CONTROL_LINE_STATE = 0x22;
    private const byte CDC_DTR = 1 << 0;
    private const byte CDC_RTS = 1 << 1;
    private const byte CDC_DATA_CLASS = 10;
    private const byte CDC_COMM_CLASS  = 2; // CDC communications (control) interface
    private const byte ENDPOINT_BULK   = 2;

    private const int ENDPOINT_ZERO = 0;
    private const int CONFIGURATION_DESCRIPTOR_SIZE = 9;
    private const int TX_FIFO_SIZE = 512;

    private enum DataDirection : byte { HostToDevice = 0, DeviceToHost = 1 }
    private enum SetupType : byte { Standard = 0, Class = 1, Vendor = 2 }
    private enum SetupRecipient : byte { Device = 0, Interface = 1, Endpoint = 2 }
    private enum SetupRequest : byte
    {
        SetAddress = 5,
        GetDescriptor = 6,
        SetDeviceConfiguration = 9,
    }
    private enum DescriptorType : byte
    {
        Device = 1,
        Configuration = 2,
        Interface = 4,
        Endpoint = 5,
    }

    private readonly UsbPeripheral _usb;
    private readonly Queue<byte> _txFifo = new(TX_FIFO_SIZE);

    private bool _initialized;
    private bool _resumeSignaled;
    private int? _descriptorsSize;
    private readonly List<byte> _descriptors = new();
    private int _inEndpoint  = -1;
    private int _outEndpoint = -1;

    /// <summary>Raised whenever the device transmits bytes on the CDC bulk-IN endpoint.</summary>
    public Action<byte[]>? OnSerialData;
    /// <summary>Raised once the host has issued SET_CONTROL_LINE_STATE (device is "open").</summary>
    public Action? OnDeviceConnected;
    /// <summary>Raised after SET_CONFIGURATION is acknowledged and CDC line-state is set.</summary>
    public Action? OnConfigurationComplete;

    /// <summary>The underlying USB peripheral.</summary>
    public UsbPeripheral Usb => _usb;

    public bool IsConnected => _initialized;
    public int InEndpoint  => _inEndpoint;
    public int OutEndpoint => _outEndpoint;
    public int TxFifoCount => _txFifo.Count;

    public UsbCdcHost(UsbPeripheral usb)
    {
        _usb = usb;
        _usb.OnUsbEnabled     += HandleUsbEnabled;
        _usb.OnResetReceived  += HandleResetReceived;
        _usb.OnEndpointWrite  += HandleEndpointWrite;
        _usb.OnEndpointRead   += HandleEndpointRead;
        _usb.OnSof            += HandleSof;
    }

    // The device's bulk-OUT endpoint stays armed (active) while it waits for host data. We remember
    // that pending arm here and fulfil it the instant bytes become available, mirroring real USB:
    // a host with nothing to send simply doesn't complete the OUT transfer. The previous behaviour —
    // fabricating a zero-length completion on every empty arm — created a BUFF_STATUS re-arm busy-loop
    // that, when the device was busy (e.g. during Wi-Fi association), raced TinyUSB into a
    // "Can't continue xfer on inactive ep" panic (hw_endpoint_xfer_continue with ep->active == 0).
    private int _armedOutEp = -1;
    private int _armedOutSize;

    /// <summary>Queue a byte to be delivered to the device on the next bulk-OUT poll.</summary>
    public void SendSerialByte(byte data) { _txFifo.Enqueue(data); FlushPendingOut(); }

    public void SendSerialBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _txFifo.Enqueue(b);
        FlushPendingOut();
    }

    private void FlushPendingOut()
    {
        if (_armedOutEp < 0 || _txFifo.Count == 0) return;
        var ep = _armedOutEp; var size = _armedOutSize;
        _armedOutEp = -1;
        DeliverOut(ep, size);
    }

    private void DeliverOut(int endpoint, int size)
    {
        var n = Math.Min(size, _txFifo.Count);
        var buffer = new byte[n];
        for (var i = 0; i < n; i++) buffer[i] = _txFifo.Dequeue();
        _usb.EndpointReadDone(endpoint, buffer);
    }

    private void HandleUsbEnabled() => _usb.SignalBusReset();

    private void HandleResetReceived()
    {
        _resumeSignaled = false;
        _usb.SendSetupPacket(SetDeviceAddressPacket(1));
    }

    private void HandleEndpointWrite(int endpoint, byte[] buffer)
    {
        if (endpoint == ENDPOINT_ZERO && buffer.Length == 0)
        {
            if (_descriptorsSize == null)
            {
                _usb.SendSetupPacket(GetDescriptorPacket(DescriptorType.Configuration, CONFIGURATION_DESCRIPTOR_SIZE));
            }
            else if (!_initialized)
            {
                // Assert DTR/RTS on every CDC control interface so the firmware sees a terminal as
                // "connected". MicroPython has one CDC (interface 0); CircuitPython is a composite with
                // its console CDC potentially at a different interface — without DTR there, CircuitPython
                // gates code.py's stdout and nothing reaches the Serial Monitor.
                var controlIfaces = ExtractCdcControlInterfaces(_descriptors);
                if (controlIfaces.Count == 0)
                    CdcSetControlLineState();
                else
                    foreach (var ifn in controlIfaces)
                        CdcSetControlLineState(interfaceNumber: (ushort)ifn);
                OnDeviceConnected?.Invoke();
                OnConfigurationComplete?.Invoke();
                // Trigger MicroPython REPL prompt (mirrors rp2040js micropython-run.ts onDeviceConnected)
                SendSerialByte((byte)'\r');
                SendSerialByte((byte)'\n');
            }
            else if (!_resumeSignaled)
            {
                // STATUS ACK for SET_CONTROL_LINE_STATE — signal resume so TinyUSB clears _usbd_dev.suspended.
                _resumeSignaled = true;
                _usb.SignalResume();
            }
            return;
        }

        if (endpoint == ENDPOINT_ZERO && buffer.Length > 1)
        {
            if (buffer.Length == CONFIGURATION_DESCRIPTOR_SIZE
                && buffer[1] == (byte)DescriptorType.Configuration
                && _descriptorsSize == null)
            {
                _descriptorsSize = (buffer[3] << 8) | buffer[2];
                _usb.SendSetupPacket(GetDescriptorPacket(DescriptorType.Configuration, _descriptorsSize.Value));
            }
            else if (_descriptorsSize != null && _descriptors.Count < _descriptorsSize)
            {
                _descriptors.AddRange(buffer);
            }

            if (_descriptorsSize == _descriptors.Count)
            {
                ExtractEndpointNumbers(_descriptors, out _inEndpoint, out _outEndpoint);
                _usb.SendSetupPacket(SetDeviceConfigurationPacket(1));
            }
            return;
        }

        if (endpoint == _inEndpoint && buffer.Length > 0)
            OnSerialData?.Invoke(buffer);
    }

    private void HandleSof(uint frameNumber)
    {
        // SOF fires every 1 ms; periodically re-signal resume (every ~128 ms) so TinyUSB
        // stays awake if it somehow suspended after the initial RESUME handshake.
        if (_initialized && (frameNumber & 0x7F) == 0)
            _usb.SignalResume();
    }

    private void HandleEndpointRead(int endpoint, int size)
    {
        if (endpoint != _outEndpoint) return;
        if (_txFifo.Count == 0)
        {
            // No data ready — leave the OUT endpoint armed (the device keeps ep->active = 1 and waits,
            // exactly as hardware waits for the host to send an OUT token). Deliver the moment bytes
            // arrive (FlushPendingOut). Do NOT fabricate a zero-length completion: that re-arm loop
            // panics TinyUSB on a busy device (see _armedOutEp note above).
            _armedOutEp = endpoint; _armedOutSize = size;
            return;
        }
        DeliverOut(endpoint, size);
    }

    private void CdcSetControlLineState(ushort value = CDC_DTR | CDC_RTS, ushort interfaceNumber = 0)
    {
        // bmRequestType = 0x21: HostToDevice | Class | Interface (not Device)
        _usb.SendSetupPacket(CreateSetupPacket(
            DataDirection.HostToDevice, SetupType.Class, SetupRecipient.Interface,
            CDC_REQUEST_SET_CONTROL_LINE_STATE, value, interfaceNumber, 0));
        _initialized = true;
    }

    // ── Descriptor parsing ───────────────────────────────────────────────

    /// <summary>
    /// Scans the configuration descriptor blob for the CDC data interface and returns its
    /// bulk IN/OUT endpoint numbers.  Each output is -1 when no CDC data endpoint is found.
    /// </summary>
    /// <summary>Returns the bInterfaceNumber of every CDC communications (control) interface in the
    /// configuration descriptor, so DTR/RTS can be asserted on each — needed for composite devices
    /// (CircuitPython) whose console CDC is not interface 0.</summary>
    public static List<int> ExtractCdcControlInterfaces(IReadOnlyList<byte> descriptors)
    {
        var result = new List<int>();
        var index = 0;
        while (index < descriptors.Count)
        {
            var len = descriptors[index];
            if (len < 2 || index + len > descriptors.Count) break;
            if (descriptors[index + 1] == (byte)DescriptorType.Interface && len >= 9
                && descriptors[index + 5] == CDC_COMM_CLASS)
            {
                var ifn = descriptors[index + 2]; // bInterfaceNumber
                if (!result.Contains (ifn)) result.Add (ifn);
            }
            index += len;
        }
        return result;
    }

    public static void ExtractEndpointNumbers(IReadOnlyList<byte> descriptors, out int inEp, out int outEp)
    {
        inEp = outEp = -1;
        var index    = 0;
        var curClass = -1;
        while (index < descriptors.Count)
        {
            var len = descriptors[index];
            if (len < 2 || index + len > descriptors.Count) break;
            var type = descriptors[index + 1];

            if (type == (byte)DescriptorType.Interface && len >= 9)
                curClass = descriptors[index + 5];

            if (type == (byte)DescriptorType.Endpoint && len == 7)
            {
                var addr   = descriptors[index + 2];
                var attr   = descriptors[index + 3];
                var isIn   = (addr & 0x80) != 0;
                var epNum  = addr & 0x0F;
                var isBulk = (attr & 0x03) == ENDPOINT_BULK;
                if (curClass == CDC_DATA_CLASS && isBulk)
                {
                    if (isIn) inEp = epNum; else outEp = epNum;
                }
            }
            index += len;
        }
    }

    // ── SETUP packet helpers ─────────────────────────────────────────────

    private static byte[] CreateSetupPacket(
        DataDirection dir, SetupType type, SetupRecipient recipient,
        byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
    {
        var p = new byte[8];
        p[0] = (byte)(((byte)dir << 7) | ((byte)type << 5) | (byte)recipient);
        p[1] = bRequest;
        p[2] = (byte)(wValue & 0xFF);
        p[3] = (byte)(wValue >> 8);
        p[4] = (byte)(wIndex & 0xFF);
        p[5] = (byte)(wIndex >> 8);
        p[6] = (byte)(wLength & 0xFF);
        p[7] = (byte)(wLength >> 8);
        return p;
    }

    private static byte[] SetDeviceAddressPacket(ushort address)
        => CreateSetupPacket(DataDirection.HostToDevice, SetupType.Standard, SetupRecipient.Device,
            (byte)SetupRequest.SetAddress, address, 0, 0);

    private static byte[] GetDescriptorPacket(DescriptorType type, int length, ushort index = 0)
        => CreateSetupPacket(DataDirection.DeviceToHost, SetupType.Standard, SetupRecipient.Device,
            (byte)SetupRequest.GetDescriptor, (ushort)((byte)type << 8), index, (ushort)length);

    private static byte[] SetDeviceConfigurationPacket(ushort configurationNumber)
        => CreateSetupPacket(DataDirection.HostToDevice, SetupType.Standard, SetupRecipient.Device,
            (byte)SetupRequest.SetDeviceConfiguration, configurationNumber, 0, 0);
}

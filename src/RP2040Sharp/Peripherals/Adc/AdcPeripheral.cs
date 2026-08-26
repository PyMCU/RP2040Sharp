using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Adc;

/// <summary>
/// RP2040 ADC peripheral (base 0x4004C000).
/// 4 external channels (GPIO26-29) + 1 internal temperature sensor.
/// Conversion results are provided via injectable callbacks for simulation.
/// START_MANY (free-running) mode is driven via ITickable.
/// </summary>
public sealed class AdcPeripheral : IMemoryMappedDevice, ITickable
{
    private const uint ADC_CS     = 0x000;  // Control / Status
    private const uint ADC_RESULT = 0x004;  // Conversion result (12-bit, read-only)
    private const uint ADC_FCS    = 0x008;  // FIFO control / status
    private const uint ADC_FIFO   = 0x00C;  // FIFO result
    private const uint ADC_DIV    = 0x010;  // Clock divisor
    private const uint ADC_INTR   = 0x014;  // Raw interrupt status
    private const uint ADC_INTE   = 0x018;  // Interrupt enable
    private const uint ADC_INTF   = 0x01C;  // Force interrupt
    private const uint ADC_INTS   = 0x020;  // Masked interrupt status

    private const int CHANNEL_COUNT = 5;
    private const int FIFO_DEPTH = 4;

    private readonly CortexM0Plus _cpu;

    private uint _cs;     // Includes selected channel (bits 14:12), EN (bit 0), START_ONCE (bit 2)
    private uint _result; // Latest 12-bit conversion result
    private uint _fcs;    // FIFO control/status
    private uint _div;
    private uint _inte;
    private uint _intf;

    private readonly Queue<ushort> _adcFifo = new(FIFO_DEPTH);
    private bool _fifoUnder;  // underflow (read when empty)
    private bool _fifoOver;   // overflow (write when full)
    private long _tickAccum;  // accumulated CPU cycles for free-running mode

    /// <summary>
    /// Optional per-channel value provider. Return a 12-bit value (0-4095).
    /// If null for a channel, returns 0.
    /// </summary>
    public Func<int, ushort>? ReadChannel;

    /// <summary>Read-only snapshot of the ADC control state for external inspection (playground/tests).</summary>
    public readonly record struct AdcStateSnapshot(
        bool Enabled, int SelectedChannel, bool TempSensorEnabled, bool StartMany, bool FifoEnabled);
    public int ChannelCount => CHANNEL_COUNT;
    public AdcStateSnapshot GetState() => new(
        (_cs & 1u) != 0,                    // EN
        (int)((_cs >> 12) & 0x7),           // AINSEL (RP2040: 3 bits, ch4 = temp)
        (_cs & (1u << 1)) != 0,             // TS_EN
        (_cs & (1u << 3)) != 0,             // START_MANY
        (_fcs & 1u) != 0);                  // FIFO EN

    /// <summary>DREQ source for DMA: true when the ADC FIFO has data to read.</summary>
    public bool HasFifoData => _adcFifo.Count > 0;

    public uint Size => 0x100;

    // ── ITickable (START_MANY free-running mode) ──────────────────────────

    public void Tick(long deltaCycles)
    {
        if ((_cs & (1u << 3)) == 0) return;  // START_MANY not set

        // ADC clock = 48 MHz; CPU clock = 125 MHz; each conversion takes 96 ADC clocks.
        // ADC_DIV: INT[27:8] + FRAC[7:0] (integer and fractional divisor of ADC clock).
        var divInt  = (long)((_div >> 8) & 0xFFFFF);
        var divFrac = (long)(_div & 0xFF);
        if (divInt == 0) divInt = 1;

        // cycles_per_conversion = (divInt + divFrac/256) * 96 * (CPU_HZ / ADC_HZ)
        //   = (divInt*256 + divFrac) * 96 * 125 / (256 * 48)
        const long num = 96L * 125;
        const long den = 256L * 48;
        var cyclesPerConv = (divInt * 256 + divFrac) * num / den;
        if (cyclesPerConv < 1) cyclesPerConv = 1;

        _tickAccum += deltaCycles;
        while (_tickAccum >= cyclesPerConv)
        {
            _tickAccum -= cyclesPerConv;
            PerformConversion();
        }
    }

    public AdcPeripheral(CortexM0Plus cpu)
    {
        _cpu = cpu;
    }

    public uint ReadWord(uint address)
    {
        return address switch
        {
            ADC_CS     => _cs | (1u << 8),  // READY is always 1 in synchronous simulation
            ADC_RESULT => _result & 0xFFF,
            ADC_FCS    => BuildFcs(),
            ADC_FIFO   => ReadFifo(),
            ADC_DIV    => _div,
            ADC_INTR   => BuildIntr(),
            ADC_INTE   => _inte,
            ADC_INTF   => _intf,
            ADC_INTS   => (BuildIntr() | _intf) & _inte,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        switch (address)
        {
            case ADC_CS:
                _cs = value & ~(1u << 8);  // READY is read-only HW; don't store it
                if ((value & (1u << 2)) != 0)  // START_ONCE
                    PerformConversion();
                break;
            case ADC_FCS:
                // Writable bits: [3:0] and [27:24]; bits [10:9] are write-1-clear
                _fcs = value & 0x0F00000Fu;
                if ((value & (1u << 10)) != 0) _fifoUnder = false;
                if ((value & (1u << 11)) != 0) _fifoOver  = false;
                if ((_fcs & 1) == 0) _adcFifo.Clear();  // clear FIFO when EN=0
                break;
            case ADC_DIV:
                _div = value;
                break;
            case ADC_INTE:
                _inte = value & 1;
                break;
            case ADC_INTF:
                _intf = value & 1;
                break;
        }
    }

    public void WriteHalfWord(uint address, ushort value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 2) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFFFu << shift)) | ((uint)value << shift));
    }

    public void WriteByte(uint address, byte value)
    {
        var aligned = address & ~3u;
        var shift = (int)((address & 3) << 3);
        WriteWord(aligned, (ReadWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
    }

    /// <summary>AINSEL value that selects the internal temperature sensor.</summary>
    private const int TEMP_SENSOR_CHANNEL = 4;

    /// <summary>CS.TS_EN — powers the temperature sensor.</summary>
    private const uint CS_TS_EN = 1u << 1;

    /// <summary>12-bit code for Vbe = 0.706 V on a 3.3 V reference, i.e. 27 °C (datasheet §4.9.5).</summary>
    private const ushort TempSensor27C = 876;

    private void PerformConversion()
    {
        var channel = (int)((_cs >> 12) & 0x7);
        if (channel >= CHANNEL_COUNT) channel = 0;

        // Channel 4 is the on-die temperature sensor, not a pin: with no provider it must still read
        // like silicon or every temperature reading comes out as hundreds of degrees. §4.9.5 gives
        // Vbe = 0.706 V at 27 °C, which is code 0.706/3.3 * 4096 = 876 — what adc/temperature.py
        // turns back into ~27 °C. TS_EN (CS bit 1) gates it, as on hardware.
        _result = ReadChannel?.Invoke(channel)
                  ?? (channel == TEMP_SENSOR_CHANNEL && (_cs & CS_TS_EN) != 0 ? TempSensor27C : (ushort)0);
        _result &= 0xFFF;

        // Clear START_ONCE (READY is always 1 in ReadWord, no need to set it here)
        _cs &= ~(1u << 2);

        AdvanceRoundRobin();

        // Push to FIFO if enabled
        if ((_fcs & 1) != 0)
        {
            if (_adcFifo.Count >= FIFO_DEPTH)
            {
                _fifoOver = true;
            }
            else
            {
                var sample = (ushort)(_result & 0xFFF);
                if ((_fcs & (1u << 1)) != 0) sample >>= 4;  // SHIFT
                _adcFifo.Enqueue(sample);
            }

            // Fire ADC_IRQ_FIFO (IRQ 22) when FIFO level meets threshold
            if (BuildIntr() != 0 && (_inte & 1) != 0)
                _cpu.SetInterrupt(22, true);
        }
    }

    private uint BuildFcs()
    {
        var level = (uint)_adcFifo.Count;
        var thresh = (_fcs >> 24) & 0xF;
        return (_fcs & 0x0F00000Fu)
             | (level << 16)
             | (_adcFifo.Count == 0 ? (1u << 8) : 0u)    // EMPTY
             | (_adcFifo.Count >= FIFO_DEPTH ? (1u << 9) : 0u)  // FULL
             | (_fifoUnder ? (1u << 10) : 0u)
             | (_fifoOver  ? (1u << 11) : 0u)
             | (thresh << 24);
    }

    private uint ReadFifo()
    {
        if (_adcFifo.TryDequeue(out var v)) return v;
        _fifoUnder = true;
        return 0;
    }

    private uint BuildIntr()
    {
        var thresh = (int)((_fcs >> 24) & 0xF);
        var effectiveThresh = thresh == 0 ? 1 : thresh;  // threshold 0 behaves as 1 (matches hardware)
        return ((_fcs & 1) != 0 && _adcFifo.Count >= effectiveThresh) ? 1u : 0u;
    }

    private void AdvanceRoundRobin()
    {
        // RROBIN bits [20:16]: 5-bit bitmask of channels participating in round-robin (channels 0–4)
        var rrobin = (int)((_cs >> 16) & 0x1F);
        if (rrobin == 0) return;

        var current = (int)((_cs >> 12) & 0x7);
        for (var i = 1; i <= 5; i++)
        {
            var next = (current + i) % 5;  // 5 channels: 0–3 external + 4 temperature sensor
            if ((rrobin & (1 << next)) != 0)
            {
                _cs = (_cs & ~(0x7u << 12)) | ((uint)next << 12);
                return;
            }
        }
    }
}

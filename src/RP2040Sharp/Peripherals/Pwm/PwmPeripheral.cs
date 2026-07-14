using RP2040.Core.Cpu;
using RP2040.Core.Memory;

namespace RP2040.Peripherals.Pwm;

/// <summary>
/// RP2040 PWM peripheral (base 0x40050000).
/// 8 slices (A/B channels each). Supports the four CSR.DIVMODE clocking modes:
/// free-running (clk_sys/div), gated on the B-pin level, and rising/falling B-pin
/// edge counting (what CircuitPython's countio uses). The B-pin input arrives via
/// <see cref="SetBInput"/> from the GPIO input path when the pin is muxed to PWM.
/// Provides ITickable to advance the counter.
/// </summary>
public sealed class PwmPeripheral : IMemoryMappedDevice, ITickable
{
    private const int SLICE_COUNT = 8;

    // Per-slice offsets within each 0x14-byte block
    private const uint OFF_CSR = 0x00;    // Control / Status
    private const uint OFF_DIV = 0x04;    // Clock divisor (8.4 fixed-point)
    private const uint OFF_CTR = 0x08;    // Counter value
    private const uint OFF_CC  = 0x0C;    // Compare values (B[31:16], A[15:0])
    private const uint OFF_TOP = 0x10;    // Wrap value

    private const uint SLICE_BYTES = 0x14;

    // System registers
    private const uint REG_EN      = 0xA0;  // enable bitfield (bit N = enable slice N)
    private const uint REG_INTR    = 0xA4;  // raw interrupt (write 1 to clear)
    private const uint REG_INTE    = 0xA8;  // interrupt enable
    private const uint REG_INTF    = 0xAC;  // interrupt force
    private const uint REG_INTS    = 0xB0;  // interrupt status

    private readonly CortexM0Plus _cpu;

    private readonly uint[] _csr = new uint[SLICE_COUNT];
    private readonly uint[] _div = new uint[SLICE_COUNT];
    private readonly uint[] _ctr = new uint[SLICE_COUNT];
    private readonly uint[] _cc  = new uint[SLICE_COUNT];
    private readonly uint[] _top = new uint[SLICE_COUNT];

    private long[] _fracAccum = new long[SLICE_COUNT];
    private bool[] _phaseDir  = new bool[SLICE_COUNT];  // true=counting up (phase-correct)
    private readonly bool[] _bInput = new bool[SLICE_COUNT];  // B-pin level (gated / edge DIVMODEs)

    private uint _enable;   // slice enable bitfield (mirrors CSR.EN per slice)
    private uint _intr;
    private uint _inte;
    private uint _intf;

    // CSR bit definitions
    private const uint CSR_EN        = 1u << 0;
    private const uint CSR_PH_CORRECT = 1u << 1;
    private const uint CSR_A_INV     = 1u << 2;
    private const uint CSR_B_INV     = 1u << 3;
    private const uint CSR_DIVMODE   = 3u << 4;   // bits [5:4]
    private const uint CSR_PH_RET    = 1u << 6;   // strobe
    private const uint CSR_PH_ADV    = 1u << 7;   // strobe
    private const uint CSR_PH_STALLED = 1u << 8;  // read-only

    public uint Size => 0x1000;

    public PwmPeripheral(CortexM0Plus cpu)
    {
        _cpu = cpu;
        for (var i = 0; i < SLICE_COUNT; i++)
        {
            _top[i] = 0xFFFF;   // default wrap at 0xFFFF
            _div[i] = 0x10;     // reset: integer=1, frac=0
        }
    }

    // ── ITickable ────────────────────────────────────────────────────

    public void Tick(long deltaCycles)
    {
        for (var s = 0; s < SLICE_COUNT; s++)
        {
            if ((_csr[s] & CSR_EN) == 0) continue;  // slice not enabled

            var divMode = (_csr[s] & CSR_DIVMODE) >> 4;
            if (divMode >= 2) continue;                // edge modes: SetBInput advances the slice
            if (divMode == 1 && !_bInput[s]) continue; // gated: only counts while B is high

            Advance(s, deltaCycles * 16);
        }
    }

    /// <summary>Drive the slice's B-pin input (from the GPIO mux when FUNCSEL=PWM). In the
    /// edge-count DIVMODEs each matching transition advances the counter by one divider event.</summary>
    public void SetBInput(int slice, bool level)
    {
        if (_bInput[slice] == level) return;
        _bInput[slice] = level;
        if ((_csr[slice] & CSR_EN) == 0) return;
        var divMode = (_csr[slice] & CSR_DIVMODE) >> 4;
        if ((divMode == 2 && level) || (divMode == 3 && !level))
            Advance(slice, 16);
    }

    private void Advance(int s, long events16)
    {
        // DIV = integer (bits 11:4) + fraction (bits 3:0) in 8.4 format
        var divInt  = (int)((_div[s] >> 4) & 0xFF);
        var divFrac = (int)(_div[s] & 0xF);
        if (divInt == 0) divInt = 1;

        // Fixed-point divisor in 1/16 units
        var divisor = divInt * 16 + divFrac;

        _fracAccum[s] += events16;
        var steps = _fracAccum[s] / divisor;
        _fracAccum[s] %= divisor;

        var phCorrect = (_csr[s] & CSR_PH_CORRECT) != 0;

        for (var i = 0L; i < steps; i++)
        {
            if (phCorrect)
            {
                // Phase-correct: count up to TOP then back down to 0
                if (_phaseDir[s])
                {
                    _ctr[s]++;
                    if (_ctr[s] >= _top[s])
                    {
                        _ctr[s] = _top[s];
                        _phaseDir[s] = false;
                    }
                }
                else
                {
                    if (_ctr[s] == 0)
                    {
                        _phaseDir[s] = true;
                        _intr |= 1u << s;
                        if ((_inte & (1u << s)) != 0)
                            _cpu.SetInterrupt(4, true); // PWM_IRQ_WRAP is single shared IRQ
                    }
                    else _ctr[s]--;
                }
            }
            else
            {
                _ctr[s]++;
                if (_ctr[s] > _top[s])
                {
                    _ctr[s] = 0;
                    _intr |= 1u << s;
                    if ((_inte & (1u << s)) != 0)
                        _cpu.SetInterrupt(4, true); // PWM_IRQ_WRAP is single shared IRQ
                }
            }
        }
    }

    // ── IMemoryMappedDevice ──────────────────────────────────────────

    public uint ReadWord(uint address)
    {
        if (address < SLICE_COUNT * SLICE_BYTES)
        {
            var s = (int)(address / SLICE_BYTES);
            return (address % SLICE_BYTES) switch
            {
                OFF_CSR => _csr[s],
                OFF_DIV => _div[s],
                OFF_CTR => _ctr[s],
                OFF_CC  => _cc[s],
                OFF_TOP => _top[s],
                _ => 0,
            };
        }

        return address switch
        {
            REG_EN   => _enable,
            REG_INTR => _intr,
            REG_INTE => _inte,
            REG_INTF => _intf,
            REG_INTS => (_intr | _intf) & _inte,
            _ => 0,
        };
    }

    public ushort ReadHalfWord(uint address) =>
        (ushort)(ReadWord(address & ~3u) >> (int)((address & 2) << 3));

    public byte ReadByte(uint address) =>
        (byte)(ReadWord(address & ~3u) >> (int)((address & 3) << 3));

    public void WriteWord(uint address, uint value)
    {
        if (address < SLICE_COUNT * SLICE_BYTES)
        {
            var s = (int)(address / SLICE_BYTES);
            switch (address % SLICE_BYTES)
            {
                case OFF_CSR:
                    // PH_ADV / PH_RET are strobe bits — apply immediately, don't store
                    if ((value & CSR_PH_ADV) != 0 && _ctr[s] < _top[s]) _ctr[s]++;
                    if ((value & CSR_PH_RET) != 0 && _ctr[s] > 0)       _ctr[s]--;
                    _csr[s] = value & ~(CSR_PH_ADV | CSR_PH_RET | CSR_PH_STALLED);
                    if ((value & CSR_EN) != 0)
                    {
                        _enable |= 1u << s;
                        // When first enabling in phase-correct mode with counter at 0,
                        // start counting UP to prevent a spurious wrap-interrupt at t=0.
                        if ((value & CSR_PH_CORRECT) != 0 && _ctr[s] == 0)
                            _phaseDir[s] = true;
                    }
                    else _enable &= ~(1u << s);
                    break;
                case OFF_DIV: _div[s] = value & 0xFFF; break;
                case OFF_CTR: _ctr[s] = value & 0xFFFF; break;
                case OFF_CC:  _cc[s]  = value; break;
                case OFF_TOP: _top[s] = value & 0xFFFF; break;
            }
            return;
        }

        switch (address)
        {
            case REG_EN:
                _enable = value & 0xFF;
                // Mirror enable bits into per-slice CSR so Tick() sees the change
                for (var i = 0; i < SLICE_COUNT; i++)
                {
                    if ((_enable & (1u << i)) != 0) _csr[i] |=  CSR_EN;
                    else                            _csr[i] &= ~CSR_EN;
                }
                break;
            case REG_INTR: _intr &= ~value;          break;  // write 1 to clear
            case REG_INTE: _inte = value & 0xFF;     break;
            case REG_INTF: _intf = value & 0xFF;     break;
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

    /// <summary>Read Channel A duty cycle (0-65535). Applies A_INV if set.</summary>
    public ushort GetDutyA(int slice)
    {
        var raw = (ushort)(_cc[slice] & 0xFFFF);
        return (_csr[slice] & CSR_A_INV) != 0 ? (ushort)(~raw) : raw;
    }

    /// <summary>Read Channel B duty cycle (0-65535). Applies B_INV if set.</summary>
    public ushort GetDutyB(int slice)
    {
        var raw = (ushort)(_cc[slice] >> 16);
        return (_csr[slice] & CSR_B_INV) != 0 ? (ushort)(~raw) : raw;
    }

    /// <summary>True if slice counter is currently counting up (phase-correct mode).</summary>
    public bool IsCountingUp(int slice) => _phaseDir[slice];
}

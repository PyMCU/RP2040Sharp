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
public sealed class PwmPeripheral : IMemoryMappedDevice, IDeferrableTickable
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
        Schedule?.Sync();
        Schedule?.Touch();
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

        // Counter steps that only count are taken at once; the loop goes round once per wrap or turn at TOP, which
        // is where a step does more (the same result as stepping one at a time).
        while (steps > 0)
        {
            long c = _ctr[s], top = _top[s];
            if (phCorrect)
            {
                if (_phaseDir[s])
                {
                    // Counting up: each step increments; the one that reaches TOP turns the counter round.
                    var toTurn = c < top ? top - c : 1;
                    if (steps < toTurn) { _ctr[s] = (uint)(c + steps); return; }
                    steps -= toTurn;
                    _ctr[s] = _top[s];
                    _phaseDir[s] = false;
                }
                else
                {
                    // Counting down: c steps to 0, then the step at 0 turns it up and raises the wrap.
                    if (steps <= c) { _ctr[s] = (uint)(c - steps); return; }
                    steps -= c + 1;
                    _ctr[s] = 0;
                    _phaseDir[s] = true;
                    _intr |= 1u << s;
                    if ((_inte & (1u << s)) != 0)
                        _cpu.SetInterrupt(4, true); // PWM_IRQ_WRAP is single shared IRQ
                }
            }
            else
            {
                // The step past TOP wraps to 0 (a counter above TOP, after a TOP write, wraps on the next step).
                var toWrap = c >= top ? 1 : top - c + 1;
                if (steps < toWrap) { _ctr[s] = (uint)(c + steps); return; }
                steps -= toWrap;
                _ctr[s] = 0;
                _intr |= 1u << s;
                if ((_inte & (1u << s)) != 0)
                    _cpu.SetInterrupt(4, true); // PWM_IRQ_WRAP is single shared IRQ
            }
        }
    }

    /// <summary>The deferred tick the machine keeps for this block (bus accesses sync it in the bridge).</summary>
    public DeferredTick? Schedule { get; set; }

    /// <summary>
    /// Cycles until the first counter step, over the slices the clock advances, that does more than count: a wrap
    /// (raw interrupt, IRQ), a turn at TOP, or a channel output that changes (the pads follow it). Edge-counting
    /// slices and gated ones with B low have no timed event (<see cref="SetBInput"/> syncs).
    /// </summary>
    public long CyclesUntilEvent()
    {
        var best = long.MaxValue;
        for (var s = 0; s < SLICE_COUNT; s++)
        {
            if ((_csr[s] & CSR_EN) == 0) continue;
            var divMode = (_csr[s] & CSR_DIVMODE) >> 4;
            if (divMode >= 2) continue;
            if (divMode == 1 && !_bInput[s]) continue;

            long c = _ctr[s], top = _top[s];
            long la = _cc[s] & 0xFFFF, lb = (_cc[s] >> 16) & 0xFFFF;
            long k;   // counter steps until the event
            var phCorrect = (_csr[s] & CSR_PH_CORRECT) != 0;
            if (!phCorrect)
            {
                k = c >= top ? 1 : top - c + 1;                       // the wrap
                // an output falls at the step that makes CTR = level
                if (la > c && la <= top) k = Math.Min(k, la - c);
                if (lb > c && lb <= top) k = Math.Min(k, lb - c);
            }
            else if (_phaseDir[s])
            {
                k = c < top ? top - c : 1;                            // the turn at TOP
                if (la > c && la < top) k = Math.Min(k, la - c);
                if (lb > c && lb < top) k = Math.Min(k, lb - c);
            }
            else
            {
                k = c + 1;                                            // the wrap at 0
                // an output rises at the step that makes CTR = level - 1
                if (la >= 1 && la <= c) k = Math.Min(k, c - la + 1);
                if (lb >= 1 && lb <= c) k = Math.Min(k, c - lb + 1);
            }

            var divInt = (int)((_div[s] >> 4) & 0xFF);
            if (divInt == 0) divInt = 1;
            long divisor = divInt * 16 + (int)(_div[s] & 0xF);
            // smallest d with (_fracAccum + 16 d) / divisor >= k
            var need = (k * divisor - _fracAccum[s] + 15) / 16;
            if (need < best) best = need;
        }
        return Math.Max(0, best);
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
    public bool IsCountingUp(int slice) { Schedule?.Sync(); return _phaseDir[slice]; }

    /// <summary>True if the slice is enabled (CSR_EN).</summary>
    public bool IsSliceEnabled(int slice) => (uint)slice < SLICE_COUNT && (_csr[slice] & CSR_EN) != 0;

    /// <summary>Live digital output level of a channel (compare vs counter, honoring inversion).</summary>
    public bool GetChannelOutput(int slice, bool channelB)
    {
        if ((uint)slice >= SLICE_COUNT) return false;
        Schedule?.Sync();
        var level = channelB ? (_cc[slice] >> 16) & 0xFFFF : _cc[slice] & 0xFFFF;
        var high = _ctr[slice] < level;
        var inv = (_csr[slice] & (channelB ? CSR_B_INV : CSR_A_INV)) != 0;
        return inv ? !high : high;
    }

    /// <summary>Read-only snapshot of a slice for external inspection (playground/tests).</summary>
    public readonly record struct PwmSliceSnapshot(
        bool Enabled, uint Counter, uint Top, ushort DutyA, ushort DutyB, uint Div,
        bool CountingUp, bool OutputA, bool OutputB);
    public int SliceCount => SLICE_COUNT;
    public PwmSliceSnapshot GetSlice(int slice)
    {
        if ((uint)slice >= SLICE_COUNT) return default;
        Schedule?.Sync();
        return new PwmSliceSnapshot(IsSliceEnabled(slice), _ctr[slice], _top[slice],
            GetDutyA(slice), GetDutyB(slice), _div[slice], IsCountingUp(slice),
            GetChannelOutput(slice, false), GetChannelOutput(slice, true));
    }
}

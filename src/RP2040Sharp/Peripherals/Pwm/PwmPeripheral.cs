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
        if (OutputStep is not null) { TickObserved(deltaCycles); return; }
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
        {
            Advance(slice, 16);
            OutputsWritten?.Invoke();
        }
    }

    private void Advance(int s, long events16)
    {
        var divisor = Divisor(s);
        _fracAccum[s] += events16;
        var steps = _fracAccum[s] / divisor;
        _fracAccum[s] %= divisor;
        AdvanceSteps(s, steps);
    }

    /// <summary>The slice's 8.4 divider in 1/16 units (an integer part of 0 counts as 1).</summary>
    private long Divisor(int s)
    {
        var divInt = (int)((_div[s] >> 4) & 0xFF);
        if (divInt == 0) divInt = 1;
        return divInt * 16 + (int)(_div[s] & 0xF);
    }

    // ── Observed ticks (exact output edges) ──────────────────────────

    /// <summary>
    /// Raised from inside a tick for every counter step that changes a channel output: (slice, cycles from the start of
    /// the tick's window to the step, the window's length). A step falls at the first cycle at which the divider's
    /// fractional phase reaches it, so the offset is the same however the cycles were split into ticks. Null (the
    /// default) keeps the plain tick, which takes the steps that only count at once.
    /// </summary>
    public Action<int, long, long>? OutputStep;

    /// <summary>Raised after a register write, or a B-pin edge in an edge-counting DIVMODE, that may have changed a
    /// channel output or a slice enable at once.</summary>
    public Action? OutputsWritten;

    private readonly long[] _obsAcc0 = new long[SLICE_COUNT], _obsTotal = new long[SLICE_COUNT],
                            _obsDone = new long[SLICE_COUNT], _obsNextK = new long[SLICE_COUNT],
                            _obsNextT = new long[SLICE_COUNT];

    /// <summary>
    /// The tick with <see cref="OutputStep"/> set: the end state of <see cref="Tick"/>, with the steps that change an
    /// output taken one by one and every slice's in step-time order (ties by slice number), so a report sees the
    /// other slices' outputs as they are at that step.
    /// </summary>
    private void TickObserved(long deltaCycles)
    {
        uint active = 0;
        for (var s = 0; s < SLICE_COUNT; s++)
        {
            if ((_csr[s] & CSR_EN) == 0) continue;
            var divMode = (_csr[s] & CSR_DIVMODE) >> 4;
            if (divMode >= 2) continue;
            if (divMode == 1 && !_bInput[s]) continue;
            active |= 1u << s;
            _obsAcc0[s] = _fracAccum[s];
            _obsTotal[s] = (_fracAccum[s] + deltaCycles * 16) / Divisor(s);
            _obsDone[s] = 0;
            PlanObserved(s);
        }
        while (true)
        {
            int best = -1;
            var bestT = long.MaxValue;
            for (var m = active; m != 0; m &= m - 1)
            {
                var s = System.Numerics.BitOperations.TrailingZeroCount(m);
                if (_obsNextT[s] < bestT) { bestT = _obsNextT[s]; best = s; }
            }
            if (best < 0) break;
            var k = _obsNextK[best];
            bool a0 = Output(best, false), b0 = Output(best, true);
            AdvanceSteps(best, k - 1);   // steps that only count
            AdvanceSteps(best, 1);       // the event step
            _obsDone[best] += k;
            if (Output(best, false) != a0 || Output(best, true) != b0) OutputStep!(best, bestT, deltaCycles);
            PlanObserved(best);
        }
        for (var m = active; m != 0; m &= m - 1)
        {
            var s = System.Numerics.BitOperations.TrailingZeroCount(m);
            AdvanceSteps(s, _obsTotal[s] - _obsDone[s]);
            _fracAccum[s] = _obsAcc0[s] + deltaCycles * 16 - _obsTotal[s] * Divisor(s);
        }
    }

    /// <summary>The next event step of an observed slice inside the window, and the cycle offset it falls at.</summary>
    private void PlanObserved(int s)
    {
        var k = StepsToEvent(s);
        var j = _obsDone[s] + k;
        _obsNextK[s] = k;
        _obsNextT[s] = j > _obsTotal[s] ? long.MaxValue : (j * Divisor(s) - _obsAcc0[s] + 15) / 16;
    }

    private bool Output(int s, bool channelB)
    {
        var level = channelB ? (_cc[s] >> 16) & 0xFFFF : _cc[s] & 0xFFFF;
        var high = _ctr[s] < level;
        return (_csr[s] & (channelB ? CSR_B_INV : CSR_A_INV)) != 0 ? !high : high;
    }

    /// <summary>Counter steps until the first one that does more than count: a wrap, a turn at TOP, or a step that
    /// changes a channel output. At least 1.</summary>
    private long StepsToEvent(int s)
    {
        long c = _ctr[s], top = _top[s];
        long la = _cc[s] & 0xFFFF, lb = (_cc[s] >> 16) & 0xFFFF;
        long k;
        if ((_csr[s] & CSR_PH_CORRECT) == 0)
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
        return k;
    }

    /// <summary>Take <paramref name="steps"/> counter steps of slice <paramref name="s"/>.</summary>
    private void AdvanceSteps(int s, long steps)
    {
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

            var k = StepsToEvent(s);   // counter steps until the event
            var divisor = Divisor(s);
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
            OutputsWritten?.Invoke();
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
        OutputsWritten?.Invoke();
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

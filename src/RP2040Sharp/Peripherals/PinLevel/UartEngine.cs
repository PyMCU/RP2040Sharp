namespace RP2040.Peripherals.PinLevel;

/// <summary>Parity setting of UARTLCR_H.</summary>
public enum UartParity { None, Odd, Even, Stick1, Stick0 }

/// <summary>Line format and bit time. <see cref="BitCycles"/> is one bit in clk_sys cycles
/// (16 × (IBRD + FBRD/64) clk_peri cycles, scaled by clk_sys / clk_peri); 0 while the baud is unset.</summary>
public readonly record struct UartFrameFormat(int DataBits, int StopBits, UartParity Parity, double BitCycles);

/// <summary>What the UART engine needs from the PL011 block that owns the registers and FIFOs.</summary>
public interface IUartHost
{
    /// <summary>UARTEN and TXE (transmitter running).</summary>
    bool TxEnabled { get; }

    /// <summary>UARTEN and RXE (receiver running).</summary>
    bool RxEnabled { get; }

    /// <summary>UARTCR.LBE: TX is looped back to RX internally.</summary>
    bool Loopback { get; }

    /// <summary>UARTLCR_H.BRK: hold TX low.</summary>
    bool SendBreak { get; }

    /// <summary>A byte is waiting in the TX FIFO (cheap; lets an idle engine skip its per-tick poll).</summary>
    bool TxQueued { get; }

    UartFrameFormat GetFormat();

    /// <summary>Remove the next byte from the TX FIFO (it is now being shifted out).</summary>
    bool TryTakeTxByte(out byte value);

    /// <summary>A frame was received (flags are the DR error bits FE / PE / BE).</summary>
    void ByteReceived(byte value, bool framingError, bool parityError, bool breakError);
}

/// <summary>
/// Bit-level PL011 UART. The transmitter shifts each FIFO byte out on the TX pad as start bit, 5-8 data
/// bits LSB first, optional parity and 1-2 stop bits at the configured baud; the receiver watches the RX pad
/// for a falling edge, confirms the start bit at its middle, then samples every following bit at its
/// middle (data, parity, first stop bit; a low stop bit is a framing error). TX idles high.
///
/// <para>Both halves run on one event-ordered timeline inside <see cref="Tick"/>, so a TX edge and the
/// RX reaction it causes (loopback, or an echoing external device) land on their true cycles however many
/// cycles one coalesced tick covers. An RX edge made by an external device between ticks is stamped at the
/// engine's current cycle, i.e. with the tick quantum as its resolution.</para>
/// </summary>
public sealed class UartEngine : IPadFunction
{
    private const int WaitHost = -1;

    private sealed class Runner(IEnumerable<int> program)
    {
        public IEnumerator<int> It = program.GetEnumerator();
        public long Due;          // absolute cycle of the next step
        public bool Host = true;  // waiting on the host: stepped once per tick (or when woken)
        public bool Polled;
    }

    private readonly IUartHost _host;
    private Runner _tx, _rx;
    private long _now;

    private bool _txLevel = true;     // TX idles high
    private bool _rxPad = true;       // RX pad input (idle high)
    private bool _rxArmed = true;     // the line has been high since the last frame: a fall is a start bit
    private long _rxEdgeAt;
    private double _txCarry, _rxCarry;
    private bool _txBusy;

    public UartEngine(IUartHost host)
    {
        _host = host;
        _tx = new Runner(TxProgram());
        _rx = new Runner(RxProgram());
    }

    internal Action<bool>? BeforeAccess;

    /// <summary>Called after a pad input reached the engine (its next event may have moved).</summary>
    internal Action? AfterInput;

    public long Cycle { get { BeforeAccess?.Invoke(false); return _now; } }

    /// <summary>The transmitter is shifting a frame (UARTFR.BUSY, beyond the FIFO contents).</summary>
    public bool TxBusy => _txBusy;

    public Action<int, long>? DriveChanged { get; set; }

    public bool GetOutputEnable(int role) => role == PadFunctions.UartTx;
    public bool GetOutputLevel(int role) => role == PadFunctions.UartTx && _txLevel;

    public void SetInput(int role, bool level)
    {
        BeforeAccess?.Invoke(true);
        if (role == PadFunctions.UartRx) { _rxPad = level; if (!_host.Loopback) RxEdge(level); }
        AfterInput?.Invoke();
    }

    /// <summary>The block's configuration changed (e.g. loopback toggled): re-evaluate the receive line.</summary>
    public void Refresh() { BeforeAccess?.Invoke(true); _rx.Polled = false; _tx.Polled = false; }

    public void Reset()
    {
        BeforeAccess?.Invoke(true);
        _tx = new Runner(TxProgram());
        _rx = new Runner(RxProgram());
        _txBusy = false; _rxArmed = true; _txCarry = _rxCarry = 0;
        SetTx(true);
    }

    /// <summary>Drop any frame and restart the engine's clock at <paramref name="now"/> (see SpiMasterEngine.Rebase).</summary>
    internal void Rebase(long now) { _now = now; Reset(); }

    private bool RxLine => _host.Loopback ? _txLevel : _rxPad;

    private void RxEdge(bool level)
    {
        if (level) _rxArmed = true;
        else _rxEdgeAt = _now;
        _rx.Polled = false;   // let the receiver look at the line again, even within this tick
    }

    private void SetTx(bool level)
    {
        if (_txLevel == level) return;
        _txLevel = level;
        DriveChanged?.Invoke(PadFunctions.UartTx, _now);
        if (_host.Loopback) RxEdge(level);
    }

    /// <summary>Nothing for a tick to do given an empty TX FIFO: both runners are parked on the host, TX
    /// already matches BRK, and no armed receiver sees the line low. The block passes its UARTCR.LBE /
    /// UARTLCR_H.BRK so this stays field reads; it then calls <see cref="Skip"/> instead of <see cref="Tick"/>.</summary>
    public bool Quiet(bool loopback, bool sendBreak) =>
        _tx.Host && _tx.Polled && _rx.Host && _rx.Polled && _txLevel != sendBreak
        && !(_rxArmed && !(loopback ? _txLevel : _rxPad));

    /// <summary>Advance time over a quiet stretch (see <see cref="Quiet"/>).</summary>
    public void Skip(long deltaCycles) => _now += deltaCycles;

    internal long CyclesUntilEvent => Math.Max(0, Math.Min(Next(_tx), Next(_rx)) - _now);

    /// <summary>The engine's own time, without the sync <see cref="Cycle"/> does (it may lag the machine's).</summary>
    internal long Now => _now;

    /// <summary>Cycles until the transmitter's next step (the only one that moves the TX pad): its timer while it shifts
    /// a frame, 0 while it waits on the host (a queued byte is taken on the next tick).</summary>
    internal long CyclesUntilTxStep => _tx.Host ? 0 : Math.Max(0, _tx.Due - _now);

    public void Tick(long deltaCycles)
    {
        var end = _now + deltaCycles;
        // A runner waiting on the host is only re-polled when something can have changed for it: the TX side
        // when a byte is queued or the line disagrees with BRK, the RX side while an armed receiver sees the line low. Every
        // other change reaches it as an event (RxEdge, Refresh), so an idle UART costs a few field reads.
        if (_tx.Host && (_host.TxQueued || _txLevel == _host.SendBreak)) _tx.Polled = false;
        if (_rx.Host && _rxArmed && !RxLine) _rx.Polled = false;
        for (var guard = 0; ; guard++)
        {
            var tt = Next(_tx);
            var tr = Next(_rx);
            var t = Math.Min(tt, tr);
            if (t > end) break;
            if (guard > 1_000_000) throw new InvalidOperationException("UART engine made no progress.");
            if (t > _now) _now = t;
            Step(tt <= tr ? _tx : _rx);
        }
        _now = end;
    }

    private long Next(Runner r) => r.Host ? (r.Polled ? long.MaxValue : _now) : r.Due;

    private void Step(Runner r)
    {
        if (r.Host) r.Polled = true;
        if (!r.It.MoveNext()) throw new InvalidOperationException("UART program ended.");
        var v = r.It.Current;
        if (v < 0) { r.Host = true; }
        else { r.Host = false; r.Due = _now + v; }
    }

    private int Bit(double bitCycles, ref double carry)
    {
        carry += bitCycles;
        var w = (int)carry;
        carry -= w;
        return w;
    }

    private static bool OddOnes(int v) => (System.Numerics.BitOperations.PopCount((uint)v) & 1) != 0;

    private IEnumerable<int> TxProgram()
    {
        while (true)
        {
            byte b;
            UartFrameFormat f;
            while (true)
            {
                SetTx(!_host.SendBreak);
                f = _host.GetFormat();
                if (_host.TxEnabled && f.BitCycles > 0 && !_host.SendBreak && _host.TryTakeTxByte(out b)) break;
                _txBusy = false;
                yield return WaitHost;
            }
            _txBusy = true;
            _txCarry = 0;
            var data = b & ((1 << f.DataBits) - 1);

            SetTx(false);                                       // start bit
            yield return Bit(f.BitCycles, ref _txCarry);
            for (var i = 0; i < f.DataBits; i++)
            {
                SetTx(((data >> i) & 1) != 0);
                yield return Bit(f.BitCycles, ref _txCarry);
            }
            if (f.Parity != UartParity.None)
            {
                SetTx(f.Parity switch
                {
                    UartParity.Odd => !OddOnes(data),           // total ones odd
                    UartParity.Even => OddOnes(data),           // total ones even
                    UartParity.Stick1 => true,
                    _ => false,
                });
                yield return Bit(f.BitCycles, ref _txCarry);
            }
            SetTx(true);                                        // stop bit(s)
            for (var i = 0; i < f.StopBits; i++)
                yield return Bit(f.BitCycles, ref _txCarry);
            _txBusy = false;
        }
    }

    private IEnumerable<int> RxProgram()
    {
        while (true)
        {
            UartFrameFormat f;
            while (true)
            {
                f = _host.GetFormat();
                if (_host.RxEnabled && f.BitCycles > 0 && _rxArmed && !RxLine) break;
                yield return WaitHost;
            }
            _rxArmed = false;
            _rxCarry = 0;
            var lag = Math.Max(_now - _rxEdgeAt, 0);

            // Confirm the start bit at its middle (a shorter low pulse is a glitch).
            yield return (int)Math.Max(f.BitCycles / 2 - lag, 0);
            if (RxLine) continue;

            var data = 0;
            for (var i = 0; i < f.DataBits; i++)
            {
                yield return Bit(f.BitCycles, ref _rxCarry);
                if (RxLine) data |= 1 << i;
            }
            var parityError = false;
            if (f.Parity != UartParity.None)
            {
                yield return Bit(f.BitCycles, ref _rxCarry);
                var p = RxLine;
                var expected = f.Parity switch
                {
                    UartParity.Odd => !OddOnes(data),
                    UartParity.Even => OddOnes(data),
                    UartParity.Stick1 => true,
                    _ => false,
                };
                parityError = p != expected;
            }
            yield return Bit(f.BitCycles, ref _rxCarry);
            var framingError = !RxLine;                         // the (first) stop bit must be high
            _rxArmed = RxLine;                                  // a line still low (break) waits to rise again
            _host.ByteReceived((byte)data, framingError, parityError, framingError && data == 0);
        }
    }
}

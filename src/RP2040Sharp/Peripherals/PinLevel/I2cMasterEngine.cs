namespace RP2040.Peripherals.PinLevel;

/// <summary>One entry of the DW_apb_i2c TX FIFO: a data byte to write, or a read request, with the
/// RESTART / STOP flags of IC_DATA_CMD.</summary>
public readonly record struct I2cCommand(byte Data, bool Read, bool Restart, bool Stop);

/// <summary>IC_TX_ABRT_SOURCE bits the engine can raise.</summary>
public static class I2cAbort
{
    public const uint AddressNoAck7Bit = 1u << 0;
    public const uint TxDataNoAck = 1u << 3;
    public const uint ArbitrationLost = 1u << 12;
}

/// <summary>What the master engine needs from the I2C block that owns the registers and FIFOs.</summary>
public interface II2cMasterHost
{
    /// <summary>The block is enabled as a master (IC_ENABLE.ENABLE, IC_CON.MASTER_MODE).</summary>
    bool Enabled { get; }

    /// <summary>The 7-bit target address (IC_TAR), sampled at the start of each address phase.</summary>
    int TargetAddress { get; }

    /// <summary>The RX FIFO can take another byte (a master stalls instead of overrunning it).</summary>
    bool RxHasRoom { get; }

    /// <summary>SCL high / low period and the SDA hold after SCL falls, in clk_sys cycles.</summary>
    void GetTiming(out int high, out int low, out int sdaHold);

    /// <summary>Peek the head of the TX FIFO without removing it.</summary>
    bool TryPeekCommand(out I2cCommand command);

    /// <summary>The engine moved the head command into its shifter (the address phase, if any, is done).</summary>
    void CommandTaken();

    void BusStart(bool repeated);
    void BusStop();

    /// <summary>A written byte finished, ACK slot included.</summary>
    void WriteDone();

    /// <summary>A byte was received (before its ACK/NACK slot).</summary>
    void ReadDone(byte value);

    /// <summary>The transfer failed; the host raises TX_ABRT with <paramref name="source"/> and flushes
    /// its FIFOs. The engine issues the STOP (except after arbitration loss) and goes idle.</summary>
    void Abort(uint source);
}

/// <summary>
/// Bit-level I2C master for the DW_apb_i2c block. It turns the block's command stream into START /
/// repeated START / STOP, the address byte, data bytes and ACK/NACK slots on SDA/SCL, drives both lines
/// open-drain (<see cref="IPadFunction"/> output-enable on = pulled low, off = released to the pull-up) and
/// samples SDA for ACKs, read data and arbitration, and SCL for clock stretching.
///
/// <para><b>Timing.</b> SCL is low for <c>LCNT+1</c> and high for <c>HCNT+1</c> clk_sys cycles per bit (the
/// block's <see cref="II2cMasterHost.GetTiming"/>), counted from the moment SCL is actually seen high, so a
/// slave stretching the clock stretches the transfer. State advances event by event inside
/// <see cref="Tick"/>: however many cycles a coalesced peripheral tick covers, each edge is placed at
/// its exact cycle (<see cref="Cycle"/>, forwarded to <see cref="IPadFunction.DriveChanged"/>).</para>
///
/// <para>A master with an empty TX FIFO holds SCL low (stalled) rather than releasing the bus, and keeps
/// holding it across an IC_ENABLE disable/enable — which is how the SDK's nostop write followed by a read
/// becomes a repeated START.</para>
/// </summary>
public sealed class I2cMasterEngine : IPadFunction
{
    private const int WaitScl = -1;    // yielded: hold until SCL is seen high (clock stretching)
    private const int WaitHost = -2;   // yielded: re-evaluate the host (command / enable / RX room) each tick
    private const int ModeTimed = 0, ModeScl = 1, ModeHost = 2;

    private readonly II2cMasterHost _host;
    private IEnumerator<int> _program;

    private long _now;
    private int _mode = ModeHost;
    private long _wait;

    private bool _sdaLow, _sclLow;          // our open-drain drive
    private bool _sdaIn = true, _sclIn = true;

    private bool _busHeld;                  // between START and STOP (including stalled with SCL low)
    private bool _lastRead;                 // direction of the transfer in progress
    private bool _byteInFlight;
    private byte _txByte;
    private bool _sampled, _ack, _lostArb;

    public I2cMasterEngine(II2cMasterHost host)
    {
        _host = host;
        _program = Program().GetEnumerator();
    }

    /// <summary>clk_sys cycles elapsed (sum of all ticks) — the time base of every edge this engine makes.</summary>
    public long Cycle => _now;

    /// <summary>The engine's own time (the same as <see cref="Cycle"/>; named as on the other engines).</summary>
    internal long Now => _now;

    /// <summary>Called before a pad input reaches the engine, to bring it up to the input's time.</summary>
    internal Action<bool>? BeforeAccess;

    /// <summary>Called after a pad input reached the engine (its next event may have moved).</summary>
    internal Action? AfterInput;

    /// <summary>True from START to STOP, including while stalled with SCL held low (IC_STATUS.MST_ACTIVITY).</summary>
    public bool BusBusy => _busHeld || _byteInFlight;

    /// <summary>True while an address or data byte (with its ACK slot) is being shifted.</summary>
    public bool ByteInFlight => _byteInFlight;

    /// <summary>Cycles until the next step that can move SDA or SCL: the timer between bus edges, 0 once a stretched
    /// SCL is seen high, long.MaxValue while it waits on SCL held low or on the host (both change only from outside).</summary>
    internal long CyclesUntilEvent => _mode switch
    {
        ModeTimed => _wait,
        ModeScl => _sclIn ? 0 : long.MaxValue,
        _ => long.MaxValue,
    };

    /// <summary>Waiting on the host (a command, the enable, RX room): only an access can move it on.</summary>
    internal bool WaitingOnHost => _mode == ModeHost;

    /// <summary>Waiting for room in the RX FIFO (a read can then go on).</summary>
    internal bool WaitingForRoom => _waitRoom;

    private bool _waitRoom;

    public bool SdaLow => _sdaLow;
    public bool SclLow => _sclLow;

    public Action<int, long>? DriveChanged { get; set; }

    // ── IPadFunction ─────────────────────────────────────────────────────────────

    public bool GetOutputEnable(int role) => role == PadFunctions.I2cSda ? _sdaLow : role == PadFunctions.I2cScl && _sclLow;
    public bool GetOutputLevel(int role) => false;   // open-drain: only ever pulls low

    public void SetInput(int role, bool level)
    {
        BeforeAccess?.Invoke(true);
        if (role == PadFunctions.I2cSda) _sdaIn = level;
        else if (role == PadFunctions.I2cScl) _sclIn = level;
        AfterInput?.Invoke();
    }

    /// <summary>Abandon any transfer, release both lines and return to idle (block reset).</summary>
    public void Reset()
    {
        _busHeld = _byteInFlight = false;
        _program = Program().GetEnumerator();
        _mode = ModeHost; _wait = 0; _waitRoom = false;
        SetSda(false); SetScl(false);
    }

    /// <summary>Drop any transfer and restart the engine's clock at <paramref name="now"/> (see SpiMasterEngine.Rebase).</summary>
    internal void Rebase(long now) { _now = now; Reset(); }

    // ── Time ─────────────────────────────────────────────────────────────────────

    /// <summary>Waiting for a command with no transfer open: a tick would only poll the host and yield again.
    /// The block checks its TX FIFO and, when empty, calls <see cref="Skip"/> instead of <see cref="Tick"/>.</summary>
    public bool Idle => _mode == ModeHost && !_busHeld && !_byteInFlight;

    /// <summary>Advance time over an idle stretch (see <see cref="Idle"/>).</summary>
    public void Skip(long deltaCycles) => _now += deltaCycles;

    /// <summary>Resume the clock of an idle engine that was not ticked for a while (the machine stops ticking an
    /// idle block): <paramref name="cycle"/> is the sum of all ticks so far. Ignored while a transfer is open.</summary>
    public void SyncTo(long cycle) { if (Idle) _now = cycle; }

    public void Tick(long deltaCycles)
    {
        // Idle: no transfer open and no command queued, so the host poll would only yield again.
        if (_mode == ModeHost && !_busHeld && !_byteInFlight && !_host.TryPeekCommand(out _))
        { _now += deltaCycles; return; }
        var polled = false;
        for (var guard = 0; ; guard++)
        {
            switch (_mode)
            {
                case ModeTimed:
                    if (_wait > 0)
                    {
                        if (_wait > deltaCycles) { _wait -= deltaCycles; _now += deltaCycles; return; }
                        deltaCycles -= _wait; _now += _wait; _wait = 0;
                    }
                    break;
                case ModeScl:
                    if (!_sclIn) { _now += deltaCycles; return; }   // slave is stretching the clock
                    break;
                default:
                    if (polled) { _now += deltaCycles; return; }
                    polled = true;
                    break;
            }
            if (guard > 100_000) throw new InvalidOperationException("I2C master engine made no progress.");
            Step();
        }
    }

    private void Step()
    {
        if (!_program.MoveNext()) { _program = Program().GetEnumerator(); _program.MoveNext(); }
        var v = _program.Current;
        if (v >= 0) { _mode = ModeTimed; _wait = v; }
        else _mode = v == WaitScl ? ModeScl : ModeHost;
    }

    private void SetSda(bool low)
    {
        if (_sdaLow == low) return;
        _sdaLow = low;
        DriveChanged?.Invoke(PadFunctions.I2cSda, _now);
    }

    private void SetScl(bool low)
    {
        if (_sclLow == low) return;
        _sclLow = low;
        DriveChanged?.Invoke(PadFunctions.I2cScl, _now);
    }

    // ── Bus sequences (SCL low on entry unless noted; they leave SCL low) ───────────

    private IEnumerable<int> Program()
    {
        while (true)
        {
            I2cCommand cmd;
            while (!(_host.Enabled && _host.TryPeekCommand(out cmd))) yield return WaitHost;

            if (!_busHeld || cmd.Restart || cmd.Read != _lastRead)
            {
                _byteInFlight = true;
                var repeated = _busHeld;
                if (repeated) foreach (var w in RepeatedStart()) yield return w;
                else foreach (var w in Start()) yield return w;
                _busHeld = true;
                _host.BusStart(repeated);

                _txByte = (byte)(((_host.TargetAddress & 0x7F) << 1) | (cmd.Read ? 1 : 0));
                foreach (var w in WriteByte()) yield return w;
                if (_lostArb) { LoseArbitration(); continue; }
                if (!_ack)
                {
                    _host.Abort(I2cAbort.AddressNoAck7Bit);
                    foreach (var w in StopAfterAbort()) yield return w;
                    continue;
                }
                _lastRead = cmd.Read;
            }

            _byteInFlight = true;
            _host.CommandTaken();
            if (!cmd.Read)
            {
                _txByte = cmd.Data;
                foreach (var w in WriteByte()) yield return w;
                if (_lostArb) { LoseArbitration(); continue; }
                if (!_ack)
                {
                    _host.Abort(I2cAbort.TxDataNoAck);
                    foreach (var w in StopAfterAbort()) yield return w;
                    continue;
                }
                _host.WriteDone();
            }
            else
            {
                while (!_host.RxHasRoom) { _waitRoom = true; yield return WaitHost; }
                _waitRoom = false;
                var value = 0;
                for (var i = 0; i < 8; i++)
                {
                    foreach (var w in Bit(sdaLow: false)) yield return w;
                    value = (value << 1) | (_sampled ? 1 : 0);
                }
                _host.ReadDone((byte)value);

                // The last byte of a transfer is NACKed: when it carries STOP, or when the next command
                // restarts / turns the bus around. With nothing queued the master stalls, SCL low, until
                // firmware says which it is.
                var nack = cmd.Stop;
                if (!nack)
                {
                    I2cCommand next;
                    while (!_host.TryPeekCommand(out next)) yield return WaitHost;
                    nack = next.Restart || !next.Read;
                }
                foreach (var w in Bit(sdaLow: !nack)) yield return w;
            }

            if (cmd.Stop)
            {
                foreach (var w in Stop()) yield return w;
                _busHeld = false;
                _host.BusStop();
            }
            _byteInFlight = false;
        }
    }

    private void LoseArbitration()
    {
        SetSda(false); SetScl(false);   // let go of both lines; no STOP — the winner owns the bus
        _busHeld = _byteInFlight = false;
        _host.Abort(I2cAbort.ArbitrationLost);
    }

    private IEnumerable<int> StopAfterAbort()
    {
        foreach (var w in Stop()) yield return w;
        _busHeld = _byteInFlight = false;
        _host.BusStop();
    }

    /// <summary>One SCL cycle: SDA is set after the hold time while SCL is low, SCL rises (and is waited
    /// for, to honour clock stretching), is sampled at the end of its high time, then falls.</summary>
    private IEnumerable<int> Bit(bool sdaLow)
    {
        _host.GetTiming(out var h, out var l, out var hold);
        if (hold >= l) hold = l - 1;
        if (hold > 0) yield return hold;
        SetSda(sdaLow);
        yield return l - hold;
        SetScl(false);
        yield return WaitScl;
        yield return h;
        _sampled = _sdaIn;
        SetScl(true);
    }

    /// <summary>Eight data bits MSB first, then the ACK slot (SDA released; low = ACK).</summary>
    private IEnumerable<int> WriteByte()
    {
        _lostArb = false;
        for (var i = 7; i >= 0; i--)
        {
            var one = ((_txByte >> i) & 1) != 0;
            foreach (var w in Bit(sdaLow: !one)) yield return w;
            if (one && !_sampled) { _lostArb = true; yield break; }   // released SDA but the bus is low
        }
        foreach (var w in Bit(sdaLow: false)) yield return w;
        _ack = !_sampled;
    }

    /// <summary>START from an idle bus: SDA falls while SCL is high, then SCL falls.</summary>
    private IEnumerable<int> Start()
    {
        _host.GetTiming(out var h, out _, out _);
        SetSda(true);
        yield return h;
        SetScl(true);
    }

    /// <summary>Repeated START from a held bus (SCL low): release SDA, raise SCL, pull SDA low, drop SCL.</summary>
    private IEnumerable<int> RepeatedStart()
    {
        _host.GetTiming(out var h, out var l, out var hold);
        if (hold >= l) hold = l - 1;
        if (hold > 0) yield return hold;
        SetSda(false);
        yield return l - hold;
        SetScl(false);
        yield return WaitScl;
        yield return h;       // tSU;STA
        SetSda(true);
        yield return h;       // tHD;STA
        SetScl(true);
    }

    /// <summary>STOP from a held bus (SCL low): SDA low, raise SCL, release SDA, then a bus-free time.</summary>
    private IEnumerable<int> Stop()
    {
        _host.GetTiming(out var h, out var l, out var hold);
        if (hold >= l) hold = l - 1;
        if (hold > 0) yield return hold;
        SetSda(true);
        yield return l - hold;
        SetScl(false);
        yield return WaitScl;
        yield return h;       // tSU;STO
        SetSda(false);        // SDA rises while SCL is high: the STOP condition
        yield return l;       // tBUF
    }
}

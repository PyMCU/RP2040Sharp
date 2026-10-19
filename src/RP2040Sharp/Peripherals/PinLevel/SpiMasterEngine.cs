namespace RP2040.Peripherals.PinLevel;

/// <summary>Frame format and clock of the PL022, resolved by the block at the start of each frame.</summary>
public readonly record struct SpiFrameFormat(int DataBits, bool Cpol, bool Cpha, bool Loopback, int HalfPeriodCycles);

/// <summary>What the SPI master engine needs from the PL022 block that owns the registers and FIFOs.</summary>
public interface ISpiMasterHost
{
    /// <summary>SSPCR1.SSE set with the block in master mode.</summary>
    bool Enabled { get; }

    SpiFrameFormat GetFormat();

    /// <summary>Remove the next word from the TX FIFO (it is now being shifted out).</summary>
    bool TryTakeWord(out ushort word);

    /// <summary>True when another word is queued (without taking it).</summary>
    bool HasWord { get; }

    /// <summary>A frame finished; <paramref name="received"/> is the word shifted in on MISO.</summary>
    void WordDone(ushort received);
}

/// <summary>
/// Bit-level SPI master for the PL022 (Motorola SPI format). Drives SCK, MOSI and the hardware CSn on
/// the pads and samples MISO, in the block's CPOL/CPHA, with the clock set by CPSDVSR × (1+SCR) peripheral
/// cycles per SCK period (<see cref="ISpiMasterHost.GetFormat"/> gives it in clk_sys cycles). Like the other
/// pin-level engines it is event-driven inside <see cref="Tick"/>: every edge sits on its exact cycle.
///
/// <para>Sampling: SPH=0 samples MISO on the leading SCK edge and shifts MOSI on the trailing one (MOSI is
/// valid before the first edge); SPH=1 shifts on the leading edge and samples on the trailing one. CSn is low
/// for the frame, with a half-period setup before the first edge; between frames it goes high, except that
/// SPH=1 keeps it low across back-to-back words, as the PL022 does.</para>
/// </summary>
public sealed class SpiMasterEngine : IPadFunction
{
    private const int WaitHost = -1;

    private readonly ISpiMasterHost _host;
    private IEnumerator<int> _program;
    private long _now;
    private long _wait;
    private bool _hostMode;     // waiting on the host (re-polled each tick) rather than on a timer

    private bool _enabled;
    private bool _sck, _mosi, _csnLow, _miso;
    private bool _busy;
    private readonly bool[] _pubOe = new bool[4], _pubLevel = new bool[4];

    public SpiMasterEngine(ISpiMasterHost host)
    {
        _host = host;
        _program = Program().GetEnumerator();
        _hostMode = true;
    }

    internal Action<bool>? BeforeAccess;

    /// <summary>Called after a pad input reached the engine (its next event may have moved).</summary>
    internal Action? AfterInput;

    public long Cycle { get { BeforeAccess?.Invoke(false); return _now; } }

    /// <summary>The engine's own time, without the sync <see cref="Cycle"/> does (it may lag the machine's).</summary>
    internal long Now => _now;

    /// <summary>A frame is in progress (SSPSR.BSY).</summary>
    public bool Busy => _busy;

    public Action<int, long>? DriveChanged { get; set; }

    public bool GetOutputEnable(int role) => _enabled && role is PadFunctions.SpiCsn or PadFunctions.SpiSck or PadFunctions.SpiTx;

    public bool GetOutputLevel(int role) => role switch
    {
        PadFunctions.SpiCsn => !_csnLow,
        PadFunctions.SpiSck => _sck,
        PadFunctions.SpiTx => _mosi,
        _ => false,
    };

    public void SetInput(int role, bool level)
    {
        BeforeAccess?.Invoke(true);
        if (role == PadFunctions.SpiRx) _miso = level;
        AfterInput?.Invoke();
    }

    /// <summary>The block's configuration or enable changed: re-evaluate the pad drive, and drop a frame in
    /// flight if the block was disabled.</summary>
    public void Refresh()
    {
        BeforeAccess?.Invoke(true);
        if (_enabled && !_host.Enabled) Reset();
        else
        {
            _enabled = _host.Enabled;
            if (!_busy) { _sck = _enabled && _host.GetFormat().Cpol; _csnLow = false; }
            Publish();
        }
    }

    public void Reset()
    {
        BeforeAccess?.Invoke(true);
        _busy = false;
        _program = Program().GetEnumerator();
        _hostMode = true; _wait = 0;
        _enabled = _host.Enabled;
        _sck = _enabled && _host.GetFormat().Cpol; _csnLow = false; _mosi = false;
        Publish();
    }

    /// <summary>Drop any frame and restart the engine's clock at <paramref name="now"/> (the machine's clock went back,
    /// as on a watchdog reboot, which resets the core cycle counter).</summary>
    internal void Rebase(long now) { _now = now; Reset(); }

    /// <summary>Waiting on the host with no frame in progress. The block checks its TX FIFO and, when empty,
    /// calls <see cref="Skip"/> instead of <see cref="Tick"/>.</summary>
    public bool Idle => _hostMode && !_busy;

    /// <summary>Advance time over an idle stretch (see <see cref="Idle"/>).</summary>
    public void Skip(long deltaCycles) => _now += deltaCycles;

    /// <summary>Counting down a timed wait inside a frame (or the CSn gaps around it): nothing the block does can
    /// change what happens before <see cref="CyclesUntilEvent"/> cycles have passed, queued words included.</summary>
    internal bool MidFrame => !_hostMode && _wait > 0;

    internal long CyclesUntilEvent => Idle ? long.MaxValue : _hostMode ? 0 : Math.Max(0, _wait);

    public void Tick(long deltaCycles)
    {
        // Idle: waiting on the host with nothing queued, so the poll would only yield again.
        if (_hostMode && !_busy && !_host.HasWord) { _now += deltaCycles; return; }
        var polled = false;
        for (var guard = 0; ; guard++)
        {
            if (_hostMode)
            {
                if (polled) { _now += deltaCycles; return; }
                polled = true;
            }
            else if (_wait > 0)
            {
                if (_wait > deltaCycles) { _wait -= deltaCycles; _now += deltaCycles; return; }
                deltaCycles -= _wait; _now += _wait; _wait = 0;
            }
            if (guard > 100_000) throw new InvalidOperationException("SPI master engine made no progress.");
            if (!_program.MoveNext()) { _program = Program().GetEnumerator(); _program.MoveNext(); }
            var v = _program.Current;
            _hostMode = v < 0;
            _wait = v < 0 ? 0 : v;
        }
    }

    private void Publish()
    {
        for (var role = 1; role < 4; role++)
        {
            var oe = GetOutputEnable(role);
            var lvl = GetOutputLevel(role);
            if (oe == _pubOe[role] && lvl == _pubLevel[role]) continue;
            _pubOe[role] = oe; _pubLevel[role] = lvl;
            DriveChanged?.Invoke(role, _now);
        }
    }

    private void SetSck(bool v) { if (_sck != v) { _sck = v; Publish(); } }
    private void SetMosi(bool v) { if (_mosi != v) { _mosi = v; Publish(); } }
    private void SetCs(bool low) { if (_csnLow != low) { _csnLow = low; Publish(); } }

    private IEnumerable<int> Program()
    {
        var csHeld = false;   // SPH=1 back-to-back words keep CSn low
        while (true)
        {
            ushort word;
            while (!(_host.Enabled && _host.TryTakeWord(out word)))
            {
                if (_busy) { _busy = false; }
                yield return WaitHost;
            }
            _enabled = true;
            var f = _host.GetFormat();
            var n = f.DataBits;
            var half = Math.Max(f.HalfPeriodCycles, 1);
            _busy = true;

            if (!csHeld)
            {
                SetSck(f.Cpol);
                SetCs(true);
                if (!f.Cpha) SetMosi(((word >> (n - 1)) & 1) != 0);
                yield return half;                       // CSn setup before the first edge
            }
            csHeld = false;

            var rx = 0;
            for (var i = 0; i < n; i++)
            {
                var bit = ((word >> (n - 1 - i)) & 1) != 0;
                if (!f.Cpha)
                {
                    rx = (rx << 1) | ((f.Loopback ? _mosi : _miso) ? 1 : 0);   // sample on the leading edge
                    SetSck(!f.Cpol);
                    yield return half;
                    SetSck(f.Cpol);
                    if (i < n - 1) SetMosi(((word >> (n - 2 - i)) & 1) != 0);
                    yield return half;
                }
                else
                {
                    SetMosi(bit);
                    SetSck(!f.Cpol);                                           // shift on the leading edge
                    yield return half;
                    rx = (rx << 1) | ((f.Loopback ? _mosi : _miso) ? 1 : 0);   // sample on the trailing edge
                    SetSck(f.Cpol);
                    yield return half;
                }
            }

            var more = _host.Enabled && _host.HasWord;
            if (f.Cpha && more) csHeld = true;
            else
            {
                SetCs(false);
                if (more) yield return half;             // CSn high between frames
            }
            if (!csHeld && !more) _busy = false;
            _host.WordDone((ushort)rx);
        }
    }
}

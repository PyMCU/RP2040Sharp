namespace RP2040.Peripherals.Pio;

/// <summary>
/// Internal state for one PIO state machine.
/// Carries shift registers, scratch X/Y, FIFO references, and execution state.
/// </summary>
internal sealed class PioStateMachine
{
    private const int FIFO_DEPTH = 4;

    // ── Registers ────────────────────────────────────────────────────
    public uint PC;          // Program counter (0-31)
    public uint X;           // Scratch X
    public uint Y;           // Scratch Y
    public uint ISR;         // Input shift register
    public uint OSR;         // Output shift register
    public uint IsrCount;    // How many bits shifted into ISR
    public uint OsrCount;    // How many bits remain in OSR (32 when full)

    public uint ClkDiv;      // CLKDIV register (8.8 integer+frac)
    public uint ExecCtrl;    // EXECCTRL
    public uint ShiftCtrl;   // SHIFTCTRL
    public uint PinCtrl;     // PINCTRL

    // ── FIFOs ────────────────────────────────────────────────────────
    internal readonly Queue<uint> TxFifo = new(FIFO_DEPTH);
    internal readonly Queue<uint> RxFifo = new(FIFO_DEPTH);

    // ── Execution state ──────────────────────────────────────────────
    public bool Enabled;
    public bool Stalled;         // waiting for FIFO or condition
    /// <summary>
    /// An IN already shifted its data into the ISR and reached the autopush threshold, but the
    /// RX FIFO was full so the push could not complete. The SM stalls without re-shifting; the
    /// deferred push completes once the RX FIFO drains. Mirrors RP2040 datasheet §3.5.4.2.
    /// </summary>
    internal bool AutopushPending;
    internal bool PcJumped;      // JMP or MOV PC set a new PC directly (skip auto-increment)
    internal long FracAccum;     // for sub-cycle fractional clock divisor
    internal int DelayCounter;   // instruction delay cycles remaining
    public int SmIndex;          // index of this SM within its PIO block (0-3); set by PioPeripheral

    // ── GPIO state (driven by this SM) ───────────────────────────────
    public uint GpioPins;    // current SET/OUT output value
    public uint GpioPinDirs; // direction bits (1=output)

    public void Reset()
    {
        PC = 0; X = 0; Y = 0; ISR = 0; OSR = 0;
        IsrCount = 0; OsrCount = 0;
        Stalled = false; AutopushPending = false; PcJumped = false; FracAccum = 0; DelayCounter = 0;
        TxFifo.Clear(); RxFifo.Clear();
    }

    // ── SHIFTCTRL helpers ─────────────────────────────────────────────
    /// <summary>ISR shift direction: false=left, true=right (SHIFTCTRL bit 18).</summary>
    public bool IsrShiftRight => (ShiftCtrl & (1u << 18)) != 0;
    /// <summary>OSR shift direction: false=left, true=right (SHIFTCTRL bit 19).</summary>
    public bool OsrShiftRight => (ShiftCtrl & (1u << 19)) != 0;
    /// <summary>Autopush threshold 0=32: SHIFTCTRL bits [24:20].</summary>
    public int AutopushThreshold => (int)((ShiftCtrl >> 20) & 0x1F) is 0 ? 32 : (int)((ShiftCtrl >> 20) & 0x1F);
    /// <summary>Autopull threshold 0=32: SHIFTCTRL bits [29:25].</summary>
    public int AutopullThreshold => (int)((ShiftCtrl >> 25) & 0x1F) is 0 ? 32 : (int)((ShiftCtrl >> 25) & 0x1F);
    public bool AutopushEnabled => (ShiftCtrl & (1u << 16)) != 0;
    public bool AutopullEnabled => (ShiftCtrl & (1u << 17)) != 0;
    /// <summary>FJOIN_TX (bit 31): double TX FIFO to 8 entries (RX disabled).</summary>
    public bool FifoJoinTx => (ShiftCtrl & (1u << 31)) != 0;
    /// <summary>FJOIN_RX (bit 30): double RX FIFO to 8 entries (TX disabled).</summary>
    public bool FifoJoinRx => (ShiftCtrl & (1u << 30)) != 0;
    /// <summary>Effective TX FIFO depth: 8 when FJOIN_TX, 0 when FJOIN_RX, else 4.</summary>
    public int TxDepth => FifoJoinTx ? 8 : FifoJoinRx ? 0 : 4;
    /// <summary>Effective RX FIFO depth: 0 when FJOIN_TX, 8 when FJOIN_RX, else 4.</summary>
    public int RxDepth => FifoJoinTx ? 0 : FifoJoinRx ? 8 : 4;

    // ── EXECCTRL helpers ──────────────────────────────────────────────
    /// <summary>Wrap top (inclusive): EXECCTRL bits [16:12].</summary>
    public uint WrapTop    => (ExecCtrl >> 12) & 0x1F;
    /// <summary>Wrap bottom: EXECCTRL bits [11:7].</summary>
    public uint WrapBottom => (ExecCtrl >> 7)  & 0x1F;
    public uint JmpPin     => (ExecCtrl >> 24) & 0x1F;
    /// <summary>STATUS_SEL: 0=TX FIFO, 1=RX FIFO — EXECCTRL bit 4.</summary>
    public uint StatusSel  => (ExecCtrl >> 4) & 1;
    /// <summary>STATUS_N: FIFO level threshold — EXECCTRL bits [3:0].</summary>
    public uint StatusN    => ExecCtrl & 0xF;
    /// <summary>Side-set enable (from program): EXECCTRL bit 30.</summary>
    public uint SideEn     => (ExecCtrl >> 30) & 1;
    /// <summary>Side-set pin dir (1=sets PINDIRS): EXECCTRL bit 29.</summary>
    public uint SidePinDir => (ExecCtrl >> 29) & 1;

    // ── PINCTRL helpers ───────────────────────────────────────────────
    /// <summary>Number of side-set bits: PINCTRL bits [31:29].</summary>
    public uint SidesetCount => (PinCtrl >> 29) & 7;
    /// <summary>Side-set base pin: PINCTRL bits [14:10].</summary>
    public uint SidesetBase  => (PinCtrl >> 10) & 0x1F;
    /// <summary>OUT pin count: PINCTRL bits [25:20].</summary>
    public uint OutCount    => (PinCtrl >> 20) & 0x3F;
    /// <summary>SET pin count: PINCTRL bits [28:26].</summary>
    public uint SetCount    => (PinCtrl >> 26) & 0x7;
    /// <summary>IN base pin: PINCTRL bits [19:15].</summary>
    public uint InBase       => (PinCtrl >> 15) & 0x1F;
    /// <summary>OUT base pin: PINCTRL bits [4:0].</summary>
    public uint OutBase      => PinCtrl & 0x1F;
    /// <summary>SET base pin: PINCTRL bits [9:5].</summary>
    public uint SetBase      => (PinCtrl >> 5) & 0x1F;
}

using RP2040.Peripherals.Pio;

namespace RP2040.TestKit.Probes;

/// <summary>
/// Captures the FIFO traffic of a PIO block's four state machines: every word the firmware pushes into
/// a state machine's TX FIFO and every word it pulls out of an RX FIFO, each with a simulated-time
/// stamp. PIO programs are arbitrary, so this is a per-state-machine FIFO capture rather than a
/// protocol decoder — but it tells you exactly what data crossed the SM boundary and when.
/// </summary>
public sealed class PioProbe
{
    /// <summary>One word that crossed a state machine's FIFO.</summary>
    public readonly record struct Word(long Cycle, int StateMachine, uint Value);

    private const int SmCount = 4;
    private readonly FifoCapture[] _tx = { new(), new(), new(), new() };
    private readonly FifoCapture[] _rx = { new(), new(), new(), new() };
    private readonly List<Word> _txWords = [];
    private readonly List<Word> _rxWords = [];
    private readonly Func<long> _clock;

    internal PioProbe(PioPeripheral pio, Func<long> clock)
    {
        _clock = clock;
        pio.OnTxPush += (sm, value) =>
        {
            var cycle = _clock();
            _txWords.Add(new Word(cycle, sm, value));
            for (int i = 0; i < 4; i++) _tx[sm].Record(cycle, (byte)(value >> (i * 8)));
        };
        pio.OnRxPull += (sm, value) =>
        {
            var cycle = _clock();
            _rxWords.Add(new Word(cycle, sm, value));
            for (int i = 0; i < 4; i++) _rx[sm].Record(cycle, (byte)(value >> (i * 8)));
        };
    }

    /// <summary>All words the firmware pushed into TX FIFOs (any state machine), in order.</summary>
    public IReadOnlyList<Word> TxWords => _txWords;

    /// <summary>All words the firmware pulled from RX FIFOs (any state machine), in order.</summary>
    public IReadOnlyList<Word> RxWords => _rxWords;

    /// <summary>Words pushed into state machine <paramref name="sm"/>'s TX FIFO.</summary>
    public IReadOnlyList<uint> TxOf(int sm) => _txWords.Where(w => w.StateMachine == sm).Select(w => w.Value).ToList();

    /// <summary>Words pulled from state machine <paramref name="sm"/>'s RX FIFO.</summary>
    public IReadOnlyList<uint> RxOf(int sm) => _rxWords.Where(w => w.StateMachine == sm).Select(w => w.Value).ToList();

    /// <summary>The little-endian byte stream pushed into a state machine's TX FIFO.</summary>
    public FifoCapture TxBytes(int sm) => _tx[sm];

    /// <summary>The little-endian byte stream pulled from a state machine's RX FIFO.</summary>
    public FifoCapture RxBytes(int sm) => _rx[sm];

    /// <summary>Clear all captured words.</summary>
    public void Clear()
    {
        _txWords.Clear();
        _rxWords.Clear();
        for (int i = 0; i < SmCount; i++) { _tx[i].Clear(); _rx[i].Clear(); }
    }
}

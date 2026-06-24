using System.Text;

namespace RP2040.TestKit.Probes;

/// <summary>
/// A timestamped capture of a byte stream flowing through a peripheral FIFO (UART/SPI/I2C TX or RX).
/// Each byte is recorded with the simulated cycle at which it passed, so a test can inspect both the
/// data and its timing.
/// </summary>
public sealed class FifoCapture
{
    /// <summary>One captured byte and the simulated cycle it was observed at.</summary>
    public readonly record struct Sample(long Cycle, byte Value);

    private readonly List<Sample> _samples = [];

    internal void Record(long cycle, byte value) => _samples.Add(new Sample(cycle, value));

    /// <summary>Every captured byte with its timestamp, in order.</summary>
    public IReadOnlyList<Sample> Samples => _samples;

    /// <summary>The captured bytes, without timestamps.</summary>
    public IReadOnlyList<byte> Bytes => _samples.Select(s => s.Value).ToList();

    /// <summary>Number of bytes captured.</summary>
    public int Count => _samples.Count;

    /// <summary>The captured bytes decoded as a string (one char per byte).</summary>
    public string Text => string.Concat(_samples.Select(s => (char)s.Value));

    /// <summary>The captured bytes as a space-separated hex string (e.g. "A0 0F 12").</summary>
    public string Hex() => string.Join(' ', _samples.Select(s => s.Value.ToString("X2")));

    /// <summary>True if the captured bytes contain <paramref name="sequence"/> as a contiguous run.</summary>
    public bool Contains(params byte[] sequence)
    {
        if (sequence.Length == 0) return true;
        for (int i = 0; i + sequence.Length <= _samples.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
                if (_samples[i + j].Value != sequence[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    /// <summary>Clear the capture.</summary>
    public void Clear() => _samples.Clear();
}

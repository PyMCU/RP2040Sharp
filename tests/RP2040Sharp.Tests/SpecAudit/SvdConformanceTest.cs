using RP2040.Peripherals;
using Xunit;
using FluentAssertions;

namespace RP2040.Tests.SpecAudit;

public class SvdConformanceTest
{
    private sealed record Register(string Name, uint Address, uint Reset, uint Fields, uint ReadOnly, uint ReadWrite,
                                   uint WriteOneToClear, uint Undefined, bool ReadSideEffect)
    {
        public string Peripheral => Name[..Name.IndexOf('.')];
    }

    private static IEnumerable<string> Lines(string resource)
    {
        using var stream = typeof(SvdConformanceTest).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException(resource);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') yield return line;
    }

    private static List<Register> Registers() => Lines("rp2040-registers.txt").Select(l =>
    {
        var f = l.Split(' ');
        uint H(int i) => Convert.ToUInt32(f[i], 16);
        return new Register(f[0], H(1), H(2), H(3), H(4), H(5), H(6), H(7), f[8] == "1");
    }).ToList();

    private static RP2040Machine Machine(bool released = true)
    {
        var m = new RP2040Machine();
        if (released) m.Bus.WriteWord(0x4000C000, 0);
        return m;
    }

    private static IEnumerable<string> Check(Register r, uint[] peripheral)
    {
        var a = r.Address;
        uint r0, r0b, roA, roB, r1, r2, r3;
        uint[] before, settled, after;
        using (var m = Machine(released: r.Peripheral != "RESETS"))
        {
            r0 = m.Bus.ReadWord(a); r0b = m.Bus.ReadWord(a);
        }
        using (var m = Machine())
        {
            before = peripheral.Select(m.Bus.ReadWord).ToArray();
            settled = peripheral.Select(m.Bus.ReadWord).ToArray();
            m.Bus.ReadWord(a); m.Bus.ReadWord(a); m.Bus.ReadWord(a);
            after = peripheral.Select(m.Bus.ReadWord).ToArray();
        }
        using (var m = Machine())
        {
            var v = m.Bus.ReadWord(a) & ~r.WriteOneToClear;
            m.Bus.WriteWord(a, v | r.ReadOnly); roA = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a, v & ~r.ReadOnly); roB = m.Bus.ReadWord(a);
        }
        using (var m = Machine())
        {
            m.Bus.WriteWord(a, 0xFFFFFFFF); r1 = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a, 0); r2 = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a, 0xFFFFFFFF); r3 = m.Bus.ReadWord(a);
        }
        var volatileBits = (r0 ^ r0b) | r.Undefined;

        if (((r0 ^ r.Reset) & r.Fields & ~volatileBits) != 0) yield return "RESET";
        if ((roA & ~roB & r.ReadOnly & ~volatileBits) != 0) yield return "RO_WRITABLE";
        if (((r0 | r1 | r2 | r3 | roA) & ~r.Fields) != 0) yield return "RESERVED";
        var stored = r1 & ~r2 & r.ReadWrite & ~volatileBits;
        if ((r.ReadWrite & ~volatileBits & ~stored) != 0) yield return "RW_NOT_STORED";
        for (var i = 0; i < peripheral.Length; i++)
            if (peripheral[i] != a && before[i] == settled[i] && after[i] != settled[i])
            {
                yield return "READ_SIDE_EFFECT";
                break;
            }

        if (r.WriteOneToClear != 0)
        {
            using var m = Machine();
            var b = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a, b & ~r.WriteOneToClear); var w0 = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a, b | r.WriteOneToClear); var w1 = m.Bus.ReadWord(a);
            if (((b ^ w0) & r.WriteOneToClear & ~volatileBits) != 0) yield return "W1C_WRITE0";
            if ((w1 & b & r.WriteOneToClear & ~volatileBits) != 0) yield return "W1C_NOT_CLEARED";
            if ((r1 & ~r2 & r.WriteOneToClear & ~r0 & ~volatileBits) != 0) yield return "W1C_STORED";
        }

        if (r.Peripheral == "SIO")
        {
            using var m = Machine();
            var b = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a + 0x2000, 0xFFFFFFFF); m.Bus.WriteWord(a + 0x3000, 0xFFFFFFFF);
            if (m.Bus.ReadWord(a) != b) yield return "SIO_ALIAS";
        }
        else if (r.Peripheral != "PPB" && stored != 0)
        {
            using var m = Machine();
            m.Bus.WriteWord(a, 0);
            m.Bus.WriteWord(a + 0x2000, 0xFFFFFFFF); var set = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a + 0x3000, 0xFFFFFFFF); var clr = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a + 0x1000, 0xFFFFFFFF); var x1 = m.Bus.ReadWord(a);
            m.Bus.WriteWord(a + 0x1000, 0xFFFFFFFF); var x2 = m.Bus.ReadWord(a);
            if (((stored & ~set) | (stored & clr) | (stored & ~x1) | (stored & x2)) != 0) yield return "ALIAS";
        }
    }

    [Fact]
    public void Registers_MatchTheSvd()
    {
        var registers = Registers().Where(r => !r.ReadSideEffect && r.Peripheral != "USB_DPRAM").ToList();
        var failures = new HashSet<string>();
        foreach (var group in registers.GroupBy(r => r.Peripheral))
        {
            var addresses = group.Select(r => r.Address).ToArray();
            foreach (var r in group)
                foreach (var check in Check(r, addresses))
                    failures.Add($"{r.Name} {check}");
        }

        var allowed = Lines("svd-allowlist.txt").Select(l => string.Join(' ', l.Split(' ').Take(2))).ToHashSet();
        var unexpected = failures.Except(allowed).Order().ToList();
        var stale = allowed.Except(failures).Order().ToList();
        unexpected.Should().BeEmpty("these registers no longer match the SVD");
        stale.Should().BeEmpty("these svd-allowlist.txt entries pass now and must be removed");
    }
}

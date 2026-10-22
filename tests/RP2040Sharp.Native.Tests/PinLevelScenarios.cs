using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>A pin-level bus program, the pads a host watches, the circuit it is wired to, and what it must receive.</summary>
internal sealed record BusScenario(string Name, byte[] Image, uint Watch, Action<BusHost> Wire, ulong Span,
    Func<Chip, IReadOnlyList<string>> Expected)
{
    public override string ToString() => Name;
}

/// <summary>The scenarios of the pin-level tests (hand-assembled firmware, see <see cref="PinLevelFirmware"/>).</summary>
internal static class PinLevelScenarios
{
    public const uint SpiPads = 1u << 17 | 1u << 18 | 1u << 19, UartPads = 1u << 0, I2cPads = 1u << 4 | 1u << 5;
    public const byte SlaveAddress = 0x4D;

    /// <summary>The scenarios by name (theory data must be serializable).</summary>
    public static readonly Dictionary<string, Func<BusScenario>> Catalog = new()
    {
        ["spi m0 d8 c20 poll"] = () => Spi(0, 8, 20, PinLevelFirmware.SpiVariant.Poll),
        ["spi m1 d8 c20 poll"] = () => Spi(1, 8, 20, PinLevelFirmware.SpiVariant.Poll),
        ["spi m2 d16 c12 poll"] = () => Spi(2, 16, 12, PinLevelFirmware.SpiVariant.Poll),
        ["spi m3 d4 c16 poll"] = () => Spi(3, 4, 16, PinLevelFirmware.SpiVariant.Poll),
        ["spi m0 d8 c20 dma"] = () => Spi(0, 8, 20, PinLevelFirmware.SpiVariant.Dma),
        ["spi m3 d16 c24 dma"] = () => Spi(3, 16, 24, PinLevelFirmware.SpiVariant.Dma),
        ["spi m0 d8 c20 irq"] = () => Spi(0, 8, 20, PinLevelFirmware.SpiVariant.Irq),
        ["spi m1 d12 c24 irq"] = () => Spi(1, 12, 24, PinLevelFirmware.SpiVariant.Irq),
        ["uart 8n1 125"] = () => Uart("8N1", 7, 52, 0x60),
        ["uart 7e2 125"] = () => Uart("7E2", 7, 52, 0x40 | 0x2 | 0x4 | 0x8),
        ["uart 5o1 52"] = () => Uart("5O1", 3, 16, 0x02),
        ["uart 6s0 125"] = () => Uart("6 stick-0", 7, 52, 0x20 | 0x2 | 0x4 | 0x80),
        ["i2c 40/50"] = () => I2c(40, 50, 10, absent: false),
        ["i2c 40/50 absent"] = () => I2c(40, 50, 10, absent: true),
        ["i2c 60/80 absent"] = () => I2c(60, 80, 20, absent: true),
    };

    public static IEnumerable<object[]> Names() => Catalog.Keys.Select(k => new object[] { k });

    /// <summary>SPI0 with MOSI (GP19) wired to MISO (GP16).</summary>
    public static BusScenario Spi(int mode, int dss, int cpsdvsr, PinLevelFirmware.SpiVariant variant, int bursts = 3) => new(
        $"spi mode {mode} dss {dss} div {cpsdvsr} {variant}",
        PinLevelFirmware.SpiBursts(mode, dss, cpsdvsr, variant, bursts: bursts),
        SpiPads,
        host => host.Wire(19, 16).Drive(16, 0),
        Span: (ulong)(bursts * 8 * (dss + 2) * cpsdvsr + 20_000),
        chip =>
        {
            int words = (variant == PinLevelFirmware.SpiVariant.Irq ? 4 : 8) * bursts;
            var lines = new List<string>();
            for (int i = 0; i < words; i++) lines.Add($"rx {i} {PinLevelFirmware.SpiWord(i, dss):X}");
            return lines;
        });

    /// <summary>The words an SPI scenario stored (bytes / halfwords for the DMA variant), as <see cref="Spi"/> expects them.</summary>
    public static IReadOnlyList<string> SpiReceived(Chip chip, int dss, PinLevelFirmware.SpiVariant variant, int bursts = 3)
    {
        int words = (variant == PinLevelFirmware.SpiVariant.Irq ? 4 : 8) * bursts;
        var bus = chip.Machine.Bus;
        var lines = new List<string>();
        for (int i = 0; i < words; i++)
        {
            uint v = variant != PinLevelFirmware.SpiVariant.Dma ? bus.ReadWord(PinLevelFirmware.RxBuffer + 4u * (uint)i)
                : dss > 8 ? bus.ReadHalfWord(PinLevelFirmware.RxBuffer + 2u * (uint)i)
                : bus.ReadByte(PinLevelFirmware.RxBuffer + (uint)i);
            lines.Add($"rx {i} {v:X}");
        }
        return lines;
    }

    /// <summary>UART0 with TX (GP0) wired to RX (GP1), the RX line idling high.</summary>
    public static BusScenario Uart(string format, uint ibrd, uint fbrd, uint lcrH, int bytes = 4, int bursts = 3) => new(
        $"uart {format} ibrd {ibrd} fbrd {fbrd}",
        PinLevelFirmware.UartBursts(ibrd, fbrd, lcrH, bytes, bursts: bursts),
        UartPads,
        host => host.Wire(0, 1).Drive(1, 1),
        Span: (ulong)(bursts * bytes * 13 * ((ibrd * 64 + fbrd) / 4) + 20_000),
        chip =>
        {
            int bits = 5 + (int)((lcrH >> 5) & 3);
            var lines = new List<string>();
            for (int k = 0; k < bytes * bursts; k++) lines.Add($"rx {k} {(uint)(0x5A + 0x25 * k) & ((1u << bits) - 1):X}");
            return lines;
        });

    public static IReadOnlyList<string> Words(Chip chip, int count)
    {
        var lines = new List<string>();
        for (int i = 0; i < count; i++) lines.Add($"rx {i} {chip.Machine.Bus.ReadWord(PinLevelFirmware.RxBuffer + 4u * (uint)i):X}");
        return lines;
    }

    /// <summary>I2C0 on GP4 / GP5 with a pull-up and a bit-level slave at 0x4D.</summary>
    public static BusScenario I2c(uint hcnt, uint lcnt, uint sdaHold, bool absent, int rounds = 2) => new(
        $"i2c hcnt {hcnt} lcnt {lcnt} hold {sdaHold}{(absent ? " absent" : "")}",
        PinLevelFirmware.I2cRounds(SlaveAddress, hcnt, lcnt, sdaHold, absent ? 0x42 : 0, rounds),
        I2cPads,
        host => host.I2c(4, 5, new I2cBitSlave(SlaveAddress)),
        Span: (ulong)(rounds * 120 * (hcnt + lcnt + 2) + 20_000),
        chip =>
        {
            var lines = new List<string>();
            int i = 0;
            for (int r = 0; r < rounds; r++)
            {
                lines.Add($"rx {i++} {r:X}");
                lines.Add($"rx {i++} {r ^ 0xFF:X}");
                if (absent) lines.Add($"rx {i++} 1");   // IC_TX_ABRT_SOURCE: ABRT_7B_ADDR_NOACK
            }
            return lines;
        });

    public static int I2cWords(bool absent, int rounds = 2) => rounds * (absent ? 3 : 2);

    /// <summary>What a scenario stored, in the form <see cref="BusScenario.Expected"/> gives.</summary>
    public static IReadOnlyList<string> Received(BusScenario sc, Chip chip)
    {
        var expected = sc.Expected(chip);
        if (sc.Name.StartsWith("spi"))
        {
            var p = sc.Name.Split(' ');
            var variant = Enum.Parse<PinLevelFirmware.SpiVariant>(p[^1]);
            int words = variant == PinLevelFirmware.SpiVariant.Irq ? 4 : 8;
            return SpiReceived(chip, int.Parse(p[4]), variant, expected.Count / words);
        }
        return Words(chip, expected.Count);
    }
}

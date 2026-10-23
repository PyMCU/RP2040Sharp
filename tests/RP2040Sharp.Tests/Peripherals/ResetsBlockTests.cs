using RP2040.Peripherals;

namespace RP2040Sharp.Tests.Peripherals;

public sealed class ResetsBlockTests
{
    private const uint Resets = 0x4000C000;
    private const uint Xor = 0x1000, Set = 0x2000, Clr = 0x3000;

    private static RP2040Machine Boot()
    {
        var m = new RP2040Machine();
        m.Bus.WriteWord(Resets, 0);
        return m;
    }

    [Theory]
    [InlineData(Set)]
    [InlineData(Xor)]
    [InlineData(0u)]
    public void I2c0_Reset_ClearsEnableAndFifo(uint alias)
    {
        var m = Boot();
        m.Bus.WriteWord(0x4004406C, 1);
        m.I2c0.InjectByte(0x42);
        m.Bus.ReadWord(0x40044078).Should().Be(1u);

        m.Bus.WriteWord(Resets + alias, 1u << 3);
        m.Bus.WriteWord(Resets + Clr, 1u << 3);

        m.Bus.ReadWord(0x4004406C).Should().Be(0u, "IC_ENABLE resets to 0");
        m.Bus.ReadWord(0x4004409C).Should().Be(0u, "IC_ENABLE_STATUS resets to 0");
        m.Bus.ReadWord(0x40044078).Should().Be(0u, "the RX FIFO is flushed");
        m.Bus.ReadWord(0x40044000).Should().Be(0x65u, "IC_CON returns to its reset value");
    }

    [Theory]
    [InlineData(4, 0x4004806Cu, 1u, 0u)]
    [InlineData(16, 0x4003C004u, 2u, 0u)]
    [InlineData(17, 0x40040004u, 2u, 0u)]
    [InlineData(22, 0x40034030u, 0x301u, 0u)]
    [InlineData(23, 0x40038030u, 0x301u, 0u)]
    [InlineData(14, 0x40050010u, 100u, 0xFFFFu)]
    [InlineData(0, 0x4004C010u, 0x100u, 0u)]
    [InlineData(2, 0x50000000u, 0x20000000u, 0u)]
    public void Block_Reset_RestoresRegister(int bit, uint register, uint written, uint expected)
    {
        var m = Boot();
        m.Bus.WriteWord(register, written);
        m.Bus.ReadWord(register).Should().Be(written);

        m.Bus.WriteWord(Resets + Set, 1u << bit);
        m.Bus.WriteWord(Resets + Clr, 1u << bit);

        m.Bus.ReadWord(register).Should().Be(expected);
    }

    [Fact]
    public void UnrelatedBit_LeavesI2cAlone()
    {
        var m = Boot();
        m.Bus.WriteWord(0x4004406C, 1);

        m.Bus.WriteWord(Resets + Set, 1u << 4);

        m.Bus.ReadWord(0x4004406C).Should().Be(1u);
    }
}

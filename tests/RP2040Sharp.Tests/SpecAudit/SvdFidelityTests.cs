using RP2040.Peripherals;
using Xunit;
using FluentAssertions;

namespace RP2040.Tests.SpecAudit;

public class SvdFidelityTests
{
    private static RP2040Machine Boot()
    {
        var m = new RP2040Machine();
        m.Bus.WriteWord(0x4000C000, 0);
        return m;
    }

    [Fact]
    public void Dma_NullTriggerOnCtrlTrig_WritesTheRegister()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000010, 0x003F8001);
        b.WriteWord(0x5000000C, 0);
        b.ReadWord(0x5000000C).Should().Be(0u);
    }

    [Fact]
    public void Dma_ClrAliasOnCtrlTrig_LeavesTheChannelDisabled()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000010, 0x003F8001);
        b.WriteWord(0x5000300C, 1);
        b.ReadWord(0x5000000C).Should().Be(0x003F8000u);
    }

    [Fact]
    public void Dma_CtrlErrorFlags_AreWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000010, 0xFE3F8000);
        b.ReadWord(0x50000010).Should().Be(0x003F8000u);
    }
}

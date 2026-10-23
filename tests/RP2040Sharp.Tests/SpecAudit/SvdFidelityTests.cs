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

    [Theory]
    [InlineData(0x1000u)]
    [InlineData(0x2000u)]
    [InlineData(0x3000u)]
    public void Adc_FcsAlias_KeepsTheStickyFlags(uint alias)
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x4004C000, 1);
        b.WriteWord(0x4004C008, 1);
        b.ReadWord(0x4004C00C);
        (b.ReadWord(0x4004C008) & (1u << 10)).Should().NotBe(0u);
        b.WriteWord(0x4004C008 + alias, 1u << 3);
        (b.ReadWord(0x4004C008) & (1u << 10)).Should().NotBe(0u, "hw_write_masked on FCS must not clear UNDER");
        b.WriteWord(0x4004C008, b.ReadWord(0x4004C008));
        (b.ReadWord(0x4004C008) & (1u << 10)).Should().Be(0u);
    }

    [Fact]
    public void SysInfo_Platform_IsAsic()
    {
        using var m = Boot();
        m.Bus.ReadWord(0x40000004).Should().Be(2u);
    }

    [Fact]
    public void Vreg_ResetsTo1V10Enabled()
    {
        using var m = Boot();
        m.Bus.ReadWord(0x40064000).Should().Be(0xB1u);
    }

    [Theory]
    [InlineData(0x40008004u)]
    [InlineData(0x40008034u)]
    [InlineData(0x40008040u)]
    [InlineData(0x40008058u)]
    [InlineData(0x40008064u)]
    [InlineData(0x40008070u)]
    public void Clocks_DividersResetToOne(uint div)
    {
        using var m = Boot();
        m.Bus.ReadWord(div).Should().Be(0x100u);
    }

    [Fact]
    public void Ssi_AtomicAliases_Work()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x18000014, 4);
        b.WriteWord(0x18002014, 2); b.ReadWord(0x18000014).Should().Be(6u);
        b.WriteWord(0x18003014, 4); b.ReadWord(0x18000014).Should().Be(2u);
        b.WriteWord(0x18001014, 3); b.ReadWord(0x18000014).Should().Be(1u);
    }

    [Fact]
    public void Rtc_ReadingRtc0_LatchesRtc1()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x4005C004, (2024u << 12) | (12u << 8) | 31u);
        b.WriteWord(0x4005C008, (23u << 16) | (59u << 8) | 59u);
        b.WriteWord(0x4005C00C, (1u << 4) | 1u);
        b.ReadWord(0x4005C01C);
        m.Rtc.Tick(125_000_000);
        (b.ReadWord(0x4005C018) & 0x1F).Should().Be(31u, "RTC_1 holds the date latched by the RTC_0 read");
        b.ReadWord(0x4005C01C);
        (b.ReadWord(0x4005C018) & 0x1F).Should().Be(1u);
    }

    [Fact]
    public void Busctrl_BusPriority_UsesTheSvdBits()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40030000, 0xFFFFFFFF);
        b.ReadWord(0x40030000).Should().Be(0x1111u);
    }

    [Fact]
    public void Sio_DividerWaitReadyLoop_Exits()
    {
        using var m = Boot(); var b = m.Bus;
        ushort[] code = { 0x4805, 0x2164, 0x6601, 0x2107, 0x6641, 0x6F81, 0x0849, 0xD3FC, 0xE7FE, 0, 0, 0 };
        for (var i = 0; i < code.Length; i++) b.WriteHalfWord(0x20000000u + (uint)i * 2, code[i]);
        b.WriteWord(0x20000018, 0xD0000000);
        m.Cpu.Registers.PC = 0x20000000; m.Cpu.Registers.SP = 0x20040000;
        m.Run(200);
        m.Cpu.Registers.PC.Should().Be(0x20000010u);
        b.ReadWord(0xD0000070).Should().Be(14u);
    }
}

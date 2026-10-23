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

    [Fact]
    public void Uart_ReadingDr_PopsTheRxFifo()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40034024, 1); b.WriteWord(0x4003402C, 0x70); b.WriteWord(0x40034030, 0x301);
        m.Uart0.InjectByte(0x41); m.Uart0.InjectByte(0x42);
        (b.ReadWord(0x40034000) & 0xFF).Should().Be(0x41u);
        (b.ReadWord(0x40034000) & 0xFF).Should().Be(0x42u);
        (b.ReadWord(0x40034018) & 0x10).Should().NotBe(0u, "RXFE");
    }

    [Fact]
    public void Spi_ReadingDr_PopsTheRxFifo()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x4003C010, 2); b.WriteWord(0x4003C000, 7); b.WriteWord(0x4003C004, 3);
        b.WriteWord(0x4003C008, 0x55); b.WriteWord(0x4003C008, 0xAA);
        m.Run(5000);
        b.ReadWord(0x4003C008).Should().Be(0x55u);
        b.ReadWord(0x4003C008).Should().Be(0xAAu);
        (b.ReadWord(0x4003C00C) & 4).Should().Be(0u, "RNE");
    }

    [Fact]
    public void Pio_ReadingRxf_PopsAndUnderflowIsWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        m.Pio0.InjectRxData(0, 0x111); m.Pio0.InjectRxData(0, 0x222);
        b.ReadWord(0x50200020).Should().Be(0x111u);
        b.ReadWord(0x50200020).Should().Be(0x222u);
        b.ReadWord(0x50200020);
        var fdebug = b.ReadWord(0x50200008);
        (fdebug & 0x100).Should().NotBe(0u, "RXUNDER");
        b.WriteWord(0x50200008, 0); b.ReadWord(0x50200008).Should().Be(fdebug);
        b.WriteWord(0x50200008, 0xFFFFFFFF); b.ReadWord(0x50200008).Should().Be(0u);
    }

    [Fact]
    public void Sio_FifoRead_PopsAndFifoStatusErrorsAreWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        m.Sio.InjectFifoRx(1); m.Sio.InjectFifoRx(2);
        b.ReadWord(0xD0000058).Should().Be(1u);
        b.ReadWord(0xD0000058).Should().Be(2u);
        b.ReadWord(0xD0000058);
        (b.ReadWord(0xD0000050) & 8).Should().NotBe(0u, "ROE");
        b.WriteWord(0xD0000050, 0); (b.ReadWord(0xD0000050) & 8).Should().NotBe(0u);
        b.WriteWord(0xD0000050, 0xC); (b.ReadWord(0xD0000050) & 8).Should().Be(0u);
    }

    [Fact]
    public void Sio_ReadingASpinlock_ClaimsIt()
    {
        using var m = Boot(); var b = m.Bus;
        b.ReadWord(0xD0000100).Should().NotBe(0u);
        b.ReadWord(0xD0000100).Should().Be(0u);
        (b.ReadWord(0xD000005C) & 1).Should().Be(1u);
        b.WriteWord(0xD0000100, 0);
        b.ReadWord(0xD0000100).Should().NotBe(0u);
    }

    [Fact]
    public void Adc_ReadingFifo_PopsIt()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x4004C000, 1); m.Run(100);
        b.WriteWord(0x4004C008, 1); b.WriteWord(0x4004C000, 1u | (1u << 2)); m.Run(2000);
        ((b.ReadWord(0x4004C008) >> 16) & 0xF).Should().Be(1u);
        b.ReadWord(0x4004C00C);
        ((b.ReadWord(0x4004C008) >> 16) & 0xF).Should().Be(0u);
    }

    [Fact]
    public void Timer_ReadingTimelr_LatchesTimehr_AndIntrIsWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40054004, 0xFFFFFFF0); b.WriteWord(0x40054000, 7);
        b.ReadWord(0x4005400C);
        b.ReadWord(0x40054008).Should().Be(7u);
        var now = b.ReadWord(0x40054028);
        b.WriteWord(0x40054038, 1); b.WriteWord(0x40054010, now + 5); m.Run(2000);
        b.ReadWord(0x40054034).Should().Be(1u);
        b.WriteWord(0x40054034, 0); b.ReadWord(0x40054034).Should().Be(1u);
        b.WriteWord(0x40054034, 1); b.ReadWord(0x40054034).Should().Be(0u);
    }

    [Fact]
    public void Pwm_IntrIsWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40050010, 10); b.WriteWord(0x40050004, 0x10); b.WriteWord(0x40050000, 1); m.Run(500);
        (b.ReadWord(0x400500A4) & 1).Should().Be(1u);
        b.WriteWord(0x400500A4, 0); (b.ReadWord(0x400500A4) & 1).Should().Be(1u);
        b.WriteWord(0x40050000, 0); b.WriteWord(0x400500A4, 1); (b.ReadWord(0x400500A4) & 1).Should().Be(0u);
    }

    [Fact]
    public void IoBank0_EdgeIntrIsWriteOneToClear()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40014014, 5);
        m.IoBank0.SetExternalInput(2, true); m.IoBank0.SetExternalInput(2, false);
        (b.ReadWord(0x400140F0) & 0xC00).Should().Be(0xC00u);
        b.WriteWord(0x400140F0, 0); (b.ReadWord(0x400140F0) & 0xC00).Should().Be(0xC00u);
        b.WriteWord(0x400140F0, 0xC00); (b.ReadWord(0x400140F0) & 0xC00).Should().Be(0u);
    }

    [Fact]
    public void Dma_SetAliasOnInte0_KeepsIntr()
    {
        using var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000000, 0x20001000); b.WriteWord(0x50000004, 0x20002000); b.WriteWord(0x50000008, 1);
        b.WriteWord(0x5000000C, (0x3Fu << 15) | (2u << 2) | 1u);
        b.ReadWord(0x50000400).Should().Be(1u);
        b.WriteWord(0x50002404, 2);
        b.ReadWord(0x50000400).Should().Be(1u);
        b.ReadWord(0x50000404).Should().Be(2u);
    }
}

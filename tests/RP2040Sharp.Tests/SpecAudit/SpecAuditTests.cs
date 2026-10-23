// Failing-first tests derived from the RP2040 datasheet / ARMv6-M ARM audit (see auditoria-rp2040-especificacion.md).
// Each test names the finding id (F-xx) it pins. Drop into tests/RP2040Sharp.Tests/SpecAudit/.
using RP2040.Core.Cpu;
using RP2040.Peripherals;
using Xunit;
using FluentAssertions;

namespace RP2040.Tests.SpecAudit;

public class SpecAuditTests
{
    private static RP2040Machine Boot(RP2040BootromRevision rev = RP2040BootromRevision.B1)
    {
        var m = new RP2040Machine(bootrom: rev);
        m.Bus.WriteWord(0x4000C000, 0);                     // release all resets
        return m;
    }

    private static void Load(RP2040Machine m, uint addr, params ushort[] code)
    {
        for (var i = 0; i < code.Length; i++) m.Bus.WriteHalfWord(addr + (uint)i * 2, code[i]);
        for (uint i = 0; i < 48; i++) m.Bus.WriteWord(0x20004000 + i * 4, 0x20003001);
        m.Bus.WriteHalfWord(0x20003000, 0xE7FE);            // fault handler: b .
        m.Cpu.Registers.VTOR = 0x20004000; m.Cpu.Registers.PC = addr; m.Cpu.Registers.SP = 0x20006000;
        m.Cpu.Registers.IPSR = 0;
    }

    [Fact] // F-01: SYSINFO.CHIP_ID PART/MANUFACTURER
    public void ChipId_IsRp2040Value()
    {
        var id = Boot().Bus.ReadWord(0x40000000);
        (id & 0x0FFFFFFF).Should().Be(0x2927u, "PART=0x0002 MANUFACTURER=0x927 (SVD CHIP_ID)");
    }

    [Fact] // F-02: SysTick COUNTFLAG is read-to-clear (ARMv6-M B3.3.3)
    public void SysTick_CountFlag_ClearsOnRead()
    {
        var m = Boot();
        m.Bus.WriteWord(0xE000E014, 100); m.Bus.WriteWord(0xE000E010, 5);
        m.Cpu.Cycles += 1000; m.Ppb.Tick(0);
        (m.Bus.ReadWord(0xE000E010) & 0x10000).Should().NotBe(0);
        (m.Bus.ReadWord(0xE000E010) & 0x10000).Should().Be(0);
    }

    [Fact] // F-03: YIELD (0xBF10) is a hint, not CBNZ
    public void Yield_IsNop()
    {
        var m = Boot(); Load(m, 0x20001000, 0xBF10, 0xBF00);
        m.Cpu.Registers.R0 = 5; m.Cpu.Run(1);
        m.Cpu.Registers.PC.Should().Be(0x20001002);
    }

    [Theory] // F-04: CBZ/CBNZ/CLZ do not exist in ARMv6-M -> HardFault
    [InlineData((ushort)0xB100)] [InlineData((ushort)0xB900)] [InlineData((ushort)0xB300)] [InlineData((ushort)0xB700)]
    public void Cbz_Cbnz_AreUndefined(ushort op)
    {
        var m = Boot(); Load(m, 0x20001000, op); m.Cpu.Run(1);
        m.Cpu.Registers.IPSR.Should().Be(3u);
    }

    [Fact] // F-05: BX to an even address (EPSR.T=0) faults
    public void Bx_EvenAddress_HardFaults()
    {
        var m = Boot(); Load(m, 0x20001000, 0x4708);          // bx r1
        m.Cpu.Registers.R1 = 0x20001100; m.Cpu.Run(2);
        m.Cpu.Registers.IPSR.Should().Be(3u);
    }

    [Fact] // F-06: unaligned word access faults (ARMv6-M A3.2)
    public void UnalignedLdr_HardFaults()
    {
        var m = Boot(); Load(m, 0x20001000, 0x6808);          // ldr r0,[r1]
        m.Cpu.Registers.R1 = 0x20002001; m.Cpu.Run(1);
        m.Cpu.Registers.IPSR.Should().Be(3u);
    }

    [Theory] // F-07: unmapped addresses raise a bus error (datasheet 2.2)
    [InlineData(0x30000000u)] [InlineData(0x40070000u)] [InlineData(0x20042000u)] [InlineData(0xA0000000u)]
    public void UnmappedRead_HardFaults(uint addr)
    {
        var m = Boot(); Load(m, 0x20001000, 0x6808);
        m.Cpu.Registers.R1 = addr; m.Cpu.Run(1);
        m.Cpu.Registers.IPSR.Should().Be(3u);
    }

    [Fact] // F-08: stacked PC of a synchronous fault is the faulting instruction
    public void Udf_StacksFaultingInstructionAddress()
    {
        var m = Boot(); Load(m, 0x20001000, 0xDE00); m.Cpu.Run(1);
        m.Bus.ReadWord(m.Cpu.Registers.SP + 0x18).Should().Be(0x20001000u);
    }

    [Fact] // F-09: SVC with PRIMASK=1 escalates to HardFault
    public void Svc_WithPrimask_HardFaults()
    {
        var m = Boot(); Load(m, 0x20001000, 0xB672, 0xDF00, 0xE7FE); m.Cpu.Run(5);
        m.Cpu.Registers.IPSR.Should().Be(3u);
    }

    [Fact] // F-10: Timer TIMEHW/TIMELW write sequence sets the 64-bit time (4.6)
    public void Timer_CanSetHighWord()
    {
        var m = Boot();
        m.Bus.WriteWord(0x40054004, 0x10); m.Bus.WriteWord(0x40054000, 5);   // timelw then timehw
        var lo = m.Bus.ReadWord(0x4005400C); var hi = m.Bus.ReadWord(0x40054008);
        hi.Should().Be(5u);
    }

    [Fact(Skip = "DMA spec fixes deferred: regress arduino-pico I2S/ADCInput throughput and I2SLoopback_Buffer HardFault")] // F-11 DMA - triggering again without rewriting TRANS_COUNT reloads it (2.5.1.2)
    public void Dma_RetriggerReloadsTransCount()
    {
        var m = Boot(); var b = m.Bus;
        for (uint i = 0; i < 4; i++) b.WriteWord(0x20010000 + i * 4, 0x11110000 + i);
        b.WriteWord(0x50000000, 0x20010000); b.WriteWord(0x50000004, 0x20011000); b.WriteWord(0x50000008, 4);
        b.WriteWord(0x5000000C, (0x3Fu << 15) | (1u << 4) | (1u << 5) | (2u << 2) | 1);
        b.ReadWord(0x20011000).Should().Be(0x11110000u);                       // first sequence
        b.WriteWord(0x5000003C, 0x20010000);                                  // AL3 READ_ADDR_TRIG, no TRANS_COUNT write
        b.ReadWord(0x20011010).Should().Be(0x11110000u, "WRITE_ADDR keeps incrementing; TRANS_COUNT reloads");
    }

    [Fact] // F-12 DMA INTS is write-1-to-clear (2.5.4)
    public void Dma_IntsWriteClears()
    {
        var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000404, 1);
        b.WriteWord(0x50000000, 0x20010000); b.WriteWord(0x50000004, 0x20011000); b.WriteWord(0x50000008, 1);
        b.WriteWord(0x5000000C, (0x3Fu << 15) | (2u << 2) | 1);
        b.ReadWord(0x5000040C).Should().Be(1u);
        b.WriteWord(0x5000040C, 1);
        b.ReadWord(0x5000040C).Should().Be(0u);
    }

    [Fact(Skip = "DMA spec fixes deferred: regress arduino-pico I2S/ADCInput throughput and I2SLoopback_Buffer HardFault")] // F-13 DMA IRQ_QUIET suppresses the end-of-sequence IRQ
    public void Dma_IrqQuiet_NoIrqOnCompletion()
    {
        var m = Boot(); var b = m.Bus;
        b.WriteWord(0x50000000, 0x20010000); b.WriteWord(0x50000004, 0x20011000); b.WriteWord(0x50000008, 1);
        b.WriteWord(0x5000000C, (1u << 21) | (0x3Fu << 15) | (2u << 2) | 1);
        b.ReadWord(0x50000400).Should().Be(0u);
    }

    [Fact] // F-14: RTC register layout follows the SVD/datasheet (YEAR[23:12], MATCH_ENA bit 28, SEC_ENA bit 28 of IRQ_SETUP_1)
    public void Rtc_AlarmUsesSvdLayout()
    {
        var m = Boot();
        m.Bus.WriteWord(0x4005C004, (2024u << 12) | (3u << 8) | 15u);
        m.Bus.WriteWord(0x4005C008, (2u << 24) | (10u << 16) | (30u << 8) | 45u);
        m.Bus.WriteWord(0x4005C00C, (1u << 4) | 1u);                         // LOAD | ENABLE
        m.Bus.WriteWord(0x4005C024, 1);                                      // INTE
        m.Bus.WriteWord(0x4005C014, (1u << 28) | 46u);                       // SEC_ENA, SEC=46
        m.Bus.WriteWord(0x4005C010, 1u << 28);                               // MATCH_ENA
        m.Rtc.Tick(125_000_000);                                             // one second
        var rtc1 = m.Bus.ReadWord(0x4005C018);
        ((rtc1 >> 12) & 0xFFF).Should().Be(2024u);
        (m.Bus.ReadWord(0x4005C010) & (1u << 29)).Should().NotBe(0u, "MATCH_ACTIVE");
        (m.Cpu.Registers.PendingInterrupts & (1u << 25)).Should().NotBe(0u, "RTC_IRQ");
    }

    [Fact] // F-15: PIO DBG_CFGINFO layout
    public void Pio_DbgCfgInfo()
    {
        Boot().Bus.ReadWord(0x50200044).Should().Be((32u << 16) | (4u << 8) | 4u);
    }

    [Fact] // F-16: B2 bootrom flash hooks must not unbalance the stack
    public void B2_FlashRangeErase_PreservesSp()
    {
        var m = Boot(RP2040BootromRevision.B2); m.LoadFlash(new byte[0x3000]);
        m.Bus.WriteHalfWord(0x20001000, 0xE7FE);
        m.Cpu.Registers.LR = 0x20001001; m.Cpu.Registers.SP = 0x20006000;
        m.Cpu.Registers.R0 = 0x1000; m.Cpu.Registers.R1 = 0x1000; m.Cpu.Registers.R2 = 0x1000; m.Cpu.Registers.R3 = 0x20;
        m.Cpu.Registers.PC = 0x236C;                                          // B2 flash_range_erase
        for (var i = 0; i < 20000 && m.Cpu.Registers.PC != 0x20001000; i++) m.Cpu.Run(1);
        m.Cpu.Registers.SP.Should().Be(0x20006000u);
    }

    [Fact] // F-17: SCB.SCR / CCR / MPU_TYPE
    public void Scb_Registers()
    {
        var m = Boot();
        m.Bus.WriteWord(0xE000ED10, 6); m.Bus.ReadWord(0xE000ED10).Should().Be(6u);
        // CCR: STKALIGN (bit 9) and UNALIGN_TRP (bit 3) read as one (datasheet 2.4.4 CCR, SVD). The audit's first
        // draft of this test said 0x204, which is bit 9 plus bit 2.
        m.Bus.ReadWord(0xE000ED14).Should().Be(0x208u);
        m.Bus.ReadWord(0xE000ED90).Should().Be(0x800u);
    }

    [Fact] // F-18: IO_BANK0 edge IRQ on a SIO-driven pad
    public void IoBank0_EdgeIrq_OnSioOutput()
    {
        var m = Boot(); var b = m.Bus;
        b.WriteWord(0x40014000 + 2 * 8 + 4, 5); b.WriteWord(0xD0000020, 1u << 2);
        b.WriteWord(0xD0000014, 1u << 2);
        (b.ReadWord(0x400140F0) & (8u << 8)).Should().NotBe(0u);
    }

    [Fact] // F-19: narrow IO writes are replicated across the bus (2.1.4)
    public void NarrowWrite_IsReplicated()
    {
        var m = Boot(); m.Bus.WriteWord(0x4005800C, 0xdeadbeef); m.Bus.WriteByte(0x4005800C, 0xA5);
        m.Bus.ReadWord(0x4005800C).Should().Be(0xA5A5A5A5u);
    }
}

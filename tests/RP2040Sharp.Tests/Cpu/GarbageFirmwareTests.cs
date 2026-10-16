using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using Xunit;
using FluentAssertions;

namespace RP2040.tests.Cpu;

/// <summary>
/// Hostile-firmware robustness: executing garbage must fault the EMULATED core (HardFault / lockup) and
/// never throw out of <c>Run</c> nor touch memory outside the emulator's native buffers. The core is
/// embedded in third-party hosts (iCircuit), so a host exception is a crash of the embedding application
/// and an out-of-buffer write is host heap corruption. Mirrors RP2350Sharp's GarbageFetchTests.
/// </summary>
public unsafe class GarbageFirmwareTests
{
    // Addresses worth landing the PC on: every region boundary, unmapped space, peripheral space,
    // XIP/SSI windows, SRAM end, the PPB, and the top of the address space.
    private static readonly uint[] InterestingPcs =
    [
        0x00000000, 0x00003FFE, 0x00004000, 0x00FFFFFE, 0x01000000, 0x0FFFFFFE,
        0x10000000, 0x101FFFFE, 0x10200000, 0x11000000, 0x17FFFFFE, 0x18000000, 0x1BFFFFFE,
        0x1C000000, 0x1FFFFFFE,
        0x20000000, 0x2003FFFE, 0x20040000, 0x2007FFFE, 0x20080000, 0x200FFFFE, 0x20100000, 0x2FFFFFFE,
        0x30000000, 0x40000000, 0x40008000, 0x40010000, 0x50000000, 0x50100000, 0x60000000, 0x7FFFFFFE,
        0x80000000, 0xA0000000, 0xC0000000, 0xD0000000, 0xD0000004, 0xE0000000, 0xE000ED00, 0xE000EDF0,
        0xE0080000, 0xEFFFFFFE, 0xF0000000, 0xFE000000, 0xFEFFFFFE, 0xFFFFFFF0, 0xFFFFFFFC, 0xFFFFFFFE,
    ];

    private const uint SramTop = 0x20080000;                 // first address past the 512 KB backing buffer
    private const uint SramEnd = 0x20042000;                 // first address past the 264 KiB the chip has

    private static void Run(CortexM0Plus cpu, int instructions, string what)
    {
        var act = () => cpu.Run(instructions);
        act.Should().NotThrow(what);
    }

    // HardFault entry logs to stderr; the fuzz would otherwise emit thousands of lines.
    private static T QuietStderr<T>(Func<T> body)
    {
        var saved = Console.Error;
        Console.SetError(TextWriter.Null);
        try { return body(); }
        finally { Console.SetError(saved); }
    }

    [Fact]
    public void Pc_on_unmapped_or_peripheral_space_hardfaults_without_a_host_exception()
    {
        QuietStderr(() =>
        {
            foreach (var pc in InterestingPcs)
            {
                using var bus = new BusInterconnect();
                var cpu = new CortexM0Plus(bus);
                cpu.Registers.VTOR = 0x20000000;
                bus.WriteWord(0x20000000 + 3 * 4, 0x20000801);
                bus.WriteHalfWord(0x20000800, 0xE7FE);               // handler: b .
                cpu.Registers.SP = 0x20040000;
                cpu.Registers.PC = pc;
                Run(cpu, 64, $"PC=0x{pc:X8}");
            }
            return 0;
        });
    }

    [Theory]
    [InlineData(0x30000000u)]   // unmapped region
    [InlineData(0x40000000u)]   // APB peripheral space
    [InlineData(0x50000000u)]   // AHB peripheral space
    [InlineData(0xD0000000u)]   // SIO
    [InlineData(0xE0000000u)]   // PPB
    [InlineData(0xF0000000u)]   // unmapped
    public void Hardfault_vector_in_same_unfetchable_region_locks_up_instead_of_dereferencing_null(uint region)
    {
        // The fault handler itself sits in the unfetchable region the PC faulted in. The first fault
        // refreshed the cached region id to that region with a null fetch pointer; the next iteration
        // matched the cached id, skipped the region-change test, and read through the null pointer.
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            cpu.Registers.VTOR = 0x20000000;
            bus.WriteWord(0x20000000 + 3 * 4, region + 0x101);     // HardFault vector -> same region
            cpu.Registers.SP = 0x20040000;
            cpu.Registers.PC = region;
            cpu.OnLockup = (_, _) => { };
            Run(cpu, 1000, $"region 0x{region:X8}");
            cpu.IsLockedUp.Should().BeTrue("a HardFault whose handler cannot be fetched is a lockup");
            return 0;
        });
    }

    [Fact]
    public void Reset_into_an_unfetchable_region_hardfaults_instead_of_dereferencing_null()
    {
        // Reset() primes the fetch cache from the reset vector; a vector pointing at a peripheral window
        // left a matching region id with a null pointer for the very first Run.
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            *(uint*)bus.PtrBootRom = 0x20040000;                    // region 0 is read-only to the guest
            *(uint*)(bus.PtrBootRom + 4) = 0x40000101;
            var cpu = new CortexM0Plus(bus);
            cpu.OnLockup = (_, _) => { };
            Run(cpu, 100, "reset vector in APB space");
            return 0;
        });
    }

    [Fact]
    public void Wide_or_misaligned_pc_at_the_end_of_a_region_does_not_throw()
    {
        QuietStderr(() =>
        {
            foreach (var pc in new uint[] { 0x1FFFFFFE, 0x101FFFFE, 0x2007FFFE, 0x20080000 - 2, 0x00003FFE,
                                            0x20000001, 0x20000003, 0x10000001, 0x40000001, 0x2007FFFF, 0x101FFFFF })
            {
                using var bus = new BusInterconnect();
                var cpu = new CortexM0Plus(bus);
                bus.WriteHalfWord(pc & ~1u, 0xF000);
                cpu.Registers.SP = 0x20040000;
                cpu.Registers.PC = pc;
                Run(cpu, 16, $"PC=0x{pc:X8}");
            }
            return 0;
        });
    }

    // ── Host-heap safety: nothing guest-controlled may touch memory past a backing store ────────────
    // The masked address is only the START of an access; the bus then reads/writes a whole halfword/word
    // (and the multi-register fast paths a whole burst) from it. These pin the two overflow classes: the
    // last bytes of a buffer (tail padding) and a burst that starts near the top of the SRAM window (the
    // fast paths must fall back to the wrapping per-word path).

    private static void AssertPadUntouched(BusInterconnect bus, string what)
    {
        for (var i = 0; i < RandomAccessMemory.TailPad; i++)
            (*(bus.PtrSram + 0x80000 + i)).Should().Be(0, $"{what}: byte {i} past the SRAM buffer");
    }

    [Fact]
    public void Unaligned_access_at_the_last_bytes_of_every_store_stays_inside_the_allocation()
    {
        using var bus = new BusInterconnect();
        // Datasheet 2.2: past the 264 KiB of SRAM nothing is mapped. The lenient accessors read 0 and drop the
        // write (no wrap into the buffer behind it); the Try forms the CPU uses report the bus error.
        bus.WriteWord(0x2007FFFF, 0xAABBCCDD);
        bus.PtrSram[0x7FFFF].Should().Be(0, "an unmapped write lands nowhere");
        bus.ReadWord(0x2007FFFF).Should().Be(0u);
        bus.TryWriteWord(SramEnd, 1).Should().BeFalse();
        bus.TryReadWord(SramEnd, out _).Should().BeFalse();
        // A word at the very last byte of SRAM spills into the allocation behind it, never past it
        bus.WriteWord(SramEnd - 1, 0xAABBCCDD);
        bus.ReadWord(SramEnd - 1).Should().Be(0xAABBCCDD);
        bus.WriteWord(0x00003FFF, 0x11223344);                   // boot ROM is writable only via the pointer
        bus.ReadWord(0x00003FFF).Should().Be(0u, "region 0 is read-only to the guest");
        bus.PtrFlash[0x1FFFFF] = 0xFF;
        bus.ReadWord(0x101FFFFF).Should().Be(0x000000FF, "flash read at its last byte: the byte, then the zeroed padding");
        RandomAccessMemory.TailPad.Should().BeGreaterThanOrEqualTo(8);
    }

    // A burst that starts inside SRAM and runs off its end faults at the first word the chip has no memory
    // for, instead of wrapping to the start of SRAM (the old 512 KB mirror). These keep the host-heap property
    // the wrap tests pinned: the burst must never touch memory outside the buffers.

    [Theory]
    [InlineData(0xB4FFu, "PUSH {r0-r7}")]
    [InlineData(0xB5FFu, "PUSH {r0-r7,lr}")]
    public void Push_burst_running_off_the_end_of_sram_faults_instead_of_wrapping(uint opcode, string what)
    {
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            bus.WriteHalfWord(0x20000100, (ushort)opcode);
            for (var r = 0; r < 8; r++) cpu.Registers[r] = 0xA0 + (uint)r;
            cpu.Registers.LR = 0xEE;
            var count = (opcode & 0x100) != 0 ? 9u : 8u;
            cpu.Registers.SP = SramEnd - 16 + count * 4;        // new SP = end - 16: the burst crosses the end
            cpu.Registers.PC = 0x20000100;
            cpu.Run(1);
            AssertPadUntouched(bus, what);
            cpu.Registers.IPSR.Should().Be(3u, $"{what}: the word past SRAM has no memory behind it");
            bus.ReadWord(0x20000000).Should().Be(0u, $"{what}: nothing wraps to the start of SRAM");
            return 0;
        });
    }

    [Fact]
    public void Stmia_burst_running_off_the_end_of_sram_faults_without_writing_back()
    {
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            bus.WriteHalfWord(0x20000100, 0xC0FF);                   // STMIA r0!, {r0-r7}
            for (var r = 1; r < 8; r++) cpu.Registers[r] = 0xA0 + (uint)r;
            cpu.Registers[0] = SramEnd - 16;
            cpu.Registers.PC = 0x20000100;
            cpu.Run(1);
            AssertPadUntouched(bus, "STMIA");
            cpu.Registers.IPSR.Should().Be(3u);
            cpu.Registers[0].Should().Be(SramEnd - 16, "the base is not written back by a faulting STMIA");
            bus.ReadWord(0x20000000).Should().Be(0u, "r4 does not wrap to the start of SRAM");
            return 0;
        });
    }

    [Theory]
    [InlineData(0xC8FFu, "LDMIA r0!, {r0-r7}")]
    [InlineData(0xBCFFu, "POP {r0-r7}")]
    [InlineData(0xBDFFu, "POP {r0-r7,pc}")]
    public void Load_burst_running_off_the_end_of_sram_faults_instead_of_reading_wrapped_words(uint opcode, string what)
    {
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            bus.WriteHalfWord(0x20000100, (ushort)opcode);
            bus.WriteWord(0x20000000, 0x12345678);                   // where the old mirror wrapped the 5th word
            var pop = (opcode & 0xF000) == 0xB000;
            if (pop) cpu.Registers.SP = SramEnd - 16; else cpu.Registers[0] = SramEnd - 16;
            cpu.Registers.PC = 0x20000100;
            cpu.Run(1);
            AssertPadUntouched(bus, what);
            cpu.Registers.IPSR.Should().Be(3u, $"{what}: the 5th word is past SRAM");
            cpu.Registers[4].Should().NotBe(0x12345678, $"{what}: the 5th word is not read from the wrapped start of SRAM");
            return 0;
        });
    }

    [Fact]
    public void Exception_stacking_with_sp_at_the_top_of_sram_stays_inside_the_buffer()
    {
        // Frame is written word by word through the bus; the last words of a frame placed at the very top
        // must still land inside the allocation (or its tail padding for an unaligned SP).
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            cpu.Registers.VTOR = 0x20000000;
            bus.WriteWord(0x2000000C, 0x20000101);
            bus.WriteHalfWord(0x20000100, 0xE7FE);
            foreach (var sp in new uint[] { SramTop, SramTop + 1, SramTop + 3, 0x20000001, 0x20000003 })
            {
                cpu.Registers.SP = sp;
                cpu.Registers.IPSR = 0;
                cpu.Registers.PC = 0x20000200;
                cpu.ExceptionEntry(3);
                cpu.ExceptionReturn(0xFFFFFFF9);
            }
            // An unaligned frame word may spill into the padding (at most 3 bytes), never past it.
            for (var i = 4; i < RandomAccessMemory.TailPad; i++)
                (*(bus.PtrSram + 0x80000 + i)).Should().Be(0, $"byte {i} past the SRAM buffer");
            return 0;
        });
    }

    // ── Bootrom flash helpers (guest-controlled offset/count) ───────────────────────────────────────

    private static RP2040Machine MachineWithBootrom()
    {
        var machine = new RP2040Machine();
        var image = new byte[0x200];
        BitConverter.GetBytes(0x20040000u).CopyTo(image, 0x100);
        BitConverter.GetBytes(0x10000101u).CopyTo(image, 0x104);
        machine.LoadFlash(image);
        return machine;
    }

    [Theory]
    [InlineData(0x237Cu)]   // flash_range_erase(offs, count)
    [InlineData(0x23C4u)]   // flash_range_program(offs, data, count)
    public void Bootrom_flash_hooks_with_overflowing_count_stay_inside_the_flash_buffer(uint hookPc)
    {
        // offset + count overflowed int and slipped past the bounds check, then filled/copied up to 2 GB
        // starting inside the flash buffer (host heap write).
        using var machine = MachineWithBootrom();
        var cpu = machine.Cpu;
        cpu.Registers.R0 = 0x100;
        cpu.Registers.R1 = hookPc == 0x237C ? 0x7FFFFF00u : 0x20000000u;
        cpu.Registers.R2 = 0x7FFFFF00u;
        cpu.Registers.LR = 0x20000001;
        cpu.Registers.PC = hookPc;
        Run(cpu, 1, "hook");
        for (var i = 0; i < RandomAccessMemory.TailPad; i++)
            (*(machine.Bus.PtrFlash + machine.Bus.FlashSize + i)).Should().Be(0, $"byte {i} past the flash buffer");
    }

    [Theory]
    [InlineData(0x20u, 0x800000u)]   // 4 KB sector erase, address beyond the 2 MB flash
    [InlineData(0x52u, 0xFFFFFFu)]   // 32 KB block
    [InlineData(0xD8u, 0x200000u)]   // 64 KB block starting exactly at the end
    public void Ssi_erase_beyond_the_flash_end_does_not_write_past_it(uint cmd, uint addr)
    {
        // start >= flash size made (end - start) wrap to ~4 GB for Unsafe.InitBlock.
        using var machine = new RP2040Machine();
        var ssi = machine.Ssi;
        ssi.WriteWord(0x008, 1);                                 // SSIENR
        void Transaction(params byte[] bytes)
        {
            ssi.WriteWord(0x010, 1);                             // SER: CS assert
            foreach (var b in bytes) ssi.WriteWord(0x060, b);
            ssi.WriteWord(0x010, 0);                             // CS deassert -> processes the command
        }
        Transaction(0x06);                                       // write enable
        Transaction((byte)cmd, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr);
        for (var i = 0; i < RandomAccessMemory.TailPad; i++)
            (*(machine.Bus.PtrFlash + machine.Bus.FlashSize + i)).Should().Be(0, $"byte {i} past the flash buffer");
    }

    [Fact]
    public void Ssi_buffers_stop_growing_when_firmware_clocks_bytes_forever()
    {
        // DR0 writes queue an RX byte each and, with CS held, a TX byte: a loop that never reads or
        // toggles CS grew both without bound (host memory).
        using var machine = new RP2040Machine();
        var ssi = machine.Ssi;
        ssi.WriteWord(0x008, 1);                                 // SSIENR
        ssi.WriteWord(0x010, 1);                                 // SER: CS held
        for (var i = 0; i < 20_000; i++) ssi.WriteWord(0x060, 0x5A);
        ssi.ReadWord(0x024).Should().BeLessThanOrEqualTo(4096u); // RXFLR
    }

    // ── DMA: guest-controlled CHAIN_TO ──────────────────────────────────────────────────────────────

    private const uint DmaBase = 0x50000000;
    private const uint DmaEn = 1, DmaWord = 2u << 2, DmaIncr = 3u << 4, DmaPermanent = 0x3Fu << 15;

    private static void DmaConfigure(RP2040Machine machine, int ch, uint count, int chainTo)
    {
        var b = DmaBase + (uint)ch * 0x40;
        machine.Bus.WriteWord(b + 0x00, 0x20000000);
        machine.Bus.WriteWord(b + 0x04, 0x20000100);
        machine.Bus.WriteWord(b + 0x08, count);
        machine.Bus.WriteWord(b + 0x10, DmaEn | DmaWord | DmaIncr | DmaPermanent | ((uint)chainTo << 11)); // AL1: CTRL, no trigger
    }

    [Theory]
    [InlineData(12)]
    [InlineData(15)]
    public void Dma_chain_to_a_nonexistent_channel_is_ignored_instead_of_throwing(int chainTo)
    {
        // CHAIN_TO is 4 bits but there are 12 channels: the array lookup threw out of the bus write.
        using var machine = new RP2040Machine();
        DmaConfigure(machine, 0, 1, chainTo);
        var act = () => machine.Bus.WriteWord(DmaBase + 0x1C, 1);   // AL1 TRANS_COUNT: trigger
        act.Should().NotThrow();
        machine.Bus.ReadWord(0x20000100).Should().Be(0u);
    }

    [Fact]
    public void Dma_chain_ring_terminates_instead_of_overflowing_the_host_stack()
    {
        // 0 -> 1 -> 0 with both enabled: each completion immediately re-triggered the other, recursing
        // until the (uncatchable) host stack overflow.
        using var machine = new RP2040Machine();
        DmaConfigure(machine, 0, 1, 1);
        DmaConfigure(machine, 1, 0, 0);
        machine.Bus.WriteWord(0x20000000, 0xCAFEF00D);
        var act = () => machine.Bus.WriteWord(DmaBase + 0x1C, 1);
        act.Should().NotThrow();
        machine.Bus.ReadWord(0x20000100).Should().Be(0xCAFEF00Du, "the first transfer still ran");
    }

    [Theory]
    [InlineData(0x1Cu)]    // its own TRANS_COUNT (trigger alias)
    [InlineData(0x430u)]   // MULTI_CHAN_TRIGGER, bit 0 = itself
    public void Dma_channel_writing_its_own_trigger_terminates_instead_of_overflowing_the_host_stack(uint target)
    {
        // Each beat re-triggers the channel that is executing it: ExecuteChannel re-entered per beat.
        using var machine = new RP2040Machine();
        machine.Bus.WriteWord(0x20000000, 1);                    // the value every beat writes
        machine.Bus.WriteWord(DmaBase + 0x00, 0x20000000);
        machine.Bus.WriteWord(DmaBase + 0x04, DmaBase + target);
        machine.Bus.WriteWord(DmaBase + 0x08, 1);
        machine.Bus.WriteWord(DmaBase + 0x10, DmaEn | DmaWord | DmaPermanent);   // no increments, chain to self
        var act = () => machine.Bus.WriteWord(DmaBase + 0x1C, 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void Dma_transfer_is_sliced_so_a_huge_count_does_not_stall_the_caller()
    {
        // TRANS_COUNT is 32 bits and a transfer ran to completion inside the triggering write:
        // 0xFFFFFFFF beats was minutes inside one bus access. One slice is 2^20 beats; the rest finishes on
        // the following ticks.
        using var machine = new RP2040Machine();
        const uint count = 2_500_000, slice = 1u << 20;
        machine.Bus.WriteWord(0x20000000, 0xCAFEF00D);
        machine.Bus.WriteWord(DmaBase + 0x00, 0x20000000);
        machine.Bus.WriteWord(DmaBase + 0x04, 0x20000100);
        machine.Bus.WriteWord(DmaBase + 0x10, DmaEn | DmaWord | DmaPermanent);   // no increments
        machine.Bus.WriteWord(DmaBase + 0x1C, count);
        machine.Dma.IsChannelBusy(0).Should().BeTrue("only the first slice ran");
        machine.Dma.GetChannelTransCount(0).Should().Be(count - slice);
        machine.Bus.ReadWord(0x20000100).Should().Be(0xCAFEF00Du);
        machine.Run(1);
        machine.Dma.GetChannelTransCount(0).Should().Be(count - 2 * slice);
        machine.Run(1);
        machine.Dma.IsChannelBusy(0).Should().BeFalse();
        machine.Dma.GetChannelTransCount(0).Should().Be(0u);
    }

    // ── USB DPRAM: firmware-written BUFFER_ADDRESS / LENGTH ─────────────────────────────────────────

    [Fact]
    public void Usb_endpoint_buffer_outside_the_4k_dpram_is_clamped_instead_of_throwing()
    {
        // BUFFER_ADDRESS (bits 15:6 of EPx_CONTROL) reaches 0xFFC0 and LENGTH 1023, but DPRAM is 4 KB.
        using var machine = new RP2040Machine();
        const uint dpram = 0x50100000;
        machine.Bus.WriteWord(dpram + 0x08, 0xFFC0);                 // EP1 IN control: buffer at 0xFFC0
        machine.Bus.WriteWord(dpram + 0x0C, 0xFFC0);                 // EP1 OUT control
        machine.Usb.EndpointReadDone(1, new byte[64]);
        var act = () =>
        {
            machine.Bus.WriteWord(dpram + 0x88, (1u << 10) | 1023); // EP1 IN buffer control: AVAILABLE, len 1023
            machine.Bus.WriteWord(dpram + 0x8C, (1u << 10) | 64);   // EP1 OUT buffer control
            machine.Run(2000);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Usb_in_buffer_rearmed_without_end_is_bounded_between_ticks()
    {
        // Each AVAILABLE write queues a copy of the buffer (up to 1 KB) for delivery at the next tick; a
        // firmware loop re-arming it forever queued them without limit.
        using var machine = new RP2040Machine();
        const uint dpram = 0x50100000;
        var delivered = 0;
        machine.Usb.OnEndpointWrite = (_, _) => delivered++;
        for (var i = 0; i < 5000; i++)
            machine.Bus.WriteWord(dpram + 0x88, (1u << 10) | 64);
        machine.Run(2000);
        delivered.Should().BeLessThanOrEqualTo(1024);
    }

    // ── Deterministic fuzz ──────────────────────────────────────────────────────────────────────────

    // GARBAGE_FUZZ_SEEDS / GARBAGE_FUZZ_SCALE widen the run for a one-off hunt (more seeds, scale x longer
    // instruction counts); the defaults keep the suite fast.
    private static int Seeds(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("GARBAGE_FUZZ_SEEDS"), out var n) ? n : fallback;
    private static int Scale =>
        int.TryParse(Environment.GetEnvironmentVariable("GARBAGE_FUZZ_SCALE"), out var n) ? n : 1;

    private static uint Pick(Random rng) => InterestingPcs[rng.Next(InterestingPcs.Length)];

    private static void Fill(Random rng, byte* dst, int length)
    {
        var buf = new byte[length];
        rng.NextBytes(buf);
        buf.AsSpan().CopyTo(new Span<byte>(dst, length));
    }

    private static void Hostile(Random rng, CortexM0Plus cpu)
    {
        cpu.Registers.VTOR = Pick(rng);
        cpu.Registers.SP = rng.Next(3) == 0 ? Pick(rng) : 0x20040000;
        for (var i = 0; i < 13; i++) cpu.Registers[i] = rng.Next(4) == 0 ? Pick(rng) : (uint)rng.Next();
        cpu.Registers.LR = rng.Next(3) == 0 ? 0xFFFFFFF0u | (uint)rng.Next(16) : Pick(rng);
        cpu.Registers.PC = Pick(rng) & ~1u;
        cpu.Registers.PRIMASK = (uint)rng.Next(2);
        cpu.Registers.CONTROL = (uint)rng.Next(4);
        if (rng.Next(4) == 0) cpu.Registers.IPSR = (uint)rng.Next(64);
        if (rng.Next(3) == 0)
        {
            cpu.Registers.EnabledInterrupts = (uint)rng.Next() & 0x03FFFFFF;
            cpu.Registers.PendingInterrupts = (uint)rng.Next() & 0x03FFFFFF;
            cpu.Registers.InterruptsUpdated = true;
        }
        cpu.OnLockup = (_, _) => { };
    }

    /// <summary>Bare cores on a bare bus: random images in every backing store, hostile registers.</summary>
    [Fact]
    public void Fuzz_random_images_on_the_bare_core_never_throw()
    {
        QuietStderr(() =>
        {
            for (var seed = 1; seed <= Seeds(200); seed++)
            {
                var rng = new Random(seed);
                using var bus = new BusInterconnect();
                var cpu = new CortexM0Plus(bus);
                Fill(rng, bus.PtrFlash, 8192);
                Fill(rng, bus.PtrSram, 8192);
                Fill(rng, bus.PtrSram + 0x7E000, 8192);               // tail of the 512 KB SRAM
                Fill(rng, bus.PtrFlash + bus.FlashSize - 8192, 8192); // tail of flash
                Hostile(rng, cpu);
                Run(cpu, 2000 * Scale, $"bare seed {seed}");
            }
            return 0;
        });
    }

    /// <summary>Full machine (all peripherals, boot ROM, both cores): garbage reaches every register file.</summary>
    [Fact]
    public void Fuzz_random_images_on_the_full_machine_never_throw()
    {
        QuietStderr(() =>
        {
            for (var seed = 1; seed <= Seeds(200); seed++)
            {
                var rng = new Random(seed);
                using var machine = new RP2040Machine();
                var image = new byte[8192];
                rng.NextBytes(image);
                machine.LoadFlash(image);
                Fill(rng, machine.Bus.PtrSram, 8192);
                Fill(rng, machine.Bus.PtrSram + 0x7E000, 8192);
                Hostile(rng, machine.Cpu);
                Hostile(rng, machine.Cpu1);
                var act = () =>
                {
                    machine.Run(1000 * Scale);
                    machine.Cpu1.Run(1000 * Scale);
                    machine.Run(1000 * Scale);
                };
                act.Should().NotThrow($"machine seed {seed}");
            }
            return 0;
        });
    }

    /// <summary>Guest-style register pokes straight at every peripheral window: any offset, any width, any
    /// value, interleaved with time passing. Peripheral decoders index arrays with guest-chosen offsets.</summary>
    [Fact]
    public void Fuzz_random_mmio_accesses_never_throw()
    {
        // The DMA window is left out on purpose: its transfers run synchronously to completion, so a
        // random 32-bit TRANS_COUNT is a multi-minute stall (known limitation), not a crash.
        QuietStderr(() =>
        {
            for (var seed = 1; seed <= Seeds(200); seed++)
            {
                var rng = new Random(seed);
                using var machine = new RP2040Machine();
                machine.LoadFlash(new byte[0x200]);
                var bus = machine.Bus;
                var act = () =>
                {
                    for (var op = 0; op < 3000 * Scale; op++)
                    {
                        uint addr = rng.Next(6) switch
                        {
                            0 => 0x40000000u + ((uint)rng.Next() & 0xFFFFF),
                            1 => 0x50010000u + ((uint)rng.Next() & 0x3FFFFF),
                            2 => 0xD0000000u + ((uint)rng.Next() & 0x1FF),
                            3 => 0xE0000000u + ((uint)rng.Next() & 0x1FFFF),
                            4 => 0x18000000u + ((uint)rng.Next() & 0xFFF),
                            _ => 0x50100000u + ((uint)rng.Next() & 0x1FFF),
                        };
                        var value = rng.Next(3) == 0 ? (uint)rng.Next() : (uint)rng.Next(4);
                        switch (rng.Next(6))
                        {
                            case 0: bus.WriteWord(addr, value); break;
                            case 1: bus.WriteHalfWord(addr, (ushort)value); break;
                            case 2: bus.WriteByte(addr, (byte)value); break;
                            case 3: bus.ReadWord(addr); break;
                            case 4: bus.ReadHalfWord(addr); break;
                            default: bus.ReadByte(addr); break;
                        }
                        if (op % 100 == 99) machine.Run(50);
                    }
                };
                act.Should().NotThrow($"mmio seed {seed}");
            }
            return 0;
        });
    }

    /// <summary>Every 16-bit encoding (and every 32-bit prefix with a random tail) against hostile registers.</summary>
    [Fact]
    public void Fuzz_every_opcode_with_hostile_registers_never_throws()
    {
        QuietStderr(() =>
        {
            using var bus = new BusInterconnect();
            var cpu = new CortexM0Plus(bus);
            cpu.OnLockup = (_, _) => { };
            var rng = new Random(7);
            for (var opcode = 0; opcode <= 0xFFFF; opcode++)
            {
                cpu.Registers.IPSR = 0;
                cpu.Registers.CONTROL = 0;
                cpu.Registers.VTOR = 0x20000000;
                bus.WriteWord(0x2000000C, 0x20000101);              // HardFault handler: b .
                bus.WriteHalfWord(0x20000100, 0xE7FE);
                for (var r = 0; r < 13; r++) cpu.Registers[r] = rng.Next(3) == 0 ? Pick(rng) : (uint)rng.Next();
                cpu.Registers.SP = rng.Next(2) == 0 ? Pick(rng) : 0x20040000;
                cpu.Registers.LR = rng.Next(4) == 0 ? 0xFFFFFFF9u : Pick(rng);
                cpu.Registers.PC = 0x20000200;
                bus.WriteHalfWord(0x20000200, (ushort)opcode);
                bus.WriteHalfWord(0x20000202, (ushort)rng.Next(0x10000));
                try { cpu.Run(1); }
                catch (Exception e) { throw new Xunit.Sdk.XunitException($"opcode 0x{opcode:X4}: {e}"); }
            }
            return 0;
        });
    }
}

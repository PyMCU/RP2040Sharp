using FluentAssertions;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using Xunit;

namespace RP2040.Peripherals.Tests.Memory;

/// <summary>
/// Tests for <see cref="RandomAccessMemory"/>, whose backing store is unmanaged native memory.
/// Verifies zero-initialisation, read/write correctness, idempotent disposal, and that creating
/// and disposing many machines does not leak managed heap (the RAM lives outside the GC heap).
/// </summary>
public sealed class RandomAccessMemoryTests
{
    [Fact]
    public void AllocZeroed_starts_at_zero()
    {
        using var ram = new RandomAccessMemory(1024);
        for (uint a = 0; a < 1024; a += 4)
            ram.ReadWord(a).Should().Be(0u, "native memory is zero-initialised like new byte[]");
    }

    [Fact]
    public void ReadWrite_byte_half_word_roundtrip()
    {
        using var ram = new RandomAccessMemory(64);

        ram.WriteByte(0, 0xAB);
        ram.ReadByte(0).Should().Be(0xAB);

        ram.WriteHalfWord(2, 0xBEEF);
        ram.ReadHalfWord(2).Should().Be(0xBEEF);

        ram.WriteWord(8, 0xDEADBEEF);
        ram.ReadWord(8).Should().Be(0xDEADBEEF);

        // Word write is visible byte-wise (little-endian).
        ram.ReadByte(8).Should().Be(0xEF);
        ram.ReadByte(11).Should().Be(0xDE);
    }

    [Fact]
    public void Size_reflects_constructor_argument()
    {
        using var ram = new RandomAccessMemory(512 * 1024);
        ram.Size.Should().Be(512u * 1024u);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var ram = new RandomAccessMemory(256);
        ram.WriteWord(0, 1u);

        var act = () =>
        {
            ram.Dispose();
            ram.Dispose(); // second free must be a no-op, not a double-free crash
        };
        act.Should().NotThrow("double Dispose must be safe (guarded by _disposed)");
    }

    [Fact]
    public void Creating_and_disposing_many_machines_does_not_grow_managed_heap()
    {
        // Each RP2040Machine allocates ~2.5 MB of RAM. Before the NativeMemory migration this was a
        // pinned managed array; creating+disposing 200 machines would have churned ~500 MB of pinned
        // managed heap. Now the RAM is unmanaged, so the managed heap stays flat.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 200; i++)
        {
            var m = new RP2040Machine();
            m.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        // Allow generous slack for unrelated allocations; the point is it is NOT ~500 MB.
        (after - before).Should().BeLessThan(32 * 1024 * 1024,
            "RAM blocks are unmanaged now, so 200 machines must not balloon the managed heap");
    }
}

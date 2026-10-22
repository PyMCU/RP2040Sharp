using RP2040.Peripherals;
using RP2040.Peripherals.Ssi;
using Xunit;

namespace RP2040Sharp.Tests.Peripherals;

/// <summary>
/// The flash unique id the SSI answers to 0x4B (Read Unique ID), which is what the RP2040 SDK's
/// pico_get_unique_board_id() reads (machine.unique_id()): non-zero and different per chip by default, settable,
/// and the JEDEC / data-read answers untouched.
/// </summary>
public sealed class SsiUniqueIdTests
{
    private const uint SSIENR = 0x008, DR0 = 0x060;

    private static byte[] ReadUniqueId(SsiPeripheral ssi)
    {
        ssi.WriteWord(SSIENR, 1);
        ssi.WriteWord(DR0, 0x4B);
        for (int i = 0; i < 12; i++) ssi.WriteWord(DR0, 0);   // 4 dummy + 8 id bytes
        Assert.Equal(0u, ssi.ReadWord(DR0));                   // response to the command byte
        for (int i = 0; i < 4; i++) Assert.Equal(0u, ssi.ReadWord(DR0));   // dummy phase
        var id = new byte[8];
        for (int i = 0; i < 8; i++) id[i] = (byte)ssi.ReadWord(DR0);
        return id;
    }

    [Fact]
    public void Default_id_is_eight_non_zero_bytes_stable_per_chip()
    {
        var ssi = new SsiPeripheral();
        var id = ReadUniqueId(ssi);
        Assert.Contains(id, b => b != 0);
        Assert.Equal(id, ReadUniqueId(ssi));
    }

    [Fact]
    public void Two_chips_have_different_ids_also_after_a_flash_is_attached()
    {
        using var m1 = new RP2040Machine();
        using var m2 = new RP2040Machine();
        using var m3 = new RP2040Machine(4 * 1024 * 1024);
        Assert.NotEqual(ReadUniqueId(m1.Ssi), ReadUniqueId(m2.Ssi));
        Assert.NotEqual(ReadUniqueId(m2.Ssi), ReadUniqueId(m3.Ssi));
    }

    [Fact]
    public void Setter_and_constructor_argument_set_the_id()
    {
        byte[] id = [0xE6, 0x61, 0x41, 0x04, 0x03, 0x9B, 0x2A, 0x17];
        var ssi = new SsiPeripheral();
        ssi.SetUniqueId(id);
        Assert.Equal(id, ReadUniqueId(ssi));
        Assert.Throws<ArgumentException>(() => ssi.SetUniqueId(new byte[7]));
        Assert.Equal(id, ReadUniqueId(ssi));

        using var m = new RP2040Machine(flashUniqueId: [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ReadUniqueId(m.Ssi));
    }
}

using RP2040.Peripherals;
using Xunit;

namespace RP2040Sharp.Tests.Peripherals;

/// <summary>
/// Verifies BootROM revision selection (RP2040 B1 = bootrom v2, B2 = bootrom v3). The version byte lives
/// at offset 0x13 of the ROM; the flash hardware helper functions (connect_internal_flash etc.) must be
/// patched to BX LR (0x4770) at the addresses resolved from each revision's ROM function table. B2 was
/// dumped from real B2 silicon (RP2040 rev B2) via HIL.
/// </summary>
public sealed class BootromRevisionTests
{
    // Minimal flash image (valid-looking vector table) just to trigger BootROM installation in LoadFlash.
    private static byte[] MinimalImage()
    {
        var img = new byte[512];
        // initial SP at [0], reset vector (thumb) at [4]
        BitConverter.GetBytes(0x20040000u).CopyTo(img, 0);
        BitConverter.GetBytes(0x10000101u).CopyTo(img, 4);
        return img;
    }

    private static bool IsBxLr(RP2040Machine m, uint addr) =>
        m.Bus.ReadByte(addr) == 0x70 && m.Bus.ReadByte(addr + 1) == 0x47;

    [Fact]
    public void B1_loads_bootrom_v2_and_patches_b1_flash_functions()
    {
        using var m = new RP2040Machine(); // default = B1
        m.LoadFlash(MinimalImage());

        m.Bus.ReadByte(0x13).Should().Be(2, "B1 silicon ships bootrom version 2");
        IsBxLr(m, 0x24A0).Should().BeTrue("connect_internal_flash patched (B1 address)");
        IsBxLr(m, 0x2330).Should().BeTrue("flash_enter_cmd_xip patched (B1 address)");
    }

    [Fact]
    public void B2_loads_bootrom_v3_and_patches_b2_flash_functions()
    {
        using var m = new RP2040Machine(bootrom: RP2040BootromRevision.B2);
        m.LoadFlash(MinimalImage());

        m.Bus.ReadByte(0x13).Should().Be(3, "B2 silicon ships bootrom version 3");
        // B2 flash helpers sit at different addresses than B1 (resolved from the ROM table).
        IsBxLr(m, 0x2490).Should().BeTrue("connect_internal_flash patched (B2 address)");
        IsBxLr(m, 0x2320).Should().BeTrue("flash_enter_cmd_xip patched (B2 address)");
    }
}

using FluentAssertions;
using RP2040.Peripherals;
using RP2040Sharp.Wireless.Cyw43;
using Xunit;

namespace RP2040Sharp.IntegrationTests.Tests;

public class Cyw43PowerOnResetTests
{
    private const uint SpiReadTestRegister = 0x14, SpiBusControl = 0x00;

    private static byte[] Cmd(bool write, int fn, uint addr, uint sz, bool swapped, params byte[] data)
    {
        var cmd = (write ? 0x8000_0000u : 0) | ((uint)fn << 28) | (addr << 11) | sz;
        byte[] b = swapped
            ? [(byte)(cmd >> 8), (byte)cmd, (byte)(cmd >> 24), (byte)(cmd >> 16)]
            : [(byte)cmd, (byte)(cmd >> 8), (byte)(cmd >> 16), (byte)(cmd >> 24)];
        return [.. b, .. data];
    }

    private static byte[] Transact(Cyw43439Device dev, byte[] host) => dev.Bus.OnRead?.Invoke(host) ?? [];

    private static byte[] ProbeTestRegister(Cyw43439Device dev) =>
        Transact(dev, Cmd(false, 0, SpiReadTestRegister, 4, swapped: true));

    [Fact]
    public void Wl_reg_on_low_returns_the_chip_to_its_cold_state()
    {
        using var machine = new RP2040Machine();
        var dev = new Cyw43439Device(machine.IoBank0);
        byte[] mac = [0x02, 0x12, 0x34, 0x00, 0xAB, 0xCD];
        dev.Sdpcm.MacAddress = mac;
        dev.Bus.OnPower?.Invoke(true);
        var cold = ProbeTestRegister(dev);

        Transact(dev, Cmd(true, 0, SpiBusControl, 4, swapped: true, 0, 0, 0, 0));
        Transact(dev, Cmd(true, 1, 0x0000, 4, swapped: false, 1, 2, 3, 4));
        Transact(dev, Cmd(true, 1, 0x1000B, 1, swapped: false, 0x10));
        Transact(dev, Cmd(true, 1, 0x1000C, 1, swapped: false, 0x18));
        Transact(dev, Cmd(true, 1, 0x3800, 1, swapped: false, 0x00));
        dev.Sdpcm.EnqueueEthernet(0, new byte[64]);
        dev.Word32.Should().BeTrue();
        dev.DebugWlanCoreUp.Should().BeTrue();
        dev.FirmwareBytes.Should().Be(4);
        dev.Sdpcm.HasPacket.Should().BeTrue();

        dev.Bus.OnPower?.Invoke(false);
        dev.Bus.OnPower?.Invoke(true);

        dev.Word32.Should().BeFalse("the bus boots in 16-bit swapped mode");
        dev.DebugWlanCoreUp.Should().BeFalse("the WLAN core is back in reset");
        dev.FirmwareBytes.Should().Be(0, "SOCRAM loses the downloaded firmware");
        dev.Window.Should().Be(0);
        dev.Sdpcm.HasPacket.Should().BeFalse("packets queued for the old host are gone");
        dev.Sdpcm.MacAddress.Should().Equal(mac, "the MAC belongs to the board");
        ProbeTestRegister(dev).Should().Equal(cold, "the startup probe must read 0xFEEDBEAD as on a cold chip");
    }
}

using System.Text;
using FluentAssertions;
using RP2040.TestKit;
using RP2040Sharp.Wireless.Cyw43;
using Xunit;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// The CYW43439's WL GPIO outputs (WL_GPIO0 is the Pico W's onboard LED), driven at the SDPCM boundary with the
/// ioctls cyw43_ll_gpio_set / cyw43_ll_gpio_get send: WLC_SET_VAR "gpioout" (le32 mask, le32 value) and
/// WLC_GET_VAR "ccgpioin". No firmware needed.
/// </summary>
public class Cyw43WlGpioTests
{
    private const uint WLC_GET_VAR = 262, WLC_SET_VAR = 263;
    private const uint SdpcmGet = 0, SdpcmSet = 2;

    /// <summary>An SDPCM control packet as cyw43_send_ioctl lays it out: SDPCM header (12), ioctl header (16), payload.</summary>
    private static byte[] Ioctl(uint cmd, uint kind, byte[] payload, ushort outLen)
    {
        var size = 12 + 16 + payload.Length;
        var p = new byte[size];
        p[0] = (byte)size; p[1] = (byte)(size >> 8); p[2] = (byte)~size; p[3] = (byte)(~size >> 8);
        p[7] = 12;
        BitConverter.GetBytes(cmd).CopyTo(p, 12);
        BitConverter.GetBytes((uint)outLen).CopyTo(p, 16);
        BitConverter.GetBytes(kind | 0x0001_0000u).CopyTo(p, 20);   // ioctl id 1
        payload.CopyTo(p, 28);
        return p;
    }

    private static byte[] Var(string name, params uint[] args)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(name + "\0"));
        foreach (var a in args) b.AddRange(BitConverter.GetBytes(a));
        return [.. b];
    }

    /// <summary>cyw43_ll_gpio_set(gpio, on): mask and value of one bit.</summary>
    private static void GpioSet(Sdpcm radio, int gpio, bool on)
    {
        radio.HostWrite(Ioctl(WLC_SET_VAR, SdpcmSet, Var("gpioout", 1u << gpio, on ? 1u << gpio : 0), 0));
        Drain(radio);
    }

    /// <summary>cyw43_ll_gpio_get: the le32 the chip answers to "ccgpioin".</summary>
    private static uint GpioIn(Sdpcm radio)
    {
        Drain(radio);
        var q = Var("ccgpioin", 0);
        radio.HostWrite(Ioctl(WLC_GET_VAR, SdpcmGet, q, (ushort)q.Length));
        radio.HasPacket.Should().BeTrue();
        var resp = radio.HostRead(radio.NextPacketLen);
        return BitConverter.ToUInt32(resp, 12 + 16);
    }

    private static void Drain(Sdpcm radio)
    {
        while (radio.HasPacket) radio.HostRead(radio.NextPacketLen);
    }

    [Fact]
    public void Gpioout_sets_the_bits_under_the_mask_and_ccgpioin_reads_them_back()
    {
        var radio = new Sdpcm();
        var changes = new List<uint>();
        radio.OnWlGpioChanged += v => changes.Add(v);
        radio.WlGpioOut.Should().Be(0u);
        GpioIn(radio).Should().Be(0u);

        GpioSet(radio, 0, true);                                  // Pin("LED").on()
        radio.WlGpioOut.Should().Be(1u);
        GpioIn(radio).Should().Be(1u);

        GpioSet(radio, 1, true);                                  // another WL GPIO leaves the LED alone
        radio.WlGpioOut.Should().Be(3u);
        GpioSet(radio, 0, true);                                  // no change, no event
        GpioSet(radio, 0, false);                                 // Pin("LED").off()
        radio.WlGpioOut.Should().Be(2u);
        GpioIn(radio).Should().Be(2u);

        // Value bits outside the mask are ignored.
        radio.HostWrite(Ioctl(WLC_SET_VAR, SdpcmSet, Var("gpioout", 0x4, 0xFFFF_FFFF), 0));
        radio.WlGpioOut.Should().Be(6u);

        changes.Should().Equal(1u, 3u, 2u, 6u);
    }

    [Fact]
    public void A_short_gpioout_is_acked_and_ignored()
    {
        var radio = new Sdpcm();
        radio.HostWrite(Ioctl(WLC_SET_VAR, SdpcmSet, Var("gpioout", 1), 0));   // mask only
        radio.HasPacket.Should().BeTrue();                                      // still answered
        radio.WlGpioOut.Should().Be(0u);
    }

    [Fact]
    public void Power_down_clears_the_outputs()
    {
        var radio = new Sdpcm();
        var changes = new List<uint>();
        radio.OnWlGpioChanged += v => changes.Add(v);
        GpioSet(radio, 0, true);
        radio.PowerDown();
        radio.WlGpioOut.Should().Be(0u);
        GpioIn(radio).Should().Be(0u);
        radio.PowerDown();                                        // already off: no event
        changes.Should().Equal(1u, 0u);
    }

    [Fact]
    public void Wl_on_low_resets_the_led()
    {
        using var sim = RP2040TestSimulation.Create();
        var dev = new Cyw43439Device(sim.Rp2040.IoBank0);
        dev.Bus.OnPower!(true);
        GpioSet(dev.Sdpcm, 0, true);
        dev.Sdpcm.WlGpioOut.Should().Be(1u);
        dev.Bus.OnPower!(false);                                  // what GSpiSlave raises when GPIO23 (WL_ON) falls
        dev.Powered.Should().BeFalse();
        dev.Sdpcm.WlGpioOut.Should().Be(0u);
    }
}

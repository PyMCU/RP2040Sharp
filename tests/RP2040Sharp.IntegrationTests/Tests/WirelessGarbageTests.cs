using FluentAssertions;
using RP2040.Peripherals;
using RP2040Sharp.Wireless.Cyw43;
using Xunit;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Hostile-firmware robustness for the CYW43439 model. Everything the guest sends over the gSPI bus
/// (SDPCM packets, Ethernet frames, backplane writes) is parsed on the host inside the guest's own bus
/// write, so a parsing exception is an exception out of <c>Run</c>, and any structure that grows with
/// guest traffic is a host-memory sink. These drive the layers directly — no firmware needed.
/// </summary>
public class WirelessGarbageTests
{
    private static readonly byte[] GuestMac = [0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
    private static readonly byte[] Broadcast = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Wrap an Ethernet frame the way the driver does: SDPCM data header (14) + BDC (4) + frame.</summary>
    private static byte[] DataPacket(byte[] eth)
    {
        var size = 14 + 4 + eth.Length;
        var p = new byte[size];
        p[0] = (byte)size; p[1] = (byte)(size >> 8);
        p[2] = (byte)~p[0]; p[3] = (byte)~p[1];
        p[5] = 2;                                // data channel
        p[7] = 14;                               // header length (+2 pad)
        eth.CopyTo(p, 14 + 4);
        return p;
    }

    private static byte[] Ipv4(byte[] dst, byte proto, ushort claimedTotal, byte[] l4Plus)
    {
        var f = new byte[14 + 20 + l4Plus.Length];
        dst.CopyTo(f, 0);
        GuestMac.CopyTo(f, 6);
        f[12] = 0x08; f[13] = 0x00;
        f[14] = 0x45;
        f[16] = (byte)(claimedTotal >> 8); f[17] = (byte)claimedTotal;
        f[23] = proto;
        l4Plus.CopyTo(f, 34);
        return f;
    }

    [Fact]
    public void Dhcp_option_53_in_the_last_two_bytes_does_not_read_past_the_frame()
    {
        // The option walker read the value byte at o + 2 after only checking o + 1 < Length.
        var net = new VirtualNet();
        var radio = new Sdpcm();
        net.AddDevice(radio);
        var udp = new byte[8 + 240 + 2];
        udp[3] = 67;                                          // UDP dst port 67
        udp[^2] = 53; udp[^1] = 1;                            // option 53, length 1, value missing
        var act = () => radio.HostWrite(DataPacket(Ipv4(Broadcast, 17, 0, udp)));
        act.Should().NotThrow();
    }

    [Fact]
    public void Tcp_segment_whose_ip_length_exceeds_the_frame_does_not_read_past_the_frame()
    {
        // Payload length came from the IP header's total-length field, not from the bytes received.
        var net = new VirtualNet();
        net.ListenTcp(80, _ => null);
        var radio = new Sdpcm();
        net.AddDevice(radio);
        byte[] Tcp(byte flags, uint seq)
        {
            var t = new byte[20 + 4];
            t[1] = 0x50; t[3] = 80;                            // ports 80 -> 80
            t[4] = (byte)(seq >> 24); t[5] = (byte)(seq >> 16); t[6] = (byte)(seq >> 8); t[7] = (byte)seq;
            t[12] = 5 << 4; t[13] = flags;
            return t;
        }
        radio.HostWrite(DataPacket(Ipv4(net.GatewayMac, 6, 40, Tcp(0x02, 100))));            // SYN
        var act = () => radio.HostWrite(DataPacket(Ipv4(net.GatewayMac, 6, 60000, Tcp(0x10, 101))));  // ACK + "data"
        act.Should().NotThrow();
    }

    private static byte[] Ioctl(uint cmd, ushort outLen)
    {
        var size = 12 + 16;
        var p = new byte[size];
        p[0] = (byte)size; p[1] = 0; p[2] = (byte)~p[0]; p[3] = 0xFF;
        p[7] = 12;
        p[12] = (byte)cmd; p[13] = (byte)(cmd >> 8);
        p[16] = (byte)outLen; p[17] = (byte)(outLen >> 8);
        return p;
    }

    [Fact]
    public void Chip_to_host_queue_is_bounded_when_the_guest_never_reads()
    {
        // Every ioctl queues a response of up to 64 KB; a loop that only writes grew the queue forever.
        var radio = new Sdpcm();
        for (var i = 0; i < 3000; i++) radio.HostWrite(Ioctl(263, 4096));
        var queued = 0;
        while (radio.HasPacket && queued < 10_000) { radio.HostRead(64); queued++; }
        queued.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public void Backplane_storage_is_bounded_when_the_guest_writes_sparse_addresses()
    {
        var chip = new Backplane();
        const int writes = 1_100_000;
        for (uint i = 0; i < writes; i++) chip.WriteByte(0x1000_0000u + i * 4, 0xAB);
        chip.ReadByte(0x1000_0000u).Should().Be(0xAB, "addresses stored before the ceiling keep working");
        chip.ReadByte(0x1000_0000u + (writes - 1) * 4).Should().Be(0, "storage stops growing at a ceiling");
    }

    [Fact]
    public void Switch_and_gateway_mac_tables_are_bounded_by_distinct_source_macs()
    {
        // One learned entry per guest-chosen source MAC; a loop with a counter in the MAC grew them forever.
        var net = new VirtualNet();
        var radio = new Sdpcm();
        net.AddDevice(radio);
        for (var i = 0; i < 5000; i++)
        {
            var eth = new byte[60];
            Broadcast.CopyTo(eth, 0);
            eth[6] = 0x02; eth[7] = (byte)(i >> 8); eth[8] = (byte)i;
            eth[12] = 0x88; eth[13] = 0xB5;
            radio.HostWrite(DataPacket(eth));
        }
        net.LearnedStations.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public void Gspi_host_bytes_are_bounded_when_the_guest_clocks_one_transaction_forever()
    {
        using var machine = new RP2040Machine();
        machine.Sio.OnGpioChanged += mask => machine.IoBank0.NotifyPads(mask);
        var radio = new Cyw43439Device(machine.IoBank0);
        var captured = -1;
        radio.Bus.OnWrite = bytes => captured = bytes.Length;
        var bus = machine.Bus;
        foreach (var pin in new[] { 23, 24, 25, 29 })
            bus.WriteWord(0x40014000u + (uint)pin * 8 + 4, 5);      // FUNCSEL = SIO
        const uint sio = 0xD0000000u, cs = 1u << 25, clk = 1u << 29, data = 1u << 24;
        bus.WriteWord(sio + 0x14, cs);                              // CS high (idle)
        bus.WriteWord(sio + 0x24, cs | clk | data | (1u << 23));    // outputs
        bus.WriteWord(sio + 0x18, cs);                              // CS low: transaction starts
        bus.WriteWord(sio + 0x14, data);                            // DATA = 1 for every bit
        for (var i = 0; i < 8 * 70_000; i++)
        {
            bus.WriteWord(sio + 0x14, clk);
            bus.WriteWord(sio + 0x18, clk);
        }
        bus.WriteWord(sio + 0x14, cs);                              // CS high: transaction ends
        captured.Should().BeInRange(1, 65536);
    }

    // ── Deterministic fuzz ──────────────────────────────────────────────────────────────────────────

    // WIRELESS_FUZZ_SEEDS=n widens the run for a one-off hunt; the default keeps the suite fast.
    private static int Seeds() =>
        int.TryParse(Environment.GetEnvironmentVariable("WIRELESS_FUZZ_SEEDS"), out var n) ? n : 400;

    private static byte[] Noise(Random rng, int max)
    {
        var b = new byte[rng.Next(max + 1)];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Ethernet frame with mostly-plausible L2/L3/L4 headers and lying length fields.</summary>
    private static byte[] RandomFrame(Random rng, VirtualNet net)
    {
        var f = new byte[rng.Next(0, 400)];
        rng.NextBytes(f);
        if (f.Length < 14) return f;
        (rng.Next(3) == 0 ? net.GatewayMac : rng.Next(2) == 0 ? Broadcast : GuestMac).CopyTo(f, 0);
        if (rng.Next(2) == 0) GuestMac.CopyTo(f, 6);
        var type = rng.Next(4) switch { 0 => 0x0806, 1 => (int)(rng.Next() & 0xFFFF), _ => 0x0800 };
        f[12] = (byte)(type >> 8); f[13] = (byte)type;
        if (f.Length > 14) f[14] = (byte)(0x40 | rng.Next(16));          // IHL 0-15
        if (f.Length > 23) f[23] = rng.Next(3) == 0 ? (byte)rng.Next(256) : (byte)(rng.Next(2) == 0 ? 6 : 17);
        var l4 = 14 + ((f.Length > 14 ? f[14] & 0xF : 5) * 4);
        ushort[] ports = [53, 67, 80, 50000, 0, 8080];
        if (l4 + 4 <= f.Length)
        {
            var d = ports[rng.Next(ports.Length)];
            f[l4 + 2] = (byte)(d >> 8); f[l4 + 3] = (byte)d;
        }
        return f;
    }

    [Fact]
    public void Fuzz_sdpcm_ethernet_dhcp_dns_and_tcp_input_never_throws()
    {
        for (var seed = 1; seed <= Seeds(); seed++)
        {
            var rng = new Random(seed);
            var net = new VirtualNet();
            net.ListenTcp(80, _ => [1, 2, 3]);
            net.ListenUdp(8080, d => d);
            net.DnsRecords["a.test"] = [1, 2, 3, 4];
            var radio = new Sdpcm();
            net.AddDevice(radio);
            radio.VisibleAps.Add(new Sdpcm.VirtualAp("ap", [2, 0, 0x5E, 0, 4, 0], 6, -40, true, "pw"));
            var act = () =>
            {
                for (var i = 0; i < 60; i++)
                {
                    switch (rng.Next(4))
                    {
                        case 0: radio.HostWrite(Noise(rng, 200)); break;
                        case 1:
                        {
                            var p = Noise(rng, 120);
                            if (p.Length >= 12)
                            {
                                p[0] = (byte)p.Length; p[1] = 0;                 // plausible size
                                p[5] = (byte)(rng.Next(4));                      // channel
                                p[7] = (byte)(rng.Next(3) == 0 ? rng.Next(256) : 12 + 2 * rng.Next(2));
                            }
                            radio.HostWrite(p);
                            break;
                        }
                        default: radio.HostWrite(DataPacket(RandomFrame(rng, net))); break;
                    }
                    if (rng.Next(4) == 0) radio.HostRead(rng.Next(0, 300));
                }
            };
            act.Should().NotThrow($"sdpcm seed {seed}");
        }
    }

    [Fact]
    public void Fuzz_gspi_commands_and_hci_packets_never_throw()
    {
        using var machine = new RP2040Machine();
        var radio = new Cyw43439Device(machine.IoBank0);
        for (var seed = 1; seed <= Seeds(); seed++)
        {
            var rng = new Random(seed);
            var act = () =>
            {
                for (var i = 0; i < 60; i++)
                {
                    var host = Noise(rng, 100);
                    if (host.Length >= 4 && rng.Next(2) == 0)
                    {
                        // A plausible command word: random function, address, size.
                        var cmd = ((uint)rng.Next(2) << 31) | ((uint)rng.Next(4) << 28) | ((uint)rng.Next(0x20000) << 11)
                                  | (uint)rng.Next(0x800);
                        host[0] = (byte)cmd; host[1] = (byte)(cmd >> 8); host[2] = (byte)(cmd >> 16); host[3] = (byte)(cmd >> 24);
                    }
                    if (rng.Next(2) == 0) radio.Bus.OnRead!(host); else radio.Bus.OnWrite!(host);
                    radio.Ble.HandleHostPacket((byte)(1 + rng.Next(3)), Noise(rng, 80));
                }
            };
            act.Should().NotThrow($"gspi seed {seed}");
        }
    }
}

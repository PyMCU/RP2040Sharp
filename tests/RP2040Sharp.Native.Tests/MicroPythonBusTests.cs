using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// MicroPython v1.28.0 (RPI_PICO) with the pin-level buses (ST_RP2040_BUS_ALL) through the C ABI only, driven as
/// Proteus drives it: run_until_edge at guard 75 watching the bus pads, and a host that answers the pad edges between
/// calls. machine.SPI loops MOSI back to MISO on the pads, machine.I2C talks to a bit-level slave on SDA / SCL and
/// machine.UART loops TX back to RX.
/// </summary>
public class MicroPythonBusTests(ITestOutputHelper output)
{
    private const uint Guard = 75;

    private sealed class Session : IDisposable
    {
        public readonly nint H;
        public readonly Host Host;
        public readonly BusHost Bus;

        public Session(string uf2)
        {
            (H, Host) = MicroPythonTests.BootToRepl(uf2, Abi.BusAll);
            Assert.Contains(">>> ", Host.Cdc);
            Bus = new BusHost(H, Host);
        }

        /// <summary>exec() <paramref name="code"/> at the REPL and run until it prints DONE; returns what it printed.</summary>
        public string Exec(string code, uint watch, ulong maxCycles = 125_000_000UL * 10)
        {
            int from = Host.Cdc.Length;
            var escaped = code.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            Abi.SerialWrite(H, 2, "exec(\"" + escaped + "print('DO'+'NE')\\n\")\r");
            var end = Abi.Cycles(H) + maxCycles;
            while (Abi.Cycles(H) < end && Host.Cdc.IndexOf("DONE", from, StringComparison.Ordinal) < 0)
                Bus.Step(1_000_000, Guard, watch);
            Assert.Equal(0, Abi.Status(H));
            var text = Host.Cdc[from..];
            Assert.Contains("DONE", text);
            return text;
        }

        public void Dispose() { Abi.Destroy(H); Host.Dispose(); }
    }

    private static string Line(string text, string tag)
    {
        var l = text.Replace("\r", "").Split('\n').FirstOrDefault(x => x.StartsWith(tag + " "));
        return l is null ? "" : l[(tag.Length + 1)..];
    }

    private static string Firmware_()
    {
        var uf2 = Firmware.MicroPythonPico();
        Skip.If(uf2 is null, "MicroPython RPI_PICO v1.28.0 UF2 not found (set RP2040_MICROPYTHON_UF2)");
        return uf2!;
    }

    [SkippableTheory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void Machine_SPI_loops_back_through_the_pads(int polarity, int phase)
    {
        using var s = new Session(Firmware_());
        s.Bus.Wire(3, 4).Drive(4, 0);                      // MOSI (GP3) wired to MISO (GP4)
        int padsBefore = s.Host.Pins.Count;
        var res = s.Exec(
            "from machine import SPI, Pin\n" +
            $"spi = SPI(0, baudrate=1000000, polarity={polarity}, phase={phase}, bits=8, firstbit=SPI.MSB, sck=Pin(2), mosi=Pin(3), miso=Pin(4))\n" +
            "tx = bytes([0xA5, 0x3C, 0xFF, 0x00, 0x81])\n" +
            "rx = bytearray(5)\n" +
            "spi.write_readinto(tx, rx)\n" +
            "print('RX', list(rx))\n" +
            "big = bytes(range(7, 47))\n" +
            "rx2 = bytearray(40)\n" +
            "spi.write_readinto(big, rx2)\n" +
            "print('BIG', rx2 == big)\n",
            watch: 1u << 2 | 1u << 3);
        output.WriteLine(res.Replace("\r", "").Replace("\n", " | "));
        Assert.Equal("[165, 60, 255, 0, 129]", Line(res, "RX"));
        Assert.Equal("True", Line(res, "BIG"));     // 40 bytes: MicroPython moves these with the DMA

        // SCK: two edges per bit, idling at CPOL. MicroPython runs clk_peri from the USB PLL (48 MHz): 1 MHz is CPSDVSR 2,
        // SCR 23, 48 clk_peri cycles, which is 125 core cycles (a half period of 62.5, rounded to 63).
        var sck = s.Host.Pins.Skip(padsBefore).Where(p => p.Gpio == 2 && p.Oe == 1).ToList();
        Assert.True(sck.Count >= 2 * 8 * 45, $"{sck.Count} SCK edges");
        Assert.Equal(polarity, sck[^1].Level);
        var halves = sck.Zip(sck.Skip(1), (a, b) => (long)(b.Cycle - a.Cycle)).Where(g => g < 1000).OrderBy(g => g).ToList();
        output.WriteLine($"SCK half period median {halves[halves.Count / 2]} cycles over {halves.Count} edges");
        Assert.InRange(halves[halves.Count / 2], 62, 63);
    }

    [SkippableFact]
    public void Machine_I2C_scans_and_reads_registers_from_a_host_slave()
    {
        using var s = new Session(Firmware_());
        var regs = new byte[256];
        regs[0] = 0xC5; regs[1] = 0x1A; regs[2] = 0x2B;
        var slave = new I2cBitSlave(77, regs);
        s.Bus.I2c(4, 5, slave);
        // scan() is zero-length writes, which the rp2 port bit-bangs through SIO (SoftI2C): the same pads, another
        // clock. The hardware block's clock is measured over the second script only.
        var scan = s.Exec(
            "from machine import I2C, Pin\n" +
            "i2c = I2C(0, sda=Pin(4), scl=Pin(5), freq=100000)\n" +
            "print('SCAN', i2c.scan())\n",
            watch: 1u << 4 | 1u << 5);
        Assert.Equal("[77]", Line(scan, "SCAN"));
        int padsBefore = s.Host.Pins.Count;
        var res = s.Exec(
            "print('MEM0', list(i2c.readfrom_mem(77, 0, 1)))\n" +
            "print('MEM3', list(i2c.readfrom_mem(77, 0, 3)))\n" +
            "i2c.writeto_mem(77, 16, b'\\x5a\\xa5')\n" +
            "print('MEMW', list(i2c.readfrom_mem(77, 16, 2)))\n" +
            "try:\n    i2c.readfrom_mem(66, 0, 1)\n    print('ABSENT', 'no error')\nexcept OSError as e:\n    print('ABSENT', e.errno)\n",
            watch: 1u << 4 | 1u << 5);
        output.WriteLine(scan.Replace("\r", "").Replace("\n", " | ") + " || " + res.Replace("\r", "").Replace("\n", " | "));
        Assert.Equal("[197]", Line(res, "MEM0"));
        Assert.Equal("[197, 26, 43]", Line(res, "MEM3"));
        Assert.Equal("[90, 165]", Line(res, "MEMW"));
        Assert.Equal("5", Line(res, "ABSENT"));               // address NACK: TX_ABRT, OSError(EIO)
        Assert.Equal(0x5A, slave.Registers[16]);
        Assert.Equal(0xA5, slave.Registers[17]);

        // SCL from the hardware block (FUNCSEL I2C), at the engine's exact cycles: 100 kHz is 1250 core cycles a period,
        // whatever clk_sys firmware believes it set (the counters are scaled from it).
        var scl = s.Host.Pins.Skip(padsBefore).Where(p => p.Gpio == 5).ToList();
        var rises = new List<ulong>();
        for (int i = 1; i < scl.Count; i++)
            if (scl[i - 1].Oe == 1 && scl[i].Oe == 0) rises.Add(scl[i].Cycle);   // released: the pull-up raises it
        var periods = rises.Zip(rises.Skip(1), (a, b) => (long)(b - a)).Where(g => g < 2000).OrderBy(g => g).ToList();
        var median = periods[periods.Count / 2];
        var mm = SiliconTwin.Native.Exports.Lookup(s.H)!.Machine;
        output.WriteLine($"IC_FS_SCL_HCNT {mm.Bus.ReadWord(0x4004401C)} LCNT {mm.Bus.ReadWord(0x40044020)}, clk_sys {mm.Clocks.ClkSysHz}");
        output.WriteLine($"SCL median period {median} cycles = {125_000_000.0 / median / 1000:F2} kHz over {periods.Count} clocks; " +
                         $"slave saw {string.Join(",", slave.Events.Select(e => e.Kind).Distinct())}");
        Assert.InRange(125_000_000.0 / median, 99_000, 101_000);
    }

    [SkippableFact]
    public void Machine_UART_loops_back_through_the_pads()
    {
        using var s = new Session(Firmware_());
        s.Bus.Wire(0, 1).Drive(1, 1);                      // TX (GP0) wired to RX (GP1), idle high
        int padsBefore = s.Host.Pins.Count;
        var res = s.Exec(
            "from machine import UART, Pin\n" +
            "import time\n" +
            "u = UART(0, 115200, tx=Pin(0), rx=Pin(1))\n" +
            "u.write(b'Hi\\x55\\x00\\xff!')\n" +
            "time.sleep_ms(3)\n" +
            "print('RX', list(u.read()))\n",
            watch: 1u << 0);
        output.WriteLine(res.Replace("\r", "").Replace("\n", " | "));
        Assert.Equal("[72, 105, 85, 0, 255, 33]", Line(res, "RX"));

        // The TX waveform: 8N1 at the bit time MicroPython programmed for its clk_peri, in core cycles.
        var m = SiliconTwin.Native.Exports.Lookup(s.H)!.Machine;
        var cfg = m.Uart0.GetConfig();
        double bit = (cfg.Ibrd * 64.0 + cfg.Fbrd) / 4 * 125_000_000.0 / m.Clocks.ClkPeriHz();
        output.WriteLine($"IBRD {cfg.Ibrd} FBRD {cfg.Fbrd} clk_peri {m.Clocks.ClkPeriHz()} clk_sys {m.Clocks.ClkSysHz}: bit {bit:F2} cycles = {125e6 / bit:F0} baud");
        Assert.InRange(125e6 / bit, 115_200 * 0.99, 115_200 * 1.01);
        var tx = s.Host.Pins.Skip(padsBefore).Where(p => p.Gpio == 0 && p.Oe == 1).ToList();
        var starts = new List<ulong>();
        for (int i = 1; i < tx.Count; i++)
            if (tx[i - 1].Level == 1 && tx[i].Level == 0 && (starts.Count == 0 || tx[i].Cycle - starts[^1] > 9.5 * bit)) starts.Add(tx[i].Cycle);
        output.WriteLine("start-to-start: " + string.Join(", ", starts.Zip(starts.Skip(1), (a, b) => b - a)));
        Assert.Equal(6, starts.Count);
        foreach (var gap in starts.Zip(starts.Skip(1), (a, b) => (long)(b - a)))
            Assert.InRange(gap, (long)(10 * bit) - 2, (long)(10 * bit) + 2);   // frames back to back from the FIFO
    }
}

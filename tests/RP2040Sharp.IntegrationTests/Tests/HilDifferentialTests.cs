using System.IO.Ports;
using System.Text;
using RP2040.TestKit.Boards;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Behavioral hardware-in-the-loop differential for the RP2040: the SAME MicroPython firmware runs in
/// the RP2040Sharp emulator and on a real Pico (1 / 1 W), both execute the SAME deterministic one-liner
/// over the REPL, and the printed result is compared bit-for-bit. Big-integer and integer arithmetic
/// exercise the CPU + runtime; any divergence is a real emulator-vs-silicon discrepancy.
///
/// Opt-in (talks to live hardware, reflashes the board): set <c>RP2040_HIL=1</c>, with a Pico in BOOTSEL
/// and picotool installed. The firmware UF2 is the local MicroPython build (override RP2040_HIL_UF2).
/// Skips cleanly when the env var, the UF2 or the board/serial port are absent.
/// </summary>
[Trait("Category", "HardwareDifferential")]
public sealed class HilDifferentialTests
{
    // One line so the friendly REPL handles it without indentation games — pure builtins only (no imports,
    // which a minimal MicroPython may lack). Three deterministic values: 2**100 (big-int), sum(range(10000)),
    // and the sum of squares 0..999. We don't hardcode the expected numbers — the differential is
    // emulator-output == silicon-output; the big-int below just anchors the result line.
    private const string OneLiner =
        "print(2**100, sum(range(10000)), sum(i*i for i in range(1000)))";

    private const string Anchor = "1267650600228229401496703205376"; // 2**100

    private static string Uf2Path =>
        Environment.GetEnvironmentVariable("RP2040_HIL_UF2")
        ?? "/Users/begeistert/Repos/micropython/ports/rp2/build-RPI_PICO/firmware.uf2";

    [Fact]
    public void Real_silicon_and_emulator_produce_identical_micropython_output()
    {
        if (Environment.GetEnvironmentVariable("RP2040_HIL") != "1") return;     // opt-in
        if (!File.Exists(Uf2Path)) return;                                       // no firmware → skip

        var uf2 = File.ReadAllBytes(Uf2Path);

        // ── emulator side ──
        var emu = RunInEmulator(uf2);

        // ── real silicon side ──
        // If the board is already running MicroPython (CDC enumerated), drive it directly — it was
        // flashed with this same UF2. Otherwise flash it from BOOTSEL. (MicroPython RP2 does not expose
        // picotool's force-reboot, so a fresh flash needs the board to start in BOOTSEL.)
        var port = FindCdcPort();
        if (port is null)
        {
            FlashBoard(Uf2Path);
            port = WaitForCdcPort(10_000);
        }
        if (port is null) return;     // board not enumerating as USB-CDC → skip rather than fail
        var board = DriveBoardRepl(port);

        // The differential: the result line must be identical on emulator and silicon.
        var emuLine = ResultLine(emu);
        var boardLine = ResultLine(board);

        emuLine.Should().NotBeEmpty($"emulator must print the result line; raw:\n{emu}");
        boardLine.Should().Be(emuLine, $"real silicon must match the emulator bit-for-bit; raw:\n{board}");
    }

    // The line that carries the space-separated decimal results (anchored by 2**100).
    private static string ResultLine(string raw) =>
        raw.Replace("\r", "").Split('\n')
           .Select(l => l.Trim())
           .FirstOrDefault(l => l.Contains(Anchor, StringComparison.Ordinal)) ?? "";

    // ── emulator ──
    private static string RunInEmulator(byte[] uf2)
    {
        var sim = new PicoSimulation();
        sim.LoadFlash(Uf2Reader.ToFlashImage(uf2));

        bool ViaCdc = false;
        bool Booted()
        {
            for (var i = 0; i < 200; i++)
            {
                sim.RunMilliseconds(100);
                if (sim.Uart0.Text.Contains(">>> ", StringComparison.Ordinal)) { ViaCdc = false; return true; }
                if (sim.UsbCdc.Text.Contains(">>> ", StringComparison.Ordinal)) { ViaCdc = true; return true; }
            }
            return false;
        }
        if (!Booted()) return sim.UsbCdc.Text + sim.Uart0.Text;

        if (ViaCdc) { sim.UsbCdc.Clear(); sim.UsbCdc.InjectString(OneLiner + "\r\n"); }
        else        { sim.Uart0.Clear(); sim.Uart0.InjectString(OneLiner + "\r\n"); }

        for (var i = 0; i < 200; i++)
        {
            sim.RunMilliseconds(50);
            var text = ViaCdc ? sim.UsbCdc.Text : sim.Uart0.Text;
            if (text.Contains("1267650600228229401496703205376", StringComparison.Ordinal)) break;
        }
        return ViaCdc ? sim.UsbCdc.Text : sim.Uart0.Text;
    }

    // ── real board ──
    private static void FlashBoard(string uf2) => RunPicotool($"load -x \"{uf2}\"");

    private static string? FindCdcPort()
    {
        var p = Directory.GetFiles("/dev", "cu.usbmodem*");
        return p.Length > 0 ? p[0] : null;
    }

    private static string? WaitForCdcPort(int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            var p = Directory.GetFiles("/dev", "cu.usbmodem*");
            if (p.Length > 0) return p[0];
            Thread.Sleep(250);
        }
        return null;
    }

    private static string DriveBoardRepl(string portName)
    {
        Thread.Sleep(500); // let the CDC settle after enumeration
        using var sp = new SerialPort(portName, 115200) { ReadTimeout = 400, WriteTimeout = 1000 };
        sp.Open();
        void Send(string s) => sp.Write(Encoding.ASCII.GetBytes(s), 0, s.Length);

        Send("\x03\x03\r");                 // Ctrl-C twice → interrupt to a clean prompt
        Drain(sp, 500);
        Send(OneLiner + "\r\n");
        return ReadUntil(sp, "1267650600228229401496703205376", 5000);
    }

    private static void Drain(SerialPort sp, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { try { _ = sp.ReadExisting(); } catch { } Thread.Sleep(20); }
    }

    private static string ReadUntil(SerialPort sp, string marker, int timeoutMs)
    {
        var sb = new StringBuilder();
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            try { sb.Append(sp.ReadExisting()); } catch { }
            if (sb.ToString().Contains(marker)) break;
            Thread.Sleep(20);
        }
        return sb.ToString();
    }

    private static void RunPicotool(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("picotool", args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(30_000);
    }
}

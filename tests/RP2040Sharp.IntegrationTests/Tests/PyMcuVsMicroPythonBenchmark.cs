using System;
using System.IO;
using RP2040Sharp.IntegrationTests.Infrastructure;
using RP2040.TestKit.Boards;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Head-to-head: the SAME MicroPython source (a tight GP25 toggle loop, no delay)
/// run two ways on the identical RP2040 emulator:
///   (1) compiled to a native binary by PyMCU, and
///   (2) interpreted by real MicroPython firmware.
/// Measures emulator instructions per GP25 edge for each and reports the speedup.
/// </summary>
[Trait("Category", "Integration")]
public class PyMcuVsMicroPythonBenchmark
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public PyMcuVsMicroPythonBenchmark(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string PyMcuBin =
        "/Users/begeistert/Repos/pymcu-arm/examples/mp-blink-tight/dist/firmware.bin";

    private const int MpIters = 4000;

    // Bounded loop with markers so we can measure exactly the toggle phase, plus a
    // GC report so we can read MicroPython's live RAM footprint.
    private static readonly string Script =
        "import gc, machine\n" +
        "from machine import Pin\n" +
        "led = Pin(25, Pin.OUT)\n" +
        "gc.collect()\n" +
        "print('MEM', gc.mem_alloc(), gc.mem_free())\n" +
        "print('SSS')\n" +
        "for i in range(" + MpIters + "):\n" +
        "    led.value(1)\n" +
        "    led.value(0)\n" +
        "print('EEE')\n";

    private const long Window = 4_000_000;   // instruction window to measure over

    // Hook SIO GPIO changes and count transitions of GP25 over a fixed window of
    // instructions, after the toggle loop has started.
    private static (long edges, long instrs) MeasureToggles(PicoSimulation pico)
    {
        var sio = pico.Rp2040.Sio;
        bool prev = sio.GetGpioOut(25);
        long edges = 0;
        sio.OnGpioChanged = () =>
        {
            bool cur = sio.GetGpioOut(25);
            if (cur != prev) { edges++; prev = cur; }
        };

        // Warm up: advance until the first GP25 edge (the loop is running).
        long warm = 0;
        while (edges == 0 && warm < 60_000_000) { pico.RunInstructions(50_000); warm += 50_000; }

        long startEdges = edges;
        long startInstr = pico.InstructionCount;
        long ran = 0;
        while (ran < Window) { pico.RunInstructions(100_000); ran += 100_000; }
        long instrs = pico.InstructionCount - startInstr;
        return (edges - startEdges, instrs);
    }

    [Fact]
    public void Native_PyMcu_vs_Interpreted_MicroPython()
    {
        if (!File.Exists(PyMcuBin))
            throw new InvalidOperationException(
                $"PyMCU binary not built: {PyMcuBin}. Run `pymcu build` in examples/mp-blink-tight.");

        // ── (1) PyMCU native binary ──
        long pymcuEdges, pymcuInstr;
        using (var pico = new PicoSimulation(withUsbCdc: false))
        {
            pico.LoadFlash(File.ReadAllBytes(PyMcuBin));
            (pymcuEdges, pymcuInstr) = MeasureToggles(pico);
        }
        double pymcuInstrPerEdge = (double)pymcuInstr / Math.Max(1, pymcuEdges);

        // ── (2) Real MicroPython interpreting the same source ──
        var runner = MicroPythonRunner.CreateAsync("v1.21.0").GetAwaiter().GetResult();
        if (runner is null)
            throw new InvalidOperationException("MicroPython firmware unavailable (no network/cache).");

        long mpEdges, mpInstr;
        try
        {
            runner.WaitForPrompt();
            runner.WriteFile("main.py", Script);
            var sim = runner.Simulation;
            // Soft reset (Ctrl-D): reboot and auto-run main.py. The Pico REPL is on
            // USB-CDC; inject on both channels so the active one takes it.
            runner.UsbCdc.Clear();
            runner.UsbCdc.InjectString("\x04");
            runner.Uart.InjectString("\x04");
            // Channel-aware: run until the loop's start marker, then time it to the end.
            if (!runner.WaitForOutput("MEM ", 30_000))
                throw new InvalidOperationException("MicroPython never reported memory.");
            // Parse "MEM <alloc> <free>" from the REPL channel.
            string memChan = runner.UsbCdc.Text;
            int mi = memChan.LastIndexOf("MEM ", StringComparison.Ordinal);
            string memLine = memChan.Substring(mi).Split('\n')[0].Trim();
            var parts = memLine.Split(' ');
            long mpAlloc = long.Parse(parts[1]);
            long mpFree = long.Parse(parts[2]);
            _out.WriteLine($"MicroPython GC heap: alloc={mpAlloc} free={mpFree} total={mpAlloc + mpFree} bytes (+ static RAM/stack)");

            if (!runner.WaitForOutput("SSS", 30_000))
                throw new InvalidOperationException("MicroPython never reached the loop start marker.");
            long sInstr = sim.InstructionCount;
            if (!runner.WaitForOutput("EEE", 60_000))
                throw new InvalidOperationException("MicroPython never finished the toggle loop.");
            mpInstr = sim.InstructionCount - sInstr;
            mpEdges = 2L * MpIters;   // value(1)+value(0) per iteration = 2 edges
        }
        finally
        {
            runner.DisposeAsync().GetAwaiter().GetResult();
        }
        double mpInstrPerEdge = (double)mpInstr / Math.Max(1, mpEdges);

        double speedup = mpInstrPerEdge / Math.Max(1e-9, pymcuInstrPerEdge);

        _out.WriteLine("=== Tight GP25 toggle loop (same MicroPython source) ===");
        _out.WriteLine($"PyMCU native : {pymcuInstrPerEdge,10:F2} instr/edge  ({pymcuEdges} edges in {pymcuInstr} instr)");
        _out.WriteLine($"MicroPython  : {mpInstrPerEdge,10:F2} instr/edge  ({mpEdges} edges in {mpInstr} instr)");
        _out.WriteLine($"SPEEDUP      : {speedup,10:F1}x  (PyMCU native vs interpreted)");

        Assert.True(pymcuInstrPerEdge < 10, "PyMCU native should toggle in a handful of instructions");
        Assert.True(speedup > 50, $"PyMCU native must be far faster than the interpreter (was {speedup:F0}x)");
    }
}

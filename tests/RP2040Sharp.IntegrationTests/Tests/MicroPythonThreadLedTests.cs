using FluentAssertions;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// The Wokwi-style blink-on-a-thread sketch: a <c>_thread</c> worker that toggles the on-board LED
/// with <c>time.sleep(0.5)</c> between edges. It exercises three things Core 1 needs and Core 0 had
/// to itself before this test existed:
///
///  1. <b>Bootrom float</b>. <c>time.sleep(float)</c> calls the ROM's 'SF'/'SD' tables, whose code is
///     stripped from the shipped images (mufplib is not redistributable — see BootromFloat) and
///     replaced with BKPT. Core 1 needs the same native hooks Core 0 gets, or it faults into lockup
///     on the first float operation — with MicroPython's <c>bkpt</c> HardFault handler, terminally.
///  2. <b>Shared IRQ lines</b>. The timer alarm that ends the sleep must reach both NVICs.
///  3. <b>Cluster-wide SEV</b>. pico-sdk's alarm callback runs on Core 0 and releases Core 1 from
///     <c>best_effort_wfe_or_timeout</c>'s WFE with a plain <c>sev</c>; a core-local event register
///     leaves Core 1 asleep after exactly one LED edge.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonThreadLedTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.28.0";

    private const string MainPy =
        "import time, _thread, machine\n" +
        "def task(n, delay):\n" +
        "    led = machine.Pin('LED', machine.Pin.OUT)\n" +
        "    for i in range(n):\n" +
        "        led.high()\n" +
        "        time.sleep(delay)\n" +
        "        led.low()\n" +
        "        time.sleep(delay)\n" +
        "    print('done')\n" +
        "\n" +
        "_thread.start_new_thread(task, (4, 0.05))\n";

    [Fact]
    public async Task Thread_BlinkingTheLedWithFloatSleeps_RunsToCompletionOnCore1()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt(30_000).Should().BeTrue("MicroPython must reach the REPL to write files");
        runner.WriteFile("main.py", MainPy).Should().BeTrue("WriteFile should succeed when the REPL is ready");

        var sio = runner.Simulation.Rp2040.Sio;
        var lastLed = sio.GetGpioOut(25);
        var edges = 0;

        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x04");   // soft reset → main.py

        var tail = -1;
        for (var batch = 0; batch < 400 && tail != 0; batch++)
        {
            runner.Simulation.RunMilliseconds(20);
            var led = sio.GetGpioOut(25);
            if (led != lastLed) { edges++; lastLed = led; }
            // Keep stepping past "done" so the worker's RETURN through core1_wrapper is covered too:
            // it pops the LR the bootrom left behind, and a wrong one lands Core 1 in lockup.
            if (tail > 0) tail--;
            else if (runner.UsbCdc.Text.Contains("done", StringComparison.Ordinal)) tail = 10;
        }

        runner.Simulation.Cpu1.Registers.PC.Should().NotBe(0,
            "a finished thread must return into the bootrom's wait_for_vector loop, not to PC = 0");
        runner.Simulation.Cpu1.IsLockedUp.Should().BeFalse(
            "Core 1 must not lock up: the stripped bootrom float routines are BKPT, and MicroPython's " +
            "HardFault handler is BKPT too, so one unhooked float call is terminal");
        runner.UsbCdc.Text.Should().Contain("done",
            "the worker thread must finish its blink loop on Core 1");
        edges.Should().Be(8, "4 iterations of high/low must produce 8 GP25 edges — one edge means " +
                             "Core 1 went to sleep in WFE and no SEV ever woke it");
    }

    /// <summary>
    /// Two threads one after the other. The second <c>start_new_thread</c> only works if Core 1 came
    /// to rest somewhere the launch handshake can restart it — the bootrom's wait_for_vector loop —
    /// rather than falling off the end of its stack when the first thread returned.
    /// </summary>
    [Fact]
    public async Task Thread_TwoInSequence_RelaunchesCore1()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt(30_000).Should().BeTrue("MicroPython must reach the REPL to write files");
        runner.WriteFile("main.py",
            "import time, _thread\n" +
            "def w(tag):\n" +
            "    time.sleep(0.05)\n" +
            "    print('T', tag)\n" +
            "\n" +
            "_thread.start_new_thread(w, ('a',))\n" +
            "time.sleep(0.5)\n" +
            "_thread.start_new_thread(w, ('b',))\n" +
            "time.sleep(0.5)\n" +
            "print('both')\n").Should().BeTrue();

        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x04");
        for (var batch = 0; batch < 400 && !runner.UsbCdc.Text.Contains("both", StringComparison.Ordinal); batch++)
            runner.Simulation.RunMilliseconds(20);

        var text = runner.UsbCdc.Text;
        runner.Simulation.Cpu1.IsLockedUp.Should().BeFalse("neither thread may leave Core 1 in lockup");
        text.Should().Contain("T a", "the first thread must run on Core 1");
        text.Should().Contain("T b", "Core 1 must be relaunchable for the second thread");
        text.Should().Contain("both", "the main thread must survive both launches");
    }
}

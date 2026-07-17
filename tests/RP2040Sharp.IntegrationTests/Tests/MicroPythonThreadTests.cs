using FluentAssertions;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// End-to-end tests for MicroPython's <c>_thread</c> module on the second core.
///
/// MicroPython runs <c>multicore_reset_core1()</c> (force PROC1 off then on via
/// <c>PSM.FRCE_OFF</c>) before every <c>_thread.start_new_thread</c>, then blocks in
/// <c>multicore_fifo_pop_blocking()</c> waiting for Core 1's bootrom "ready" word.  Before
/// the PSM-driven Core 1 reset was emulated, Core 0 slept in WFE forever and any <c>_thread</c>
/// program hung — this is what crashes the Wokwi pedestrian-crossing sketch (project
/// 469350452950129665) and hangs it under this emulator.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonThreadTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    // v1.28.0: this is where MicroPython's _thread path runs multicore_reset_core1() before
    // launching Core 1 (older builds such as v1.21.0 do not, so they cannot exercise the bug).
    private const string Version = "v1.28.0";

    /// <summary>
    /// A <c>_thread.start_new_thread</c> must return (not hang in multicore_reset_core1's
    /// blocking FIFO pop) and the spawned thread must actually run on Core 1.
    /// </summary>
    [Fact]
    public async Task Thread_StartNewThread_RunsOnCore1_WithoutHanging()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue("MicroPython must reach the REPL to write files");

        // Mirror the Wokwi crosswalk: a worker thread runs alongside the main loop.
        const string mainPy =
            "import _thread, time\n" +
            "def worker():\n" +
            "    for i in range(3):\n" +
            "        print('CORE1', i)\n" +
            "        time.sleep(0.05)\n" +
            "_thread.start_new_thread(worker, ())\n" +
            "time.sleep(1)\n" +
            "print('MAIN-DONE')\n";

        runner.WriteFile("main.py", mainPy)
              .Should().BeTrue("WriteFile should succeed when the REPL is ready");

        runner.SoftReset(timeoutMs: 30_000)
              .Should().BeTrue("MicroPython must return to the REPL after main.py — not hang at start_new_thread");

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;

        // start_new_thread returned and the main thread ran to completion.
        text.Should().Contain("MAIN-DONE",
            "the main thread must continue past start_new_thread instead of blocking in WFE");
        // Core 1 actually executed the spawned thread body.
        text.Should().Contain("CORE1",
            "the worker thread must run on Core 1");
    }
}

using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Integration tests that verify PIO behaviour via the MicroPython <c>rp2</c> module.
///
/// Each test boots MicroPython, writes a small PIO script to the filesystem, soft-resets
/// the VM so the script runs as <c>main.py</c>, then checks the output.
///
/// This validates both the PIO peripheral emulation AND the MicroPython <c>rp2</c> binding
/// layer end-to-end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonPioTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.21.0";

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a Script embedded resource by name (without the .py extension).
    /// </summary>
    private static string LoadScript(string name)
    {
        var asm = typeof(MicroPythonPioTests).Assembly;
        var resourceName = $"RP2040Sharp.IntegrationTests.Scripts.{name}.py";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded script '{resourceName}' not found.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── SET PINS ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A PIO program that uses SET PINS to drive a GPIO pin high must produce the expected
    /// output when the pin value is read back via <c>machine.Pin</c>.
    /// Exercises: SM creation, <c>set_base</c>, <c>active(1)</c>, GPIO OE from PIO.
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_SetPins_DrivesGpioHigh()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue("MicroPython must reach REPL before running PIO test");

        var script = LoadScript("pio_set_pins");
        runner.WriteFile("main.py", script).Should().BeTrue("script file must be written to VFS");

        runner.SoftReset(timeoutMs: 25_000).Should().BeTrue("MicroPython must return to REPL after running PIO script");

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("pin: 1",
            "PIO SET PINS must drive GPIO 16 high; machine.Pin.value() must return 1");
    }

    // ── TX → RX FIFO round-trip ───────────────────────────────────────────────

    /// <summary>
    /// A PIO program that does PULL → MOV ISR,OSR → PUSH should return the exact word
    /// written to the TX FIFO via the RX FIFO.
    /// Exercises: PULL (blocking), MOV ISR/OSR, PUSH, <c>sm.put()</c>, <c>sm.get()</c>.
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_TxRxFifo_RoundTrip()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var script = LoadScript("pio_tx_rx_fifo");
        runner.WriteFile("main.py", script).Should().BeTrue();

        runner.SoftReset(timeoutMs: 25_000).Should().BeTrue();

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("0xdeadbeef",
            "PIO PULL→MOV ISR,OSR→PUSH round-trip must return the original 0xDEADBEEF sentinel");
    }

    // ── GPIO loopback (OUT → IN via shared pin window) ───────────────────────

    /// <summary>
    /// Two PIO state machines on the same GPIO pin window: SM0 drives 8 bits via OUT PINS,
    /// SM1 reads them back via IN PINS.  The byte read from SM1 RX FIFO must match the byte
    /// written to SM0 TX FIFO.
    /// Exercises: multi-SM setup, autopull, autopush, <c>out_base</c>/<c>in_base</c>.
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_FifoLoopback_ReturnsOriginalByte()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var script = LoadScript("pio_fifo_loopback");
        runner.WriteFile("main.py", script).Should().BeTrue();

        runner.SoftReset(timeoutMs: 25_000).Should().BeTrue();

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("0xa5",
            "PIO OUT PINS → IN PINS loopback must return the 0xA5 sentinel byte");
    }

    // ── Late producer (autopull stall regression) ────────────────────────────

    /// <summary>
    /// Functional check (against real MicroPython) of the autopull late-producer path: SM0 runs
    /// OUT PINS,8 with autopull, stalls on the empty TX FIFO, and must drive the pins with the
    /// byte once it is produced late via <c>sm.put()</c>. The strict regression that pins down the
    /// PC-skip bug lives in the unit suite (<c>LateProducerAutopull</c>).
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_LateProducer_AutopullStall_DrivesPins()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var script = LoadScript("pio_late_producer");
        runner.WriteFile("main.py", script).Should().BeTrue();

        runner.SoftReset(timeoutMs: 25_000).Should().BeTrue();

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("late: 0x3c",
            "an OUT stalled on autopull must drive the pins once the byte is produced late");
    }

    // ── MOV operators (invert / bit-reverse) ──────────────────────────────────

    /// <summary>
    /// Verify the PIO MOV invert and bit-reverse operators end-to-end via real MicroPython.
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_MovOperators_InvertAndReverse()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var script = LoadScript("pio_mov_ops");
        runner.WriteFile("main.py", script).Should().BeTrue();

        runner.SoftReset(timeoutMs: 25_000).Should().BeTrue();

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("inv: 0xffff0000", "MOV ISR, ~OSR must bitwise-invert the word");
        text.Should().Contain("rev: 0x80000000", "MOV ISR, ::OSR must bit-reverse the word");
    }

    // ── REPL-based smoke test ─────────────────────────────────────────────────

    /// <summary>
    /// Verify that <c>import rp2</c> succeeds in the MicroPython REPL on the emulated Pico,
    /// and that <c>rp2.PIO.OUT_LOW</c> evaluates to the expected constant.  In CPython/MicroPython
    /// for RP2040 the pin-init enum is <c>IN_LOW=0, IN_HIGH=1, OUT_LOW=2, OUT_HIGH=3</c>
    /// (see <c>ports/rp2/modrp2.c</c> in MicroPython).
    /// This is a minimal sanity check that the rp2 module is present and not broken.
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_ImportRp2_Succeeds()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var found = runner.ExecuteAndWait("import rp2; print(rp2.PIO.OUT_LOW)", "2");
        found.Should().BeTrue("import rp2 must succeed and rp2.PIO.OUT_LOW must equal 2");
    }

    /// <summary>
    /// Verify that creating a simple PIO <see cref="rp2.StateMachine"/> via the REPL does not
    /// crash MicroPython (no MemoryError, AttributeError, or HardFault).
    /// </summary>
    [Fact]
    public async Task MicroPython_Pio_StateMachineCreate_DoesNotCrash()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        // Define a minimal no-op PIO program and instantiate a SM
        runner.ExecuteCompound("@rp2.asm_pio()\ndef noop_prog():\n    wrap_target()\n    nop()\n    wrap()");

        var found = runner.ExecuteAndWait(
            "sm = rp2.StateMachine(0, noop_prog); sm.active(1); print('ok')", "ok");
        found.Should().BeTrue("creating and activating a StateMachine must succeed without error");

        // Cleanup
        runner.ExecuteAndWait("sm.active(0)", ">>> ");
    }
}

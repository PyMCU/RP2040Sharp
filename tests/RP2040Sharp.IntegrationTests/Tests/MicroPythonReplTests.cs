using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Tests that exercise the MicroPython REPL: injecting code lines and verifying the output
/// captured from the emulated UART.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonReplTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    // Use a single firmware version for REPL tests — the behaviour is stable across versions.
    private const string Version = "v1.21.0";

    [Fact]
    public async Task Repl_CanEvaluateArithmeticExpression()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var found = runner.ExecuteAndWait("print(1 + 2)", "3");
        found.Should().BeTrue("1 + 2 should evaluate to 3");
    }

    [Fact]
    public async Task Repl_CanReadSysVersion()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var found = runner.ExecuteAndWait("import sys; print(sys.version)", "MicroPython");
        found.Should().BeTrue("sys.version should contain 'MicroPython'");
    }

    [Fact]
    public async Task Repl_CanReadSysPlatform()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        var found = runner.ExecuteAndWait("import sys; print(sys.platform)", "rp2");
        found.Should().BeTrue("sys.platform should be 'rp2' for MicroPython on RP2040");
    }

    [Fact]
    public async Task Repl_CanDefineAndCallFunction()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        // def is a compound statement in MicroPython REPL; ExecuteCompound sends
        // the def line, waits for "... " continuation, then sends a blank line to
        // complete the definition and waits for the next ">>> " prompt.
        runner.ExecuteCompound("def greet(name): return 'Hi ' + name");

        var found = runner.ExecuteAndWait("print(greet('world'))", "Hi world");
        found.Should().BeTrue("user-defined function should be callable from REPL");
    }

    [Fact]
    public async Task Repl_MultipleCommands_ProduceCorrectOutput()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        runner.ExecuteAndWait("x = 10", ">>> ");
        runner.ExecuteAndWait("y = 32", ">>> ");
        var found = runner.ExecuteAndWait("print(x + y)", "42");
        found.Should().BeTrue("accumulated variable state should be preserved across REPL lines");
    }

    [Fact]
    public async Task Repl_MachineUniqueId_IsNotAllZero()
    {
        // Regression: SsiPeripheral.UniqueId used to default to an all-zero byte[8], so
        // machine.unique_id() always read back "0000000000000000" instead of a real per-chip id.
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        runner.Execute("import machine, ubinascii; print(ubinascii.hexlify(machine.unique_id()))");
        var found = runner.WaitForOutput(text => text.Contains("b'", StringComparison.Ordinal)
                                                  && !text.Contains("b'0000000000000000'", StringComparison.Ordinal));
        found.Should().BeTrue("machine.unique_id() must not read back as all zero bytes");
    }

    [Fact]
    public async Task Repl_MachineUniqueId_DiffersAcrossTwoBoards()
    {
        // Regression: two independently-booted boards both defaulted to the same all-zero
        // unique id, so client_id = b"pico-" + hexlify(machine.unique_id()) collided between
        // boards (e.g. an MQTT broker would drop the first connection when the second joined).
        if (ShouldSkip) return;

        await using var boardA = await MicroPythonRunner.CreateAsync(Version);
        await using var boardB = await MicroPythonRunner.CreateAsync(Version);
        if (boardA is null || boardB is null) return;

        boardA.WaitForPrompt().Should().BeTrue();
        boardB.WaitForPrompt().Should().BeTrue();

        boardA.Execute("import machine, ubinascii; print(ubinascii.hexlify(machine.unique_id()))");
        boardA.WaitForOutput("b'", 5_000).Should().BeTrue();
        var idA = boardA.Uart.Text + boardA.UsbCdc.Text;

        boardB.Execute("import machine, ubinascii; print(ubinascii.hexlify(machine.unique_id()))");
        boardB.WaitForOutput("b'", 5_000).Should().BeTrue();
        var idB = boardB.Uart.Text + boardB.UsbCdc.Text;

        idA.Should().NotBe(idB, "two boards must not report the same machine.unique_id()");
    }
}

using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Integration tests that boot real CircuitPython firmware on the RP2040 emulator
/// and verify the REPL prompt, USB-CDC enumeration, and basic boot behaviour.
///
/// These tests require network access on the first run to download the firmware from
/// downloads.circuitpython.org. Subsequent runs use the cached UF2 in the system
/// temp directory.
///
/// Set environment variable SKIP_INTEGRATION_TESTS=1 to skip all tests in CI pipelines
/// that cannot access the internet.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CircuitPythonBootTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    // ── USB-CDC enumeration ───────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_UsbCdcEnumerates(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        // 4 s, not 2: RunMilliseconds used to charge its budget in instructions while the timer
        // advanced in cycles, so the old "2 s" handed the firmware nearly 3 s of simulated time.
        for (var i = 0; i < 40 && !runner.UsbCdc.IsConnected; i++)
            runner.Simulation.RunMilliseconds(100);

        runner.UsbCdc.IsConnected.Should().BeTrue(
            $"CircuitPython {version} USB CDC should complete enumeration within 4 s");
    }

    // ── REPL prompt ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_BootsAndShowsReplPrompt(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        var booted = runner.WaitForPrompt(timeoutMs: 20_000);

        booted.Should().BeTrue(
            $"CircuitPython {version} should produce a REPL prompt within 20 s of simulated time");
    }

    // ── Version header ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_OutputsVersionHeader(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000)
            .Should().BeTrue($"CircuitPython {version} must reach REPL");

        var found = runner.ExecuteAndWait("import sys; print(sys.version)", "CircuitPython");
        found.Should().BeTrue("sys.version should contain 'CircuitPython'");
    }

    // ── No hard fault ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_NoHardFault_AfterStartup(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        runner.Simulation.RunMilliseconds(500);

        runner.Simulation.Cpu.Registers.IPSR.Should().NotBe(3u,
            $"CircuitPython {version} must not trigger a HardFault during startup");
    }

    // ── Hello World ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_OutputsHelloWorld(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000)
            .Should().BeTrue($"CircuitPython {version} must reach REPL");

        var found = runner.ExecuteAndWait("print('Hello, CircuitPython!')", "Hello, CircuitPython!");
        found.Should().BeTrue("print() output should be captured");
    }

    // ── sys.platform ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9.2.1")]
    public async Task CircuitPython_SysPlatform_IsRP2040(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await CircuitPythonRunner.CreateAsync(version);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000)
            .Should().BeTrue($"CircuitPython {version} must reach REPL");

        // CircuitPython reports "RP2040" via sys.platform on Pico
        // In CircuitPython 9.x, sys.platform returns "RP2040" (upper-case).
        var found = runner.ExecuteAndWait("import sys; print(sys.platform)", "RP2040");
        found.Should().BeTrue("sys.platform should be 'RP2040' for CircuitPython on Pico");
    }
}

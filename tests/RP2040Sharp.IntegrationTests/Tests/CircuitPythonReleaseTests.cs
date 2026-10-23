using System.Text;
using RP2040.TestKit;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

[Trait("Category", "Release")]
public sealed class CircuitPythonReleaseTests
{
    private static bool Required =>
        Environment.GetEnvironmentVariable("RELEASE_GATE_REQUIRED") == "true";

    private static bool ShouldSkip =>
        !Required && Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "10.3.1";

    private const string TickCode =
        "import time\nn = 0\nwhile True:\n    n += 1\n    print('tick', n)\n    time.sleep(0.05)\n";

    private const string PeripheralCode =
        "import time, board, busio, pwmio, analogio\n" +
        "i2c = busio.I2C(board.GP5, board.GP4)\n" +
        "spi = busio.SPI(board.GP18, board.GP19, board.GP16)\n" +
        "uart = busio.UART(board.GP8, board.GP9)\n" +
        "pwm = pwmio.PWMOut(board.GP15, frequency=1000)\n" +
        "adc = analogio.AnalogIn(board.A0)\n" +
        "adc.value\n" +
        "print('periph', 'ok')\n" +
        "while True:\n" +
        "    time.sleep(0.05)\n";

    private static (string, byte[]) Py(string path, string content) => (path, Encoding.ASCII.GetBytes(content));

    private static async Task<CircuitPythonRunner?> CreateAsync(IReadOnlyList<(string, byte[])> files)
    {
        var runner = await CircuitPythonRunner.CreateWithFilesAsync(Version, files);
        if (runner is null)
            Required.Should().BeFalse($"CircuitPython {Version} firmware must be available for the release gate");
        return runner;
    }

    private static void RunUntil(CircuitPythonRunner runner, Func<bool> condition, double timeoutMs = 5_000)
    {
        for (var elapsed = 0.0; elapsed < timeoutMs && !condition(); elapsed += 10)
            runner.Simulation.RunMilliseconds(10);
    }

    private static void Reload(CircuitPythonRunner runner)
    {
        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x03");
        runner.Simulation.RunMilliseconds(200);
        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x04");
    }

    private static void OpenPortLate(CircuitPythonRunner runner)
    {
        RunUntil(runner, () => runner.UsbCdc.IsConnected);
        runner.UsbCdc.IsConnected.Should().BeTrue("USB CDC must enumerate");
        runner.Simulation.RunMilliseconds(500);
        Reload(runner);
    }

    [Fact]
    public async Task Boot_RunsCodePy_AndReachesRepl()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync([Py("code.py", "print('boot', 6 * 7)\n")]);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000)
            .Should().BeTrue($"CircuitPython {Version} must reach the REPL, got: {runner.UsbCdc.Text}");
        runner.SoftReset(timeoutMs: 20_000).Should().BeTrue("Ctrl-D must re-run code.py and return to the REPL");
        runner.UsbCdc.Text.Should().Contain("boot 42");
    }

    [Fact]
    public async Task NeverEndingCodePy_OutputArrives_AfterLatePortOpenRecovery()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync([Py("code.py", TickCode)]);
        if (runner is null) return;

        OpenPortLate(runner);

        runner.Simulation.RunUntilOutput(runner.UsbCdc, "tick 3", timeoutMs: 15_000)
            .Should().BeTrue($"Ctrl-C + Ctrl-D on a late-opened port must re-run code.py and show its output, got: {runner.UsbCdc.Text}");
    }

    [Fact]
    public async Task Peripherals_RecreateAfterReload()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync([Py("code.py", PeripheralCode)]);
        if (runner is null) return;
        runner.Simulation.Gpio[4].ForceInput(true);
        runner.Simulation.Gpio[5].ForceInput(true);

        for (var i = 0; i < 2; i++)
        {
            if (i == 0) OpenPortLate(runner);
            else Reload(runner);
            runner.Simulation.RunUntilOutput(runner.UsbCdc, t => t.Contains("periph ok") || t.Contains("Error"), timeoutMs: 15_000);
            runner.UsbCdc.Text.Should().Contain("periph ok", $"reload {i + 1} must re-create I2C, SPI, UART, PWM and ADC");
            runner.UsbCdc.Text.Should().NotContain("Traceback");
        }
    }

    [Fact]
    public async Task MicrocontrollerReset_RebootsTwice()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync([Py("code.py", "print('code', 'ran')\n")]);
        if (runner is null) return;
        runner.WaitForPrompt(timeoutMs: 20_000).Should().BeTrue($"CircuitPython {Version} must reach the REPL");

        for (var i = 0; i < 2; i++)
        {
            var before = runner.Simulation.Rp2040.ResetCount;
            runner.Execute("import microcontroller; microcontroller.reset()");
            RunUntil(runner, () => runner.Simulation.Rp2040.ResetCount > before);
            runner.Simulation.Rp2040.ResetCount.Should().Be(before + 1, "microcontroller.reset() must reboot the chip once");

            OpenPortLate(runner);
            runner.Simulation.RunUntilOutput(runner.UsbCdc, "code ran", timeoutMs: 15_000)
                .Should().BeTrue($"reboot {i + 1} must boot CircuitPython and run code.py, got: {runner.UsbCdc.Text}");
            runner.WaitForPrompt(timeoutMs: 20_000).Should().BeTrue($"reboot {i + 1} must reach the REPL");
            runner.ExecuteAndWait("print('alive', 6 * 7)", "alive 42").Should().BeTrue();
        }
    }

    [Fact]
    public async Task MicrocontrollerReset_ReleasesPinsAndPio()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync([Py("code.py", "print('code', 'ran')\n")]);
        if (runner is null) return;
        runner.WaitForPrompt(timeoutMs: 20_000).Should().BeTrue($"CircuitPython {Version} must reach the REPL");
        var rp = runner.Simulation.Rp2040;

        runner.ExecuteAndWait(
            "import board, digitalio, rp2pio, array, microcontroller; o = digitalio.DigitalInOut(board.GP15); o.switch_to_output(True); " +
            "sm = rp2pio.StateMachine(array.array('H', [0xFF01, 0xFF00]), frequency=200_000, first_set_pin=board.GP16); print('pio', 'on')", "pio on")
            .Should().BeTrue($"the pin and the state machine must start, got: {runner.UsbCdc.Text}");
        (rp.Pio0.ReadWord(0x50200000) | rp.Pio1.ReadWord(0x50300000)).Should().NotBe(0u, "a state machine runs");

        var before = rp.ResetCount;
        runner.Execute("microcontroller.reset()");
        RunUntil(runner, () => rp.ResetCount > before);
        OpenPortLate(runner);
        runner.Simulation.RunUntilOutput(runner.UsbCdc, "code ran", timeoutMs: 15_000)
            .Should().BeTrue($"CircuitPython must boot again, got: {runner.UsbCdc.Text}");

        rp.IoBank0.GetPadOutputEnable(15).Should().BeFalse("microcontroller.reset() releases GP15");
        rp.IoBank0.GetPadOutputEnable(16).Should().BeFalse("microcontroller.reset() releases GP16");
        (rp.Pio0.ReadWord(0x50200000) | rp.Pio1.ReadWord(0x50300000)).Should().Be(0u, "no state machine survives the reset");
    }

    [Fact]
    public async Task Fat_LongNamedModule_AndLibPackage_Import()
    {
        if (ShouldSkip) return;

        await using var runner = await CreateAsync(
        [
            Py("code.py", "print('code', 'ran')\n"),
            Py("release_gate_long_module.py", "VALUE = 'long'\n"),
            Py("lib/release_gate_pkg/__init__.py", "NAME = 'pkg'\n"),
            Py("lib/release_gate_pkg/long_named_submodule.py", "SUB = 'sub'\n"),
        ]);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000).Should().BeTrue($"CircuitPython {Version} must reach the REPL");
        runner.Simulation.RunMilliseconds(200);

        runner.ExecuteAndWait(
                "import release_gate_long_module as m, release_gate_pkg as p; from release_gate_pkg import long_named_submodule as s; print('fat', m.VALUE, p.NAME, s.SUB)",
                "fat long pkg sub", timeoutMs: 10_000)
            .Should().BeTrue($"long names and lib/ packages must import, got: {runner.UsbCdc.Text}");
    }
}

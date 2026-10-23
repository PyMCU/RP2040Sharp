using RP2040.TestKit;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

[Trait("Category", "Release")]
public sealed class MicroPythonReleaseTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.28.0";

    private const string TickMain =
        "import time\nn = 0\nwhile True:\n    n += 1\n    print('tick', n)\n    time.sleep_ms(50)\n";

    private const string PeripheralMain =
        "import time\n" +
        "from machine import Pin, I2C, SPI, UART, PWM, ADC\n" +
        "i2c = I2C(0, scl=Pin(5), sda=Pin(4))\n" +
        "spi = SPI(0)\n" +
        "uart = UART(1, 9600)\n" +
        "pwm = PWM(Pin(15))\n" +
        "pwm.freq(1000)\n" +
        "adc = ADC(26)\n" +
        "adc.read_u16()\n" +
        "print('periph', 'ok')\n" +
        "while True:\n" +
        "    time.sleep_ms(50)\n";

    private static async Task<MicroPythonRunner?> BootAsync()
    {
        var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return null;
        runner.WaitForPrompt(timeoutMs: 15_000)
            .Should().BeTrue($"MicroPython {Version} must reach the REPL");
        return runner;
    }

    private static void RunUntil(MicroPythonRunner runner, Func<bool> condition, double timeoutMs = 5_000)
    {
        for (var elapsed = 0.0; elapsed < timeoutMs && !condition(); elapsed += 10)
            runner.Simulation.RunMilliseconds(10);
    }

    private static void Reload(MicroPythonRunner runner)
    {
        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x03");
        runner.Simulation.RunMilliseconds(100);
        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x04");
    }

    private static void RebootAndWaitForRepl(MicroPythonRunner runner, string trigger)
    {
        var before = runner.Simulation.Rp2040.ResetCount;
        runner.Execute(trigger);
        RunUntil(runner, () => runner.Simulation.Rp2040.ResetCount > before);
        runner.Simulation.Rp2040.ResetCount.Should().Be(before + 1, $"'{trigger}' must reboot the chip once");
        runner.UsbCdc.Clear();
        runner.WaitForPrompt(timeoutMs: 15_000)
            .Should().BeTrue($"MicroPython must reach the REPL again after '{trigger}'");
        runner.ExecuteAndWait("print('alive', 6 * 7)", "alive 42")
            .Should().BeTrue($"the REPL must evaluate code after '{trigger}'");
    }

    [Fact]
    public async Task Boot_ReachesRepl_AndEvaluates()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.ExecuteAndWait("import sys; print(sys.implementation.name, 6 * 7)", "micropython 42")
            .Should().BeTrue();
    }

    [Fact]
    public async Task NeverEndingMain_OutputArrives_AfterLatePortOpenRecovery()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.WriteFile("main.py", TickMain).Should().BeTrue();
        var before = runner.Simulation.Rp2040.ResetCount;
        runner.Execute("import machine; machine.reset()");
        RunUntil(runner, () => runner.Simulation.Rp2040.ResetCount > before);
        RunUntil(runner, () => runner.UsbCdc.IsConnected);
        runner.Simulation.RunMilliseconds(500);

        Reload(runner);

        runner.Simulation.RunUntilOutput(runner.UsbCdc, "tick 3", timeoutMs: 10_000)
            .Should().BeTrue($"Ctrl-C + Ctrl-D on a late-opened port must re-run main.py and show its output, got: {runner.UsbCdc.Text}");
    }

    [Fact]
    public async Task Peripherals_RecreateAfterReload()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.WriteFile("main.py", PeripheralMain).Should().BeTrue();
        runner.UsbCdc.Clear();
        runner.UsbCdc.InjectString("\x04");
        runner.Simulation.RunUntilOutput(runner.UsbCdc, "periph ok", timeoutMs: 10_000)
            .Should().BeTrue($"main.py must create I2C, SPI, UART, PWM and ADC, got: {runner.UsbCdc.Text}");

        for (var i = 0; i < 2; i++)
        {
            Reload(runner);
            runner.Simulation.RunUntilOutput(runner.UsbCdc, "periph ok", timeoutMs: 10_000)
                .Should().BeTrue($"reload {i + 1} must re-create every peripheral, got: {runner.UsbCdc.Text}");
            runner.UsbCdc.Text.Should().NotContain("Traceback");
        }
    }

    [Fact]
    public async Task MachineReset_RebootsTwice()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        RebootAndWaitForRepl(runner, "import machine; machine.reset()");
        RebootAndWaitForRepl(runner, "import machine; machine.reset()");
    }

    [Fact]
    public async Task Watchdog_RebootsTwice()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        RebootAndWaitForRepl(runner, "from machine import WDT; w = WDT(timeout=100)");
        runner.ExecuteAndWait("import machine; print('cause', machine.reset_cause() == machine.WDT_RESET)", "cause True")
            .Should().BeTrue("the firmware must see the watchdog as the reset cause");
        RebootAndWaitForRepl(runner, "from machine import WDT; w = WDT(timeout=100)");
    }
}

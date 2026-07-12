using FluentAssertions;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Regression tests for GPIO interrupts raised from <c>machine.Pin.irq</c>.
/// IO_IRQ_BANK0 is level-held by IoBank0 (re-asserted until the handler acks INTR),
/// so without ARMv6-M execution-priority gating the IRQ preempts its own handler
/// after every instruction and the core hard-faults from stack overflow.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonGpioIrqTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.21.0";

    /// <summary>
    /// Registers a rising-edge IRQ callback on GP15, injects external edges via
    /// <see cref="RP2040.Peripherals.Gpio.GpioPin.ForceInput"/>, and verifies the
    /// callback counted every edge and the REPL is still alive afterwards.
    /// </summary>
    [Fact]
    public async Task GpioIrq_ExternalRisingEdges_FireCallbackWithoutHardFault()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue("MicroPython must reach REPL");

        runner.ExecuteAndWait("from machine import Pin", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("n = 0", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("exec('def cb(p):\\n global n\\n n += 1')", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("Pin(15, Pin.IN).irq(cb, Pin.IRQ_RISING)", ">>> ")
              .Should().BeTrue("registering the IRQ must return to the REPL");

        var pin = runner.Simulation.Gpio[15];
        for (var i = 0; i < 5; i++)
        {
            pin.ForceInput(true);
            runner.Simulation.RunMilliseconds(5);
            pin.ForceInput(false);
            runner.Simulation.RunMilliseconds(5);
        }

        runner.ExecuteAndWait("print('edges =', n)", "edges =", 10_000)
              .Should().BeTrue("the REPL must answer after the edges, so the core did not lock up");
        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("edges = 5", "the callback must run once per rising edge");
    }

    /// <summary>
    /// A burst of edges much faster than the Python scheduler drains them must not
    /// wedge the core; the REPL must survive and the counter must have advanced.
    /// </summary>
    [Fact]
    public async Task GpioIrq_EdgeBurst_CoreSurvivesAndReplResponds()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue("MicroPython must reach REPL");

        runner.ExecuteAndWait("from machine import Pin", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("n = 0", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("exec('def cb(p):\\n global n\\n n += 1')", ">>> ").Should().BeTrue();
        runner.ExecuteAndWait("Pin(15, Pin.IN).irq(cb, Pin.IRQ_RISING | Pin.IRQ_FALLING)", ">>> ")
              .Should().BeTrue();

        var pin = runner.Simulation.Gpio[15];
        for (var i = 0; i < 50; i++)
        {
            pin.ForceInput((i & 1) == 0);
            runner.Simulation.RunMilliseconds(0.2);
        }
        runner.Simulation.RunMilliseconds(20);

        runner.ExecuteAndWait("print('alive', n > 0)", "alive True", 10_000)
              .Should().BeTrue("the REPL must stay responsive after an IRQ burst");
    }
}

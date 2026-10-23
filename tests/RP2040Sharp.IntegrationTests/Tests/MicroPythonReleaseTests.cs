using System.Text.Json;
using RP2040.TestKit;
using RP2040Sharp.Wireless.Cyw43;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

[Trait("Category", "Release")]
public sealed class MicroPythonReleaseTests
{
    private static bool Required =>
        Environment.GetEnvironmentVariable("RELEASE_GATE_REQUIRED") == "true";

    private static bool ShouldSkip =>
        !Required && Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

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

    private const string Join =
        "import network, time\nw = network.WLAN(network.STA_IF)\nw.active(True)\nw.connect('RP2040Sharp-AP')\n" +
        "n = 0\nwhile w.status() != 3 and n < 100:\n    time.sleep_ms(50)\n    n += 1\nprint('IP', w.ifconfig()[0])\n";

    private const string PioSquare =
        "import rp2\nfrom machine import Pin\n" +
        "@rp2.asm_pio(set_init=rp2.PIO.OUT_LOW)\n" +
        "def sq():\n    set(pins, 1) [31]\n    set(pins, 0) [31]\n" +
        "sm = rp2.StateMachine(0, sq, freq=200_000, set_base=Pin(16))\nsm.active(1)\nprint('pio', 'on')\n";

    private const string Reinit =
        "import rp2, time\nfrom machine import Pin, SPI, UART, PWM\n" +
        "@rp2.asm_pio()\ndef p1():\n    set(x, 1)\n    mov(isr, x)\n    push()\n    wrap_target()\n    nop()\n    wrap()\n" +
        "@rp2.asm_pio()\ndef p2():\n    set(x, 2)\n    mov(isr, x)\n    push()\n    wrap_target()\n    nop()\n    wrap()\n" +
        "sm = rp2.StateMachine(0, p1); sm.active(1); time.sleep_ms(5); a = sm.get(); sm.active(0)\n" +
        "rp2.PIO(0).remove_program()\n" +
        "sm = rp2.StateMachine(0, p2); sm.active(1); time.sleep_ms(5); b = sm.get(); sm.active(0)\n" +
        "print('pio', a, b, sm.rx_fifo())\n" +
        "p = PWM(Pin(15), freq=1000, duty_u16=32768); p.deinit()\n" +
        "p = PWM(Pin(15), freq=2000, duty_u16=16384); print('pwm', p.freq(), p.duty_u16()); p.deinit()\n" +
        "u = UART(1, 9600, tx=Pin(8), rx=Pin(9)); u.write(b'A'); time.sleep_ms(5); u.deinit()\n" +
        "u = UART(1, 115200, tx=Pin(8), rx=Pin(9)); u.write(b'B'); time.sleep_ms(5)\n" +
        "s = SPI(0, baudrate=1_000_000); s.deinit(); s = SPI(0, baudrate=4_000_000); s2 = SPI(0, baudrate=2_000_000); s.write(b'xy')\n" +
        "src = bytearray(b'0123456789abcdef'); dst = bytearray(16); dst2 = bytearray(16)\n" +
        "d = rp2.DMA(); d.config(read=src, write=dst, count=4, ctrl=d.pack_ctrl(), trigger=True); time.sleep_ms(2); d.close()\n" +
        "d = rp2.DMA(); d.config(read=src, write=dst2, count=4, ctrl=d.pack_ctrl(), trigger=True); time.sleep_ms(2); d.close()\n" +
        "print('dma', dst == src, dst2 == src)\n" +
        "print('reinit', 'done')\n";

    private static int PadEdges(MicroPythonRunner runner, int pin, double ms)
    {
        var io = runner.Simulation.Rp2040.IoBank0;
        var last = io.GetPadOutputLevel(pin);
        var edges = 0;
        for (var t = 0.0; t < ms; t += 0.05)
        {
            runner.Simulation.RunMilliseconds(0.05);
            var now = io.GetPadOutputLevel(pin);
            if (now != last) edges++;
            last = now;
        }
        return edges;
    }

    private static async Task<MicroPythonRunner?> BootAsync()
    {
        var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null)
        {
            Required.Should().BeFalse($"MicroPython {Version} firmware must be available for the release gate");
            return null;
        }
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

    [Fact]
    public async Task MachineReset_ReleasesPins()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.ExecuteAndWait(
            "from machine import Pin; a = Pin(15, Pin.OUT, value=1); b = Pin(14, Pin.IN, Pin.PULL_UP); " +
            "c = Pin(13, Pin.IN); c.irq(lambda p: None, Pin.IRQ_RISING); print('set', a.value(), b.value())", "set 1 1")
            .Should().BeTrue();
        RebootAndWaitForRepl(runner, "import machine; machine.reset()");

        runner.Simulation.Rp2040.IoBank0.GetPadOutputEnable(15).Should().BeFalse("machine.reset() releases an output pin");
        runner.ExecuteAndWait(
            "import machine; m = machine.mem32; print('gpio', hex(m[0x4001407c]), machine.Pin(15).value(), " +
            "machine.Pin(14).value(), hex(m[0x4001c03c]), hex(m[0x40014104]))", "gpio 0x1f 0 0 0x56 0x0")
            .Should().BeTrue($"IO_BANK0, the pads and the GPIO interrupt enables must come back at their reset values, got: {runner.UsbCdc.Text}");
    }

    [Fact]
    public async Task MachineReset_StopsPio()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.Execute($"exec({JsonSerializer.Serialize(PioSquare)})");
        runner.WaitForOutput("pio on").Should().BeTrue($"the state machine must start, got: {runner.UsbCdc.Text}");
        PadEdges(runner, 16, 2).Should().BeGreaterThan(0, "the state machine toggles GPIO16");
        RebootAndWaitForRepl(runner, "import machine; machine.reset()");

        PadEdges(runner, 16, 2).Should().Be(0, "machine.reset() stops every PIO state machine");
        runner.ExecuteAndWait("import machine; print('pio', hex(machine.mem32[0x50200000]))", "pio 0x0")
            .Should().BeTrue($"PIO0 CTRL must come back at its reset value, got: {runner.UsbCdc.Text}");
    }

    [Fact]
    public async Task HostReset_WithInterruptsDisabled_RebootsToRepl()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.Execute("import machine; machine.disable_irq()");
        runner.Simulation.RunMilliseconds(50);
        runner.Simulation.Reset();
        runner.UsbCdc.Clear();
        runner.WaitForPrompt(timeoutMs: 15_000)
            .Should().BeTrue("a core leaves reset with PRIMASK clear, so the restarted firmware gets its interrupts back");
        runner.ExecuteAndWait("print('alive', 6 * 7)", "alive 42").Should().BeTrue();
    }

    [Fact]
    public async Task Thread_StartsAgain_AfterMachineReset()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.ExecuteAndWait("import _thread; _thread.start_new_thread(lambda: print('th', 1), ())", "th 1")
            .Should().BeTrue("_thread must run on core 1");
        RebootAndWaitForRepl(runner, "import machine; machine.reset()");
        for (var i = 2; i <= 3; i++)
            runner.ExecuteAndWait($"import _thread; _thread.start_new_thread(lambda: print('th', {i}), ())", $"th {i}")
                .Should().BeTrue($"_thread #{i} must run on core 1 after machine.reset()");
    }

    [Fact]
    public async Task Peripherals_ReinitWithoutReset()
    {
        if (ShouldSkip) return;

        await using var runner = await BootAsync();
        if (runner is null) return;

        runner.Execute($"exec({JsonSerializer.Serialize(Reinit)})");
        runner.WaitForOutput(t => t.Contains("reinit done") || t.Contains("Traceback"), timeoutMs: 10_000);
        var text = runner.UsbCdc.Text;
        text.Should().NotContain("Traceback");
        text.Should().Contain("pio 1 2 0", "a state machine re-created with a new program runs it, with an empty RX FIFO");
        text.Should().Contain("pwm 2000 16384", "a PWM re-created after deinit takes its new settings");
        text.Should().Contain("dma True True", "a DMA channel re-claimed after close copies again");
        runner.Simulation.Uart1.Text.Should().Contain("A").And.Contain("B", "a UART re-created after deinit transmits");
    }

    [Fact]
    public async Task PicoW_WifiConnectsAgain_AfterMachineResetTwice()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version, picoW: true);
        if (runner is null)
        {
            Required.Should().BeFalse($"MicroPython {Version} Pico W firmware must be available for the release gate");
            return;
        }
        var rp = runner.Simulation.Rp2040;
        rp.Pio0.ReadGpioIn = () => rp.IoBank0.GetInputWord();
        rp.Pio1.ReadGpioIn = () => rp.IoBank0.GetInputWord();
        rp.Sio.OnGpioChanged += mask => rp.IoBank0.NotifyPads(mask);
        var dev = new Cyw43439Device(rp.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp("RP2040Sharp-AP", [0x02, 0x00, 0x5E, 0x00, 0x04, 0x01], 6, -50, false));
        _ = new VirtualNet(dev.Sdpcm);
        runner.WaitForPrompt(timeoutMs: 15_000).Should().BeTrue("MicroPython must reach the REPL");

        for (var i = 0; i <= 2; i++)
        {
            if (i > 0) RebootAndWaitForRepl(runner, "import machine; machine.reset()");
            runner.Execute($"exec({JsonSerializer.Serialize(Join)})");
            for (long n = 0; n < 400_000_000 && !runner.UsbCdc.Text.Contains("IP 192.168.4.2") && !runner.UsbCdc.Text.Contains("Traceback"); n++)
                rp.Run(rp.Core0Waiting ? 1600 : 512);
            runner.UsbCdc.Text.Should().Contain("IP 192.168.4.2", $"connect #{i} must take a DHCP lease");
        }
    }
}

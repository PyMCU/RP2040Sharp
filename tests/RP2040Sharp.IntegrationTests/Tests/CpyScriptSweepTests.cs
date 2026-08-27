using RP2040Sharp.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RP2040Sharp.IntegrationTests.Tests;

[Trait("Category", "Integration")]
public sealed class CpyScriptSweepTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, string> Scripts() => new()
    {
        { "arithmetic",  "print('R', 2+3*4, 7//2, 2**10)",                       "R 14 3 1024" },
        { "strings",     "s='CircuitPython'; print('R', s[:6], len(s))",         "R Circui 13" },
        { "listcomp",    "print('R', [x*x for x in range(5)])",                  "R [0, 1, 4, 9, 16]" },
        { "dict",        "d={'a':1,'b':2}; print('R', sorted(d), d['b'])",       "R ['a', 'b'] 2" },
        { "float",       "print('R', round(3.14159*2, 3))",                      "R 6.283" },
        { "exception",   "try:\n    1/0\nexcept ZeroDivisionError:\n    print('R caught')", "R caught" },
        { "sys",         "import sys; print('R', sys.implementation.name)",      "R circuitpython" },
        { "struct",      "import struct; print('R', list(struct.pack('<HH',258,772)))", "R [2, 1, 4, 3]" },
        { "time",        "import time; print('R', time.monotonic() >= 0)",       "R True" },
        { "os",          "import os; print('R', hasattr(os,'listdir'))",         "R True" },
        { "digitalio",   "import board, digitalio\np=digitalio.DigitalInOut(board.LED)\np.direction=digitalio.Direction.OUTPUT\np.value=True\nprint('R', p.value)", "R True" },
        { "mcu_freq",    "import microcontroller; print('R', microcontroller.cpu.frequency)", "R 125000000" },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Script_runs_on_CircuitPython(string name, string code, string expect)
    {
        var runner = await CircuitPythonRunner.CreateAsync("9.2.1");
        if (runner is null) return;   // firmware unavailable (offline)
        await using var _ = runner;

        Assert.True(runner!.WaitForPrompt(40_000), $"[{name}] the REPL was never reached");

        // Execute() clears the probe buffer before injecting, so send every line but the last, then
        // let the final line's Execute be the one WaitForOutput measures against.
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            runner.Execute(lines[i]);
            runner.Simulation.RunMilliseconds(200);
        }
        runner.Execute(lines[^1]);
        var ok = runner.WaitForOutput(expect, 20_000);

        var tail = runner.UsbCdc.Text.Length > 0 ? runner.UsbCdc.Text : runner.Uart.Text;
        output.WriteLine($"[{name}] expected \"{expect}\" -> {(ok ? "OK" : "NOT FOUND")}");
        if (!ok) output.WriteLine("REPL tail: " + tail[^Math.Min(400, tail.Length)..].Replace("\r", " ").Replace("\n", " "));
        Assert.True(ok, $"[{name}] expected \"{expect}\"");
    }
}

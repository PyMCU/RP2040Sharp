using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// End-to-end proof that the native stand-in for the bootrom float library works for real firmware.
/// The shipped ROM images have mufplib stripped (it is not redistributable — see NOTICE.txt), and
/// pico-sdk builds float against the ROM by default (pico_float_default -> pico_float_pico), so
/// MicroPython resolves the 'SF'/'SD' tables and calls straight into BootromFloat's hooks. If those
/// were missing or wrong, this arithmetic would trap or return garbage.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MicroPythonFloatTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "v1.21.0";

    [Theory]
    [InlineData("print(2.5 * 4.0)", "10.0")]
    [InlineData("print(7.0 / 2.0)", "3.5")]
    [InlineData("print(1.5 + 2.25)", "3.75")]
    [InlineData("print(10.0 - 0.5)", "9.5")]
    public async Task Repl_evaluates_float_arithmetic(string code, string expected)
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();
        runner.ExecuteAndWait(code, expected)
            .Should().BeTrue($"'{code}' should print {expected} through the ROM float table");
    }

    [Fact]
    public async Task Repl_evaluates_math_module_functions()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        runner.ExecuteAndWait("import math; print(math.sqrt(16.0))", "4.0")
            .Should().BeTrue("sqrt routes to the ROM fsqrt entry");
        runner.ExecuteAndWait("print(math.floor(-2.5))", "-3")
            .Should().BeTrue("floor exercises the round-towards--infinity conversion");
        runner.ExecuteAndWait("print(round(math.exp(1.0), 3))", "2.718")
            .Should().BeTrue("exp routes to the ROM fexp entry");
    }

    /// <summary>
    /// sin and cos together: pico-sdk's sincosf calls the ROM's fsin and reads the sine from r0 and
    /// the cosine from r1, so this catches a stand-in that only fills in the sine.
    /// </summary>
    [Fact]
    public async Task Repl_evaluates_trigonometry()
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(Version);
        if (runner is null) return;

        runner.WaitForPrompt().Should().BeTrue();

        runner.ExecuteAndWait("import math; print(round(math.sin(0.0), 3))", "0.0")
            .Should().BeTrue();
        runner.ExecuteAndWait("print(round(math.cos(0.0), 3))", "1.0")
            .Should().BeTrue();
        runner.ExecuteAndWait("print(round(math.sin(math.pi / 2), 3))", "1.0")
            .Should().BeTrue();
    }
}

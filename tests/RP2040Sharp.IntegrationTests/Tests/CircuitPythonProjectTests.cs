using System.Text;
using FluentAssertions;
using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

[Trait("Category", "Integration")]
public sealed class CircuitPythonProjectTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    private const string Version = "9.2.1";

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task BakedProject_ImportsLongNamedRootModuleAndNestedPackage()
    {
        if (ShouldSkip) return;

        var files = new List<(string Name, byte[] Data)>
        {
            ("code.py", B("import adafruit_pioasm_like\nfrom mypkg.sub import deep_module\nprint('ICIRCUIT', adafruit_pioasm_like.Z, deep_module.W)\n")),
            ("adafruit_pioasm_like.py", B("Z = 'pio'\n")),
            ("lib/mypkg/__init__.py", B("")),
            ("lib/mypkg/sub/__init__.py", B("")),
            ("lib/mypkg/sub/deep_module.py", B("W = 42\n")),
        };

        await using var runner = await CircuitPythonRunner.CreateWithFilesAsync(Version, files);
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000).Should().BeTrue("CircuitPython must boot");
        runner.SoftReset(timeoutMs: 40_000).Should().BeTrue("CircuitPython must finish code.py and return to the REPL");

        var text = runner.UsbCdc.IsConnected ? runner.UsbCdc.Text : runner.Uart.Text;
        text.Should().Contain("ICIRCUIT pio 42");
    }
}

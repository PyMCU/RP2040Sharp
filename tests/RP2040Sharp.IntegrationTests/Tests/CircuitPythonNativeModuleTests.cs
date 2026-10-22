using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Boots real CircuitPython firmware and imports a native <c>.mpy</c> module — a "natmod", whose
/// top-level code is relocatable ARM machine code rather than bytecode.
///
/// Importing one exercises a path nothing else in the suite reaches: the loader copies the module's
/// text into RAM, patches its GOT and qstr table through <c>mp_native_relocate</c>, then calls the
/// module's <c>mpy_init</c> from RAM. That entry is position independent code, and it locates its own
/// GOT with <c>add rN, pc</c> — so a core that reads R15 as anything other than the instruction
/// address plus 4 hands it a GOT two bytes low, and the module registers nothing while the import
/// still reports success (PyMCU/RP2040Sharp#68).
///
/// The module is MicroPython's <c>examples/natmod/features0</c> built with CircuitPython 10.3.1's own
/// <c>py/dynruntime.mk</c> and <c>tools/mpy_ld.py</c> for <c>ARCH=armv6m</c>. The same bytes run on a
/// real Pico, so anything else here is an emulator defect.
///
/// Set environment variable SKIP_INTEGRATION_TESTS=1 to skip in CI pipelines without internet access.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CircuitPythonNativeModuleTests
{
    /// <summary>The first CircuitPython release whose rp2040 build compiles in the native loader.</summary>
    private const string Version = "10.3.1";

    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    [Fact]
    public async Task NativeMpyModule_BindsItsFunctions_AndRunsThem()
    {
        if (ShouldSkip) return;

        var mpy = LoadEmbeddedMpy("features0-armv6m.mpy");

        await using var runner = await CircuitPythonRunner.CreateWithFilesAsync(
            Version, new[] { ("feat0.mpy", mpy) });
        if (runner is null) return;

        runner.WaitForPrompt(timeoutMs: 20_000)
            .Should().BeTrue($"CircuitPython {Version} must reach the REPL");

        runner.ExecuteAndWait("import feat0; print(dir(feat0))", "factorial", timeoutMs: 30_000)
            .Should().BeTrue("mpy_init must bind 'factorial' into the module namespace — an import " +
                             "that raises nothing but leaves the module empty is the #68 symptom");

        runner.ExecuteAndWait("print(feat0.factorial(10))", "3628800", timeoutMs: 30_000)
            .Should().BeTrue("the relocated native code must run and compute 10! = 3628800");
    }

    /// <summary>
    /// Loads a <c>.mpy</c> embedded from Firmware/natmod/, matched by a suffix of its filename so the
    /// test does not depend on MSBuild's manifest-resource name mangling.
    /// </summary>
    private static byte[] LoadEmbeddedMpy(string fileName)
    {
        var asm = typeof(CircuitPythonNativeModuleTests).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        name.Should().NotBeNull($"'{fileName}' must be embedded in the test assembly");

        using var stream = asm.GetManifestResourceStream(name!)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

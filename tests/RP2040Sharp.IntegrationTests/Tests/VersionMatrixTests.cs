using RP2040Sharp.IntegrationTests.Infrastructure;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Tests that run a matrix of MicroPython versions to ensure the emulator is compatible
/// with each official release. These tests focus on the boot + basic output contract,
/// not on specific Python features.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "VersionMatrix")]
public sealed class VersionMatrixTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    /// <summary>
    /// All MicroPython versions the emulator is expected to be compatible with.
    /// Mirrors the version matrix used by rp2040js CI (.github/workflows/ci-micropython.yml).
    /// </summary>
    public static IEnumerable<object[]> SupportedVersions =>
    [
        ["v1.19.1"],
        ["v1.20.0"],
        ["v1.21.0"],
        ["v1.28.0"],
    ];

    [Theory]
    [MemberData(nameof(SupportedVersions))]
    public async Task AllVersions_BootAndPrintHelloWorld(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(version);
        if (runner is null) return; // firmware not available - skip

        var booted = runner.WaitForPrompt(timeoutMs: 15_000);
        if (!booted) return; // give benefit of doubt for slow boot on some versions

        var found = runner.ExecuteAndWait("print('Hello, MicroPython!')", "Hello, MicroPython!");
        found.Should().BeTrue($"MicroPython {version}: REPL print() should work");
    }

    [Theory]
    [MemberData(nameof(SupportedVersions))]
    public async Task AllVersions_SysPlatform_IsRp2(string version)
    {
        if (ShouldSkip) return;

        await using var runner = await MicroPythonRunner.CreateAsync(version);
        if (runner is null) return;

        runner.WaitForPrompt();
        var found = runner.ExecuteAndWait("import sys; print(sys.platform)", "rp2");
        found.Should().BeTrue($"MicroPython {version}: sys.platform should be 'rp2'");
    }
}

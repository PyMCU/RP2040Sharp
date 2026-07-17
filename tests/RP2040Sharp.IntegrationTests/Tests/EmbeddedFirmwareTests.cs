using System.Reflection;

namespace RP2040Sharp.IntegrationTests.Tests;

/// <summary>
/// Guards that the MicroPython and CircuitPython UF2 images stay embedded in the test assembly.
/// They are bundled (under Firmware/python/) so the integration tests run fully offline — without
/// these resources, <see cref="Infrastructure.FirmwareCache"/> would silently fall back to network
/// downloads and reintroduce flakiness.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmbeddedFirmwareTests
{
    [Theory]
    [InlineData("micropython-v1.19.1")]
    [InlineData("micropython-v1.20.0")]
    [InlineData("micropython-v1.21.0")]
    [InlineData("circuitpython-9.2.1")]
    public void Firmware_image_is_embedded(string fileNameContains)
    {
        var asm = Assembly.GetExecutingAssembly();
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase)
                                 && n.Contains(fileNameContains, StringComparison.OrdinalIgnoreCase));

        match.Should().NotBeNull($"'{fileNameContains}.uf2' must remain embedded for offline tests");

        using var stream = asm.GetManifestResourceStream(match!);
        stream.Should().NotBeNull();
        stream!.Length.Should().BeGreaterThan(100_000, "a real Python firmware UF2 is hundreds of KB");
    }
}

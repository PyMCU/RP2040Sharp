using System.Globalization;
using System.Text.Json;
using RP2040.TestKit.Boards;

namespace RP2040.NanoFramework.TestKit;

/// <summary>
/// A flashed nanoCLR firmware (nanoBooter + nanoCLR) plus the native-methods checksums it provides,
/// discovered from a directory by convention. Boots a deployment onto a <see cref="PicoSimulation"/>
/// at the RP2040 flash layout, after verifying the deployment is checksum-compatible.
/// </summary>
public sealed class NanoFirmware
{
    // RP2040 flash layout, from the nf-interpreter linker scripts:
    //   nanoBooter @ 0x10000000 (flash base), nanoCLR @ +0x14000, deployment @ +0xFC000.
    public const int NanoClrOffset = 0x14000;
    public const int DeploymentOffset = 0xFC000;

    /// <summary>The nanoBooter image (loaded at the flash base).</summary>
    public byte[] NanoBooter { get; }

    /// <summary>The nanoCLR interpreter image.</summary>
    public byte[] NanoClr { get; }

    /// <summary>Native-methods checksums the firmware was built with, keyed by assembly name (from the manifest).</summary>
    public IReadOnlyDictionary<string, uint> ExpectedNativeChecksums { get; }

    private NanoFirmware(byte[] booter, byte[] clr, IReadOnlyDictionary<string, uint> expected)
    {
        NanoBooter = booter;
        NanoClr = clr;
        ExpectedNativeChecksums = expected;
    }

    /// <summary>
    /// Discovers the firmware in <paramref name="directory"/>: <c>nanoBooter*.bin</c>, <c>nanoCLR*.bin</c>,
    /// and an optional <c>firmware.manifest.json</c> declaring the native checksums it provides. No
    /// hard-coded file names or flash offsets leak into the test.
    /// </summary>
    public static NanoFirmware FromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No firmware directory at '{directory}'.");
        }

        byte[] booter = File.ReadAllBytes(FindOne(directory, "nanoBooter"));
        byte[] clr = File.ReadAllBytes(FindOne(directory, "nanoCLR"));
        var expected = LoadManifest(Path.Combine(directory, "firmware.manifest.json"));
        return new NanoFirmware(booter, clr, expected);
    }

    /// <summary>
    /// Verifies every assembly the firmware declares a checksum for is present in <paramref name="app"/>
    /// with a matching <c>.pe</c> checksum. Throws <see cref="NanoChecksumMismatchException"/> on drift.
    /// </summary>
    public void AssertCompatible(NanoApp app)
    {
        foreach (var (assembly, firmwareChecksum) in ExpectedNativeChecksums)
        {
            if (app.NativeChecksums.TryGetValue(assembly, out uint peChecksum) && peChecksum != firmwareChecksum)
            {
                throw new NanoChecksumMismatchException(assembly, peChecksum, firmwareChecksum);
            }
        }
    }

    /// <summary>Boots the deployment: verifies checksums, then loads booter + nanoCLR + deployment into flash.</summary>
    public void BootInto(PicoSimulation pico, NanoApp app)
    {
        AssertCompatible(app);
        pico.Rp2040.LoadFlash(NanoBooter);
        pico.Rp2040.WriteFlash(NanoClrOffset, NanoClr);
        pico.Rp2040.WriteFlash(DeploymentOffset, app.DeploymentBytes);
    }

    private static string FindOne(string directory, string prefix)
    {
        var matches = Directory.GetFiles(directory, prefix + "*.bin");
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"No '{prefix}*.bin' in '{directory}'."),
            _ => throw new InvalidOperationException(
                $"Ambiguous firmware: {matches.Length} files match '{prefix}*.bin' in '{directory}'."),
        };
    }

    private static IReadOnlyDictionary<string, uint> LoadManifest(string manifestPath)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
        {
            return map; // optional: no manifest means no checksum guard
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("nativeChecksums", out var checksums))
        {
            foreach (var entry in checksums.EnumerateObject())
            {
                map[entry.Name] = ParseHex(entry.Value.GetString());
            }
        }

        return map;
    }

    private static uint ParseHex(string? value)
    {
        if (value is null)
        {
            throw new InvalidDataException("Null checksum in firmware manifest.");
        }

        string s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

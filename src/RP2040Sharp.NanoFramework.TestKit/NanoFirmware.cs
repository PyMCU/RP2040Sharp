using System.Globalization;
using System.Linq;
using System.Text.Json;
using NanoFramework.Clr;
using RP2040.TestKit.Boards;

namespace RP2040Sharp.NanoFramework.TestKit;

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

    /// <summary>Selected native symbol addresses (name -&gt; address) from the manifest, for run-to-function.</summary>
    public IReadOnlyDictionary<string, uint> Symbols { get; }

    private NanoFirmware(
        byte[] booter,
        byte[] clr,
        IReadOnlyDictionary<string, uint> expected,
        IReadOnlyDictionary<string, uint> symbols)
    {
        NanoBooter = booter;
        NanoClr = clr;
        ExpectedNativeChecksums = expected;
        Symbols = symbols;
    }

    /// <summary>Resolves a native symbol address by name (Thumb bit cleared). Throws if not in the manifest.</summary>
    public uint ResolveSymbol(string name)
    {
        if (!Symbols.TryGetValue(name, out uint address))
        {
            throw new KeyNotFoundException(
                $"Symbol '{name}' is not in the firmware manifest. Known: {string.Join(", ", Symbols.Keys)}.");
        }

        return address & ~1u;
    }

    /// <summary>The manifest symbol whose address most closely precedes <paramref name="pc"/> ("func+0xNN"),
    /// or "0x........" if none. Curated symbols only — good for the points you run to, not full coverage.</summary>
    public string SymbolizePc(uint pc)
    {
        pc &= ~1u;
        string? best = null;
        uint bestAddr = 0;
        foreach (var (name, addr) in Symbols)
        {
            uint a = addr & ~1u;
            if (a <= pc && a >= bestAddr)
            {
                best = name;
                bestAddr = a;
            }
        }

        if (best is null)
        {
            return $"0x{pc:X8}";
        }

        uint offset = pc - bestAddr;
        return offset == 0 ? best : $"{best}+0x{offset:X}";
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

        // Prefer deriving everything from the nanoCLR ELF; fall back to a manifest when only the .bin ships.
        string? elf = Directory.GetFiles(directory, "nanoCLR*.elf").FirstOrDefault();
        var (checksums, symbols) = elf is not null
            ? FromElf(elf)
            : LoadManifest(Path.Combine(directory, "firmware.manifest.json"));

        return new NanoFirmware(booter, clr, checksums, symbols);
    }

    // Symbols the test kit resolves by name; layout offsets and native checksums derive automatically.
    private static readonly (string Name, string Pattern)[] WantedSymbols =
    {
        ("g_CLR_RT_TypeSystem", @"\bg_CLR_RT_TypeSystem$"),
        ("g_CLR_RT_ExecutionEngine", @"\bg_CLR_RT_ExecutionEngine$"),
        ("g_CLR_RT_GarbageCollector", @"\bg_CLR_RT_GarbageCollector$"),
        ("Execute_IL", @"CLR_RT_Thread\d+Execute_IL"),
        ("PioBlock.NativeAddProgram", @"Library_.*PioBlock\d+NativeAddProgram.*STATIC"),
        ("c_CLR_StringTable_Data", @"c_CLR_StringTable_Data$"),
        ("c_CLR_StringTable_Lookup", @"c_CLR_StringTable_Lookup$"),
    };

    private static (IReadOnlyDictionary<string, uint> Checksums, IReadOnlyDictionary<string, uint> Symbols)
        FromElf(string elfPath)
    {
        FirmwareDescriptor d = FirmwareElf.Read(File.ReadAllBytes(elfPath), WantedSymbols);

        var symbols = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in d.Symbols)
        {
            symbols[k] = v;
        }

        foreach (var (k, v) in d.Layout)
        {
            symbols[k] = v;
        }

        var checksums = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in d.NativeChecksums)
        {
            checksums[k] = v;
        }

        return (checksums, symbols);
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

    private static (IReadOnlyDictionary<string, uint> Checksums, IReadOnlyDictionary<string, uint> Symbols)
        LoadManifest(string manifestPath)
    {
        var checksums = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var symbols = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
        {
            return (checksums, symbols); // optional: no manifest means no guard / no symbols
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        ReadHexMap(doc.RootElement, "nativeChecksums", checksums);
        ReadHexMap(doc.RootElement, "symbols", symbols);
        ReadHexMap(doc.RootElement, "layout", symbols); // struct field offsets, resolved by name like symbols
        return (checksums, symbols);
    }

    private static void ReadHexMap(JsonElement root, string property, Dictionary<string, uint> into)
    {
        if (root.TryGetProperty(property, out var obj))
        {
            foreach (var entry in obj.EnumerateObject())
            {
                into[entry.Name] = ParseHex(entry.Value.GetString());
            }
        }
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

using System.Buffers.Binary;

namespace RP2040Sharp.NanoFramework.TestKit;

/// <summary>
/// A nanoFramework deployment image assembled from build output. The Metadata Processor emits one
/// <c>.pe</c> per assembly (mscorlib, each referenced library, and the app); a deployment is the
/// 4-byte-aligned concatenation of those <c>.pe</c> blobs in load order — mscorlib first, the app
/// last. No recompilation: the <c>.pe</c> bytes are reused as-is (the CLR matches native checksums).
/// </summary>
public sealed class NanoApp
{
    /// <summary>The assembled deployment blob to write to flash at the deployment offset.</summary>
    public byte[] DeploymentBytes { get; }

    /// <summary>Each included assembly's declared native-methods checksum, keyed by assembly name.</summary>
    public IReadOnlyDictionary<string, uint> NativeChecksums { get; }

    /// <summary>The assemblies included, in deployment (load) order.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    private NanoApp(byte[] deployment, IReadOnlyList<string> assemblies, IReadOnlyDictionary<string, uint> checksums)
    {
        DeploymentBytes = deployment;
        Assemblies = assemblies;
        NativeChecksums = checksums;
    }

    /// <summary>
    /// Assembles the deployment from every <c>.pe</c> in <paramref name="peDirectory"/>, ordered by
    /// convention: <c>mscorlib</c> first, <paramref name="appAssemblyName"/> last, other libraries
    /// (sorted) in between. Each <c>.pe</c>'s native checksum is read for the compatibility guard.
    /// </summary>
    public static NanoApp FromPeDirectory(string peDirectory, string appAssemblyName)
    {
        if (!Directory.Exists(peDirectory))
        {
            throw new DirectoryNotFoundException($"No .pe directory at '{peDirectory}'.");
        }

        var names = Directory.GetFiles(peDirectory, "*.pe")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        if (names.Count == 0)
        {
            throw new FileNotFoundException($"No .pe assemblies found in '{peDirectory}'.");
        }

        // Load order: mscorlib first, the app last, the rest (sorted) in between.
        var ordered = names
            .OrderBy(n => n.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ? 0
                        : n.Equals(appAssemblyName, StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FromPeFiles(peDirectory, ordered);
    }

    /// <summary>Assembles the deployment from the named assemblies, in the exact order given.</summary>
    public static NanoApp FromPeFiles(string peDirectory, IReadOnlyList<string> assemblyOrder)
    {
        var checksums = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        using var ms = new MemoryStream();

        foreach (var name in assemblyOrder)
        {
            string path = Path.Combine(peDirectory, name + ".pe");
            byte[] pe = File.ReadAllBytes(path);
            checksums[name] = ReadNativeChecksum(pe, name);

            // 4-byte align each assembly within the deployment image.
            while (ms.Length % 4 != 0)
            {
                ms.WriteByte(0);
            }

            ms.Write(pe, 0, pe.Length);
        }

        return new NanoApp(ms.ToArray(), assemblyOrder, checksums);
    }

    /// <summary>
    /// Wraps an already-assembled deployment image (e.g. a prebuilt <c>deployment.bin</c>) as-is. No
    /// checksum guard is applied unless <paramref name="nativeChecksums"/> is supplied — the caller
    /// vouches that the deployment matches the firmware.
    /// </summary>
    public static NanoApp FromDeployment(byte[] deployment, IReadOnlyDictionary<string, uint>? nativeChecksums = null)
        => new(deployment, Array.Empty<string>(), nativeChecksums ?? new Dictionary<string, uint>());

    // nanoFramework PE header: marker "NFMRK1" at 0, nativeMethodsChecksum (UINT32 LE) at offset 20.
    private const int NativeChecksumOffset = 20;
    private static readonly byte[] Marker = "NFMRK1"u8.ToArray();

    private static uint ReadNativeChecksum(byte[] pe, string name)
    {
        if (pe.Length < NativeChecksumOffset + 4 || !pe.AsSpan(0, Marker.Length).SequenceEqual(Marker))
        {
            throw new InvalidDataException($"'{name}.pe' is not a nanoFramework assembly (missing NFMRK1 header).");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(NativeChecksumOffset, 4));
    }
}

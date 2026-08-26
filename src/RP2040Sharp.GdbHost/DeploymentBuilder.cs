// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
using System.Buffers.Binary;

namespace RP2040Sharp.GdbHost;

/// <summary>
/// Builds a nanoFramework deployment image from compiled <c>.pe</c> assemblies.
///
/// The on-flash format is plain concatenation: each assembly starts with a 124-byte
/// <c>CLR_RECORD_ASSEMBLY</c> header, occupies <c>TotalSize()</c> bytes, and the next one
/// begins at the following 4-byte boundary. There is no index and no terminator — the CLR
/// walks forward until a header fails validation or the region ends, which is why the tail
/// must be left as erased flash (<c>0xFF</c>).
///
/// Layout and validation mirror nf-interpreter: <c>src/CLR/Include/nanoCLR_Types.h</c>
/// (struct definition) and <c>src/CLR/Core/TypeSystem.cpp</c> (GoodHeader/GoodAssembly).
/// </summary>
internal static class DeploymentBuilder
{
    public const uint DeploymentAddress = 0x100FC000;   // RP2040 sector 252, per Device_BlockStorage.c

    private const int HeaderSize = 124;
    private const int OffsetHeaderCrc = 8;
    private const int OffsetAssemblyCrc = 12;
    private const int OffsetStringTableVersion = 38;
    private const int OffsetTotalSize = 100;            // startOfTables[TBL_EndOfAssembly], TBL_EndOfAssembly = 15
    private const int StringTableVersion = 1;

    // "NFMRK1" plus its NUL — ValidateMarker compares sizeof(), so 7 of the 8 marker bytes.
    private static ReadOnlySpan<byte> Marker => "NFMRK1\0"u8;

    public sealed record Assembly(string Path, string Name, byte[] Bytes, uint TotalSize);

    /// <summary>
    /// Reads and validates one <c>.pe</c>. Throws <see cref="InvalidDataException"/> with a
    /// specific reason rather than letting a malformed assembly fail silently at boot, where
    /// the only symptom would be a CLR that quietly finds no application.
    /// </summary>
    public static Assembly Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);

        if (bytes.Length < HeaderSize)
            throw new InvalidDataException($"{name}: {bytes.Length} bytes is shorter than a {HeaderSize}-byte assembly header.");

        if (!bytes.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            throw new InvalidDataException($"{name}: missing the NFMRK1 marker — is this really a .pe assembly?");

        var stringTableVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(OffsetStringTableVersion));
        if (stringTableVersion != StringTableVersion)
            throw new InvalidDataException($"{name}: string table version {stringTableVersion}, expected {StringTableVersion}.");

        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(OffsetTotalSize));
        if (totalSize < HeaderSize || totalSize > bytes.Length)
            throw new InvalidDataException($"{name}: header declares {totalSize} bytes but the file holds {bytes.Length}.");

        // Header CRC is computed over the header with the CRC field itself zeroed.
        var header = bytes.AsSpan(0, HeaderSize).ToArray();
        var declaredHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetHeaderCrc));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(OffsetHeaderCrc), 0);
        var actualHeaderCrc = ComputeCrc(header, 0);
        if (actualHeaderCrc != declaredHeaderCrc)
            throw new InvalidDataException($"{name}: header CRC is 0x{actualHeaderCrc:X8}, header claims 0x{declaredHeaderCrc:X8}.");

        var declaredAssemblyCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(OffsetAssemblyCrc));
        var actualAssemblyCrc = ComputeCrc(bytes.AsSpan(HeaderSize, (int)totalSize - HeaderSize), 0);
        if (actualAssemblyCrc != declaredAssemblyCrc)
            throw new InvalidDataException($"{name}: body CRC is 0x{actualAssemblyCrc:X8}, header claims 0x{declaredAssemblyCrc:X8}.");

        return new Assembly(path, name, bytes, totalSize);
    }

    /// <summary>
    /// Concatenates assemblies, padding each to a 4-byte boundary with zeros — the alignment
    /// <c>ROUNDTOMULTIPLE</c> applies in CLRStartup.cpp when it steps to the next header.
    /// </summary>
    public static byte[] Build(IReadOnlyList<Assembly> assemblies)
    {
        using var image = new MemoryStream();
        foreach (var a in assemblies)
        {
            image.Write(a.Bytes, 0, (int)a.TotalSize);
            for (var pad = (4 - (int)(a.TotalSize % 4)) % 4; pad > 0; pad--)
                image.WriteByte(0);
        }
        return image.ToArray();
    }

    /// <summary>
    /// Orders assemblies for deployment: mscorlib first, since everything references it, then
    /// the rest alphabetically for a stable, reproducible image.
    /// </summary>
    public static IEnumerable<string> Order(IEnumerable<string> paths) =>
        paths.OrderBy(p => Path.GetFileNameWithoutExtension(p)
                  .Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
             .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CRC-32/MPEG-2 as implemented by <c>SUPPORT_ComputeCRC</c>: polynomial 0x04C11DB7,
    /// MSB-first, no input or output reflection, no final xor.
    /// </summary>
    public static uint ComputeCrc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data)
            crc = CrcTable[((crc >> 24) ^ b) & 0xFF] ^ (crc << 8);
        return crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var bit = 0; bit < 8; bit++)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return table;
    }
}

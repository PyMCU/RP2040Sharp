using System;
using System.Collections.Generic;
using System.Text;

namespace RP2040.Peripherals.Fs;

/// <summary>
/// Locates and reads the CircuitPython internal-flash filesystem (a FAT12 volume CircuitPython
/// formats on first boot when the reserved flash region is blank). This is general-knowledge
/// tooling for inspecting what the firmware staged — e.g. reading <c>boot_out.txt</c> (which
/// records the running version and any safe-mode crash reason) or <c>code.py</c> — and is the
/// foundation for pre-baking a filesystem image into flash before boot.
///
/// <para>Empirically (CircuitPython 10.x on the 2&#160;MB Pico/Pico&#160;2 flash) the volume sits at
/// flash offset <c>0x100000</c>, is <c>1&#160;MB</c>, FAT12, 512&#160;B/sector, 1&#160;sector/cluster,
/// 1 reserved sector, 1 FAT, 512 root entries, 7 sectors/FAT. Rather than hard-code that, the
/// region is discovered by scanning for a valid FAT boot sector, so it stays correct across builds
/// and flash sizes.</para>
/// </summary>
public static class CircuitPythonFs
{
    /// <summary>The located FAT volume and the BPB geometry parsed from its boot sector.</summary>
    public readonly record struct Region(
        int Offset, int SizeBytes,
        ushort BytesPerSector, byte SectorsPerCluster, ushort ReservedSectors,
        byte NumFats, ushort RootEntries, ushort SectorsPerFat);

    /// <summary>Flash offset of the reserved CircuitPython filesystem region on the standard 2&#160;MB
    /// Pico/Pico&#160;2 build (verified empirically against CircuitPython 10.x). Use <see cref="TryLocate"/>
    /// for builds/flash sizes that may differ.</summary>
    public const int DefaultRegionOffset = 0x100000;

    /// <summary>Size of that reserved region (1&#160;MB).</summary>
    public const int DefaultRegionSize = 0x100000;

    /// <summary>
    /// Scans a flash image for the CircuitPython FAT volume. A match is a 512-aligned sector with
    /// the 0x55AA signature and a plausible BPB (512 B/sector, 1–2 FATs, ≥1 reserved sector). The
    /// bare string "CIRCUITPY" also appears inside the firmware binary, so the boot-sector shape —
    /// not a string search — is what identifies the real volume.
    /// </summary>
    public static bool TryLocate(byte[] flash, out Region region)
    {
        region = default;
        for (int off = 0; off + 512 <= flash.Length; off += 512)
        {
            if (flash[off + 510] != 0x55 || flash[off + 511] != 0xAA) continue;
            ushort bps = U16(flash, off + 11);
            byte nFats = flash[off + 16];
            ushort reserved = U16(flash, off + 14);
            if (bps != 512 || (nFats != 1 && nFats != 2) || reserved < 1) continue;

            ushort rootEnts = U16(flash, off + 17);
            ushort secPerFat = U16(flash, off + 22);
            ushort totSec16 = U16(flash, off + 19);
            uint totSec = totSec16 != 0 ? totSec16 : U32(flash, off + 32);
            region = new Region(off, (int)(totSec * bps), bps, flash[off + 13], reserved,
                                 nFats, rootEnts, secPerFat);
            return true;
        }
        return false;
    }

    /// <summary>Lists the 8.3 names of the files/directories in the volume's root directory.</summary>
    public static IReadOnlyList<string> ListRootEntries(byte[] flash)
    {
        var names = new List<string>();
        if (!TryLocate(flash, out var r)) return names;
        int rootStart = r.Offset + (r.ReservedSectors + r.NumFats * r.SectorsPerFat) * r.BytesPerSector;
        for (int e = 0; e < r.RootEntries; e++)
        {
            int p = rootStart + e * 32;
            if (p + 32 > flash.Length || flash[p] == 0x00) break;
            if (flash[p] == 0xE5) continue;          // deleted
            byte attr = flash[p + 11];
            if (attr == 0x0F || (attr & 0x08) != 0) continue; // LFN / volume label
            names.Add(ShortName(flash, p));
        }
        return names;
    }

    /// <summary>
    /// Reads a root-level file by its name (8.3, case-insensitive — e.g. "boot_out.txt" or
    /// "code.py"), following the FAT12 cluster chain. Returns null if the volume or file is absent.
    /// </summary>
    public static byte[]? ReadFile(byte[] flash, string name)
    {
        if (!TryLocate(flash, out var r)) return null;
        string want = To83(name);
        int rootStart = r.Offset + (r.ReservedSectors + r.NumFats * r.SectorsPerFat) * r.BytesPerSector;
        int rootSectors = (r.RootEntries * 32 + r.BytesPerSector - 1) / r.BytesPerSector;
        int dataStart = rootStart + rootSectors * r.BytesPerSector;

        for (int e = 0; e < r.RootEntries; e++)
        {
            int p = rootStart + e * 32;
            if (p + 32 > flash.Length || flash[p] == 0x00) break;
            if (flash[p] == 0xE5) continue;
            byte attr = flash[p + 11];
            if (attr == 0x0F || (attr & 0x18) != 0) continue; // skip LFN / dir / volume label
            if (!string.Equals(RawName(flash, p), want, StringComparison.OrdinalIgnoreCase)) continue;

            uint size = U32(flash, p + 28);
            int cluster = U16(flash, p + 26);
            int clusterBytes = r.SectorsPerCluster * r.BytesPerSector;
            var outBuf = new byte[size];
            int written = 0;
            while (cluster >= 2 && cluster < 0xFF8 && written < size)
            {
                int src = dataStart + (cluster - 2) * clusterBytes;
                int n = (int)Math.Min(clusterBytes, size - written);
                if (src + n > flash.Length) break;
                Array.Copy(flash, src, outBuf, written, n);
                written += n;
                cluster = NextCluster12(flash, r, cluster);
            }
            return outBuf;
        }
        return null;
    }

    // ── Writing / baking ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fresh FAT12 volume of <paramref name="sizeBytes"/> containing
    /// <paramref name="files"/> (typically <c>code.py</c> + modules), using the exact geometry
    /// CircuitPython formats itself — so the firmware mounts it on boot and runs it instead of
    /// reformatting and dropping the files. The volume label is "CIRCUITPY".
    /// </summary>
    public static byte[] BuildVolume(int sizeBytes, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        const int bps = 512, spc = 1, reserved = 1, nFats = 1, rootEnts = 512, secPerFat = 7;
        int totSec = sizeBytes / bps;
        var img = new byte[sizeBytes];

        img[0] = 0xEB; img[1] = 0xFE; img[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(img, 3);
        img[11] = bps & 0xFF; img[12] = bps >> 8;
        img[13] = spc;
        img[14] = reserved;
        img[16] = nFats;
        img[17] = rootEnts & 0xFF; img[18] = rootEnts >> 8;
        img[19] = (byte)(totSec & 0xFF); img[20] = (byte)((totSec >> 8) & 0xFF);
        img[21] = 0xF8;
        img[22] = secPerFat & 0xFF; img[23] = secPerFat >> 8;
        img[24] = 0x3F; img[26] = 0xFF;
        img[36] = 0x80; img[38] = 0x29;
        img[39] = 0x12; img[40] = 0x34; img[41] = 0x56; img[42] = 0x78;
        WriteRaw11(img, 43, "CIRCUITPY  ");
        Encoding.ASCII.GetBytes("FAT12   ").CopyTo(img, 54);
        img[510] = 0x55; img[511] = 0xAA;

        int fatStart = reserved * bps;
        int rootStart = (reserved + nFats * secPerFat) * bps;
        int rootSectors = (rootEnts * 32 + bps - 1) / bps;
        int dataStart = rootStart + rootSectors * bps;
        img[fatStart] = 0xF8; img[fatStart + 1] = 0xFF; img[fatStart + 2] = 0xFF;

        void SetFat12(int cl, int val)
        {
            int idx = fatStart + cl + cl / 2;
            if ((cl & 1) == 0) { img[idx] = (byte)(val & 0xFF); img[idx + 1] = (byte)((img[idx + 1] & 0xF0) | ((val >> 8) & 0x0F)); }
            else { img[idx] = (byte)((img[idx] & 0x0F) | ((val << 4) & 0xF0)); img[idx + 1] = (byte)((val >> 4) & 0xFF); }
        }

        WriteRaw11(img, rootStart, "CIRCUITPY  ");
        img[rootStart + 11] = 0x08; // volume label
        int entry = 1, cluster = 2;
        int clusterBytes = spc * bps;
        foreach (var (name, data) in files)
        {
            int p = rootStart + entry * 32;
            WriteRaw11(img, p, To83(name));
            img[p + 11] = 0x20; // archive
            int first = cluster;
            int need = Math.Max(1, (data.Length + clusterBytes - 1) / clusterBytes);
            for (int c = 0; c < need; c++)
            {
                int cl = cluster + c;
                SetFat12(cl, c == need - 1 ? 0xFFF : cl + 1);
                int src = c * clusterBytes, n = Math.Min(clusterBytes, data.Length - src);
                if (n > 0) Array.Copy(data, src, img, dataStart + (cl - 2) * clusterBytes, n);
            }
            img[p + 26] = (byte)(first & 0xFF); img[p + 27] = (byte)(first >> 8);
            img[p + 28] = (byte)data.Length; img[p + 29] = (byte)(data.Length >> 8);
            img[p + 30] = (byte)(data.Length >> 16); img[p + 31] = (byte)(data.Length >> 24);
            cluster += need; entry++;
        }
        return img;
    }

    /// <summary>
    /// Splices a freshly-built CircuitPython volume (containing <paramref name="files"/>) into a
    /// firmware flash image at the reserved FS region, so the next boot mounts it and runs
    /// <c>code.py</c> without the firmware formatting first. Defaults to the standard 2&#160;MB layout.
    /// </summary>
    public static void Bake(byte[] flash, IReadOnlyList<(string Name, byte[] Data)> files,
                            int regionOffset = DefaultRegionOffset, int regionSize = DefaultRegionSize)
    {
        var vol = BuildVolume(regionSize, files);
        Array.Copy(vol, 0, flash, regionOffset, Math.Min(vol.Length, flash.Length - regionOffset));
    }

    static void WriteRaw11(byte[] img, int p, string raw11)
    {
        for (int k = 0; k < 11; k++) img[p + k] = (byte)(k < raw11.Length ? raw11[k] : ' ');
    }

    // ── FAT12 / BPB helpers ───────────────────────────────────────────────────
    static int NextCluster12(byte[] flash, Region r, int cluster)
    {
        int fatStart = r.Offset + r.ReservedSectors * r.BytesPerSector;
        int idx = fatStart + cluster + cluster / 2; // 12 bits per entry → c*3/2
        if (idx + 1 >= flash.Length) return 0xFFF;
        int val = flash[idx] | (flash[idx + 1] << 8);
        return (cluster & 1) != 0 ? val >> 4 : val & 0x0FFF;
    }

    static ushort U16(byte[] b, int p) => (ushort)(b[p] | (b[p + 1] << 8));
    static uint U32(byte[] b, int p) => (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));

    static string ShortName(byte[] b, int p)
    {
        string nm = Encoding.ASCII.GetString(b, p, 8).TrimEnd();
        string ext = Encoding.ASCII.GetString(b, p + 8, 3).TrimEnd();
        return ext.Length == 0 ? nm : nm + "." + ext;
    }

    // The raw 11-byte directory name with no dot (e.g. "CODE    PY ") for exact comparison.
    static string RawName(byte[] b, int p) => Encoding.ASCII.GetString(b, p, 11);

    // Convert "code.py" → the padded 11-byte form "CODE    PY ".
    static string To83(string name)
    {
        int dot = name.LastIndexOf('.');
        string nm = (dot < 0 ? name : name[..dot]).ToUpperInvariant();
        string ext = (dot < 0 ? "" : name[(dot + 1)..]).ToUpperInvariant();
        return (nm.Length > 8 ? nm[..8] : nm.PadRight(8)) + (ext.Length > 3 ? ext[..3] : ext.PadRight(3));
    }
}

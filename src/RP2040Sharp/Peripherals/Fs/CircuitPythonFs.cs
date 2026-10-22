using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Reads a file by its '/'-separated path (e.g. "code.py" or "lib/pkg/mod.py"), matching long
    /// and short names case-insensitively. Returns null if the volume or file is absent. Accepts a
    /// bare volume image or a whole flash image.
    /// </summary>
    public static byte[]? ReadFile(byte[] flash, string path) => FatVolume.Open(flash, 0)?.ReadFile(path);

    /// <summary>Lists every file in the volume, recursively, as '/'-separated paths using long names.</summary>
    public static IReadOnlyList<string> ListFiles(byte[] flash)
    {
        var files = new List<string>();
        if (FatVolume.Open(flash, 0) is { } vol) CollectFiles(vol, "", files);
        return files;
    }

    static void CollectFiles(FatVolume vol, string dir, List<string> files)
    {
        foreach (var name in vol.List(dir))
        {
            var path = dir + name;
            if (vol.ReadFile(path) is null) CollectFiles(vol, path + "/", files);
            else files.Add(path);
        }
    }

    // ── Copying files onto CIRCUITPY ───────────────────────────────────────────

    /// <summary>
    /// What a PC does when it copies <paramref name="files"/> onto the CIRCUITPY drive, done on the FAT volume in the
    /// machine's flash: the volume is found after <paramref name="firmwareEnd"/> (the end of the firmware image; see
    /// <see cref="FatVolume"/>), the files are written (directories created, existing files replaced) and, with
    /// <paramref name="reset"/>, the chip reboots as a watchdog reset does so CircuitPython runs the new code.py. All or
    /// nothing: on a <see cref="FatException"/> (no volume yet, full, a bad path) flash is unchanged and there is no
    /// reset. Do not write under a running CircuitPython without the reset: it caches sectors in RAM and may write them
    /// back over the files.
    /// </summary>
    public static void PutFiles(RP2040Machine machine, int firmwareEnd, IEnumerable<(string Path, byte[] Data)> files, bool reset)
    {
        var flash = machine.FlashMemory;
        var vol = FatVolume.Open(flash, firmwareEnd)
            ?? throw new FatException("no FAT volume in flash after the firmware (CircuitPython formats CIRCUITPY on its first boot)");
        foreach (var (path, data) in files) vol.Put(path, data);
        vol.CopyTo(flash);
        if (reset) machine.SystemReset();
    }

    // ── Writing / baking ───────────────────────────────────────────────────────

    const int Bps = 512, Reserved = 1, RootEnts = 512, SecPerFat = 7;
    const string Label = "CIRCUITPY  ";

    /// <summary>
    /// Builds a fresh FAT12 volume of <paramref name="sizeBytes"/> containing
    /// <paramref name="files"/> (typically <c>code.py</c> + modules), using the exact geometry
    /// CircuitPython formats itself — so the firmware mounts it on boot and runs it instead of
    /// reformatting and dropping the files. Names are '/'-separated paths; subfolders and long
    /// file names are supported (see <see cref="FatVolume.Put"/>). The volume label is "CIRCUITPY".
    /// Throws <see cref="FatException"/> for an invalid or duplicate path, or when the files do not fit.
    /// </summary>
    public static byte[] BuildVolume(int sizeBytes, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var img = new byte[sizeBytes];
        WriteBootSector(img, sizeBytes / Bps);
        int fatStart = Reserved * Bps;
        int rootStart = (Reserved + SecPerFat) * Bps;
        img[fatStart] = 0xF8; img[fatStart + 1] = 0xFF; img[fatStart + 2] = 0xFF;
        WriteRaw11(img, rootStart, Label);
        img[rootStart + 11] = 0x08;

        var vol = FatVolume.FromImage(img);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, data) in files)
        {
            if (FatVolume.CheckPath(path) is { } error) throw new FatException(error);
            var normal = path.Replace('\\', '/');
            if (normal.StartsWith('/') || normal.EndsWith('/')) throw new FatException($"'{path}' must not start or end with a separator");
            if (!seen.Add(normal)) throw new FatException($"duplicate path '{path}'");
            vol.Put(path, data);
        }
        return img;
    }

    /// <summary>
    /// Builds a volume from every file under <paramref name="directory"/>, preserving the folder
    /// tree. Dot-files, dot-folders and <c>__pycache__</c> folders are skipped.
    /// </summary>
    public static byte[] BuildVolumeFromDirectory(int sizeBytes, string directory)
    {
        var files = new List<(string Name, byte[] Data)>();
        AddDirectory(new DirectoryInfo(directory), "", files);
        return BuildVolume(sizeBytes, files);
    }

    static void AddDirectory(DirectoryInfo dir, string prefix, List<(string Name, byte[] Data)> files)
    {
        foreach (var f in dir.GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            if (!f.Name.StartsWith('.')) files.Add((prefix + f.Name, File.ReadAllBytes(f.FullName)));
        foreach (var d in dir.GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            if (!d.Name.StartsWith('.') && d.Name != "__pycache__") AddDirectory(d, prefix + d.Name + "/", files);
    }


    static void WriteBootSector(byte[] img, int totSec)
    {
        img[0] = 0xEB; img[1] = 0xFE; img[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(img, 3);
        img[11] = Bps & 0xFF; img[12] = Bps >> 8;
        img[13] = 1;
        img[14] = Reserved;
        img[16] = 1;
        img[17] = RootEnts & 0xFF; img[18] = RootEnts >> 8;
        img[19] = (byte)(totSec & 0xFF); img[20] = (byte)((totSec >> 8) & 0xFF);
        img[21] = 0xF8;
        img[22] = SecPerFat & 0xFF; img[23] = SecPerFat >> 8;
        img[24] = 0x3F; img[26] = 0xFF;
        img[36] = 0x80; img[38] = 0x29;
        img[39] = 0x12; img[40] = 0x34; img[41] = 0x56; img[42] = 0x78;
        WriteRaw11(img, 43, Label);
        Encoding.ASCII.GetBytes("FAT12   ").CopyTo(img, 54);
        img[510] = 0x55; img[511] = 0xAA;
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

    // ── BPB helpers ───────────────────────────────────────────────────────────

    static ushort U16(byte[] b, int p) => (ushort)(b[p] | (b[p + 1] << 8));
    static uint U32(byte[] b, int p) => (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));

    static string ShortName(byte[] b, int p)
    {
        string nm = Encoding.ASCII.GetString(b, p, 8).TrimEnd();
        string ext = Encoding.ASCII.GetString(b, p + 8, 3).TrimEnd();
        return ext.Length == 0 ? nm : nm + "." + ext;
    }
}

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
    /// and short names case-insensitively and following the FAT12 cluster chain. Returns null if
    /// the volume or file is absent. Accepts a bare volume image or a whole flash image.
    /// </summary>
    public static byte[]? ReadFile(byte[] flash, string path)
    {
        if (!TryLocate(flash, out var r)) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        int dir = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            DirEntry? hit = null;
            foreach (var e in ReadDirectory(flash, r, dir))
            {
                if (e.IsDir == last || !string.Equals(e.Name, parts[i], StringComparison.OrdinalIgnoreCase)) continue;
                hit = e;
                break;
            }
            if (hit is null) return null;
            if (last) return ReadClusters(flash, r, hit.Value.Cluster, hit.Value.Size);
            dir = hit.Value.Cluster;
        }
        return null;
    }

    /// <summary>Lists every file in the volume, recursively, as '/'-separated paths using long names.</summary>
    public static IReadOnlyList<string> ListFiles(byte[] flash)
    {
        var files = new List<string>();
        if (TryLocate(flash, out var r)) CollectFiles(flash, r, 0, "", files, new HashSet<int>());
        return files;
    }

    static void CollectFiles(byte[] flash, Region r, int dir, string prefix, List<string> files, HashSet<int> seen)
    {
        foreach (var e in ReadDirectory(flash, r, dir))
        {
            if (!e.IsDir) files.Add(prefix + e.Name);
            else if (seen.Add(e.Cluster)) CollectFiles(flash, r, e.Cluster, prefix + e.Name + "/", files, seen);
        }
    }

    readonly record struct DirEntry(string Name, bool IsDir, int Cluster, uint Size);

    static int RootStart(Region r) => r.Offset + (r.ReservedSectors + r.NumFats * r.SectorsPerFat) * r.BytesPerSector;

    static int DataStart(Region r) =>
        RootStart(r) + (r.RootEntries * 32 + r.BytesPerSector - 1) / r.BytesPerSector * r.BytesPerSector;

    static List<DirEntry> ReadDirectory(byte[] flash, Region r, int cluster)
    {
        byte[] bytes = cluster == 0
            ? flash.AsSpan(RootStart(r), Math.Min(r.RootEntries * 32, flash.Length - RootStart(r))).ToArray()
            : ReadClusters(flash, r, cluster, uint.MaxValue);
        var entries = new List<DirEntry>();
        var lfn = new SortedDictionary<int, string>();
        for (int p = 0; p + 32 <= bytes.Length; p += 32)
        {
            if (bytes[p] == 0x00) break;
            if (bytes[p] == 0xE5) { lfn.Clear(); continue; }
            byte attr = bytes[p + 11];
            if (attr == 0x0F)
            {
                var sb = new StringBuilder();
                foreach (int o in LfnOffsets)
                {
                    int c = bytes[p + o] | (bytes[p + o + 1] << 8);
                    if (c == 0x0000 || c == 0xFFFF) break;
                    sb.Append((char)c);
                }
                lfn[bytes[p] & 0x3F] = sb.ToString();
                continue;
            }
            if ((attr & 0x08) != 0 || bytes[p] == 0x2E) { lfn.Clear(); continue; }

            string name;
            if (lfn.Count > 0 && LfnChecksum(bytes, p) == bytes[p - 19]) name = string.Concat(lfn.Values);
            else name = CasedShortName(bytes, p);
            lfn.Clear();
            entries.Add(new DirEntry(name, (attr & 0x10) != 0, U16(bytes, p + 26), U32(bytes, p + 28)));
        }
        return entries;
    }

    static byte[] ReadClusters(byte[] flash, Region r, int first, uint limit)
    {
        int clusterBytes = r.SectorsPerCluster * r.BytesPerSector;
        int dataStart = DataStart(r);
        var chain = new List<int>();
        for (int c = first; c >= 2 && c < 0xFF8 && chain.Count < 4096; c = NextCluster12(flash, r, c)) chain.Add(c);

        var buf = new byte[Math.Min(limit, (long)chain.Count * clusterBytes)];
        int done = 0;
        foreach (int c in chain)
        {
            int src = dataStart + (c - 2) * clusterBytes;
            int n = Math.Min(clusterBytes, buf.Length - done);
            if (n <= 0 || src + n > flash.Length) break;
            Array.Copy(flash, src, buf, done, n);
            done += n;
        }
        return done == buf.Length ? buf : buf[..done];
    }

    static string CasedShortName(byte[] b, int p)
    {
        string nm = Encoding.ASCII.GetString(b, p, 8).TrimEnd();
        string ext = Encoding.ASCII.GetString(b, p + 8, 3).TrimEnd();
        if ((b[p + 12] & 0x08) != 0) nm = nm.ToLowerInvariant();
        if ((b[p + 12] & 0x10) != 0) ext = ext.ToLowerInvariant();
        return ext.Length == 0 ? nm : nm + "." + ext;
    }

    static readonly int[] LfnOffsets = { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };

    static byte LfnChecksum(byte[] b, int p)
    {
        byte sum = 0;
        for (int k = 0; k < 11; k++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b[p + k]);
        return sum;
    }

    // ── Writing / baking ───────────────────────────────────────────────────────

    const int Bps = 512, Reserved = 1, RootEnts = 512, SecPerFat = 7;
    const int MaxClusters = SecPerFat * Bps * 2 / 3 - 2;
    const string Label = "CIRCUITPY  ";

    sealed class Node
    {
        public string Name = "";
        public bool IsDir;
        public byte[] Data = Array.Empty<byte>();
        public Dictionary<string, Node> Kids = new(StringComparer.OrdinalIgnoreCase);
        public Node? Parent;
        public int Cluster;
        public int Clusters;
        public string Short = "";
        public byte CaseFlags;
        public bool NeedsLfn;
    }

    /// <summary>
    /// Builds a fresh FAT12 volume of <paramref name="sizeBytes"/> containing
    /// <paramref name="files"/> (typically <c>code.py</c> + modules), using the exact geometry
    /// CircuitPython formats itself — so the firmware mounts it on boot and runs it instead of
    /// reformatting and dropping the files. Names are '/'-separated paths; subfolders and long
    /// file names are supported. The volume label is "CIRCUITPY".
    /// </summary>
    public static byte[] BuildVolume(int sizeBytes, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var root = new Node { IsDir = true };
        foreach (var (path, data) in files)
        {
            var parts = SplitPath(path);
            var dir = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!dir.Kids.TryGetValue(parts[i], out var next))
                {
                    next = new Node { Name = parts[i], IsDir = true, Parent = dir };
                    dir.Kids[parts[i]] = next;
                }
                else if (!next.IsDir) throw new ArgumentException($"'{parts[i]}' is both a file and a folder", nameof(files));
                dir = next;
            }
            string leaf = parts[^1];
            if (dir.Kids.TryGetValue(leaf, out var existing))
                throw new ArgumentException(existing.IsDir ? $"'{leaf}' is both a file and a folder" : $"Duplicate path '{path}'", nameof(files));
            dir.Kids[leaf] = new Node { Name = leaf, Data = data, Parent = dir };
        }

        int totSec = sizeBytes / Bps;
        var img = new byte[sizeBytes];
        WriteBootSector(img, totSec);

        int fatStart = Reserved * Bps;
        int rootStart = (Reserved + SecPerFat) * Bps;
        int dataStart = rootStart + RootEnts * 32;
        int dataClusters = Math.Min((sizeBytes - dataStart) / Bps, MaxClusters);
        img[fatStart] = 0xF8; img[fatStart + 1] = 0xFF; img[fatStart + 2] = 0xFF;

        int nextCluster = 2;
        Layout(root, ref nextCluster);
        if (nextCluster - 2 > dataClusters)
            throw new InvalidOperationException("Files do not fit in the CIRCUITPY volume");

        WriteRaw11(img, rootStart, Label);
        img[rootStart + 11] = 0x08;
        WriteDirectory(img, root, rootStart + 32, dataStart, fatStart);
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

    static string[] SplitPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part[^1] is '.' or ' ' || part.Any(c => c < 0x20 || c == 0x7F || "\"*:<>?|".Contains(c)))
                throw new ArgumentException($"Invalid path '{path}'", "files");
        }
        return parts;
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

    static void Layout(Node dir, ref int nextCluster)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        int entries = dir.Parent is null ? 1 : 2;
        foreach (var kid in dir.Kids.Values)
        {
            AssignShortName(kid, used);
            entries += 1 + (kid.NeedsLfn ? (kid.Name.Length + 12) / 13 : 0);
        }
        if (dir.Parent is null)
        {
            if (entries > RootEnts)
                throw new InvalidOperationException("Too many entries in the CIRCUITPY root folder");
        }
        else
        {
            dir.Clusters = (entries * 32 + Bps - 1) / Bps;
            dir.Cluster = nextCluster;
            nextCluster += dir.Clusters;
        }
        foreach (var kid in dir.Kids.Values)
        {
            if (kid.IsDir) Layout(kid, ref nextCluster);
            else
            {
                kid.Clusters = (kid.Data.Length + Bps - 1) / Bps;
                if (kid.Clusters == 0) continue;
                kid.Cluster = nextCluster;
                nextCluster += kid.Clusters;
            }
        }
    }

    static void AssignShortName(Node n, HashSet<string> used)
    {
        string name = n.Name;
        int dot = name.LastIndexOf('.');
        string baseName = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[(dot + 1)..] : "";
        bool plain = dot != 0 && baseName.Length is > 0 and <= 8 && ext.Length <= 3
            && !baseName.Contains('.') && baseName.All(IsShortChar) && ext.All(IsShortChar)
            && IsSingleCase(baseName) && IsSingleCase(ext);
        if (plain)
        {
            n.Short = baseName.ToUpperInvariant().PadRight(8) + ext.ToUpperInvariant().PadRight(3);
            if (baseName.Any(char.IsLower)) n.CaseFlags |= 0x08;
            if (ext.Any(char.IsLower)) n.CaseFlags |= 0x10;
            if (used.Add(n.Short)) return;
            n.CaseFlags = 0;
        }
        n.NeedsLfn = true;
        string cleanBase = new(baseName.ToUpperInvariant().Where(c => c != ' ' && c != '.').Select(c => IsShortChar(c) ? c : '_').ToArray());
        string cleanExt = new(ext.ToUpperInvariant().Where(c => c != ' ').Select(c => IsShortChar(c) ? c : '_').Take(3).ToArray());
        if (cleanBase.Length == 0) cleanBase = "_";
        for (int i = 1; ; i++)
        {
            string tail = "~" + i;
            string candidate = (cleanBase[..Math.Min(cleanBase.Length, 8 - tail.Length)] + tail).PadRight(8) + cleanExt.PadRight(3);
            if (used.Add(candidate))
            {
                n.Short = candidate;
                return;
            }
        }
    }

    static bool IsShortChar(char c) => c < 128 && (char.IsLetterOrDigit(c) || "$%'-_@~`!(){}^#&".Contains(c));

    static bool IsSingleCase(string s) => !(s.Any(char.IsLower) && s.Any(char.IsUpper));

    static void WriteDirectory(byte[] img, Node dir, int pos, int dataStart, int fatStart)
    {
        if (dir.Parent is not null)
        {
            pos = dataStart + (dir.Cluster - 2) * Bps;
            WriteShort(img, pos, ".          ", 0x10, 0, dir.Cluster, 0);
            WriteShort(img, pos + 32, "..         ", 0x10, 0, dir.Parent.Parent is null ? 0 : dir.Parent.Cluster, 0);
            pos += 64;
            Chain(img, fatStart, dir.Cluster, dir.Clusters);
        }
        foreach (var kid in dir.Kids.Values)
        {
            if (kid.NeedsLfn) pos = WriteLfn(img, pos, kid);
            WriteShort(img, pos, kid.Short, kid.IsDir ? (byte)0x10 : (byte)0x20, kid.CaseFlags, kid.Cluster, kid.IsDir ? 0 : kid.Data.Length);
            pos += 32;
            if (kid.IsDir) WriteDirectory(img, kid, 0, dataStart, fatStart);
            else if (kid.Clusters > 0)
            {
                Array.Copy(kid.Data, 0, img, dataStart + (kid.Cluster - 2) * Bps, kid.Data.Length);
                Chain(img, fatStart, kid.Cluster, kid.Clusters);
            }
        }
    }

    static void Chain(byte[] img, int fatStart, int first, int count)
    {
        for (int c = 0; c < count; c++) SetFat12(img, fatStart, first + c, c == count - 1 ? 0xFFF : first + c + 1);
    }

    static void SetFat12(byte[] img, int fatStart, int cl, int val)
    {
        int idx = fatStart + cl + cl / 2;
        if ((cl & 1) == 0) { img[idx] = (byte)(val & 0xFF); img[idx + 1] = (byte)((img[idx + 1] & 0xF0) | ((val >> 8) & 0x0F)); }
        else { img[idx] = (byte)((img[idx] & 0x0F) | ((val << 4) & 0xF0)); img[idx + 1] = (byte)((val >> 4) & 0xFF); }
    }

    static void WriteShort(byte[] img, int p, string raw11, byte attr, byte caseFlags, int cluster, int size)
    {
        WriteRaw11(img, p, raw11);
        img[p + 11] = attr;
        img[p + 12] = caseFlags;
        img[p + 26] = (byte)(cluster & 0xFF); img[p + 27] = (byte)(cluster >> 8);
        img[p + 28] = (byte)size; img[p + 29] = (byte)(size >> 8);
        img[p + 30] = (byte)(size >> 16); img[p + 31] = (byte)(size >> 24);
    }

    static int WriteLfn(byte[] img, int pos, Node n)
    {
        byte sum = 0;
        foreach (char ch in n.Short) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + (byte)ch);
        int count = (n.Name.Length + 12) / 13;
        for (int seq = count; seq >= 1; seq--)
        {
            img[pos] = (byte)(seq == count ? seq | 0x40 : seq);
            img[pos + 11] = 0x0F;
            img[pos + 13] = sum;
            for (int j = 0; j < 13; j++)
            {
                int idx = (seq - 1) * 13 + j;
                int v = idx < n.Name.Length ? n.Name[idx] : idx == n.Name.Length ? 0x0000 : 0xFFFF;
                img[pos + LfnOffsets[j]] = (byte)(v & 0xFF);
                img[pos + LfnOffsets[j] + 1] = (byte)(v >> 8);
            }
            pos += 32;
        }
        return pos;
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
}

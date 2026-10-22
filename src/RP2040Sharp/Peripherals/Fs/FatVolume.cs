using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RP2040.Peripherals.Fs;

/// <summary>A FAT volume could not be found, read or written; the message says why.</summary>
public sealed class FatException(string message) : IOException(message);

/// <summary>
/// A FAT12 / FAT16 volume held in memory: what a PC does when it copies files onto a USB drive, done on the image of
/// the CIRCUITPY volume that CircuitPython keeps in flash. Long file names (VFAT), subdirectories created on demand,
/// replacing a file frees its old clusters. Works on a copy: <see cref="Open"/> copies the volume out of the flash
/// image, <see cref="Put"/> edits the copy, <see cref="CopyTo"/> puts it back, so a failed write leaves flash as it was.
///
/// <para><b>Finding the volume</b> (<see cref="Locate"/>). CircuitPython keeps no partition table: the raspberrypi port
/// puts CIRCUITPY in the flash after its own image and the NVM sector, up to the end of the chip, and formats it with
/// FatFs (no MBR) on the first boot that finds no file system there. So the search starts at the end of the firmware
/// image (the highest flash address the UF2 programmed), rounded up to a 4 KiB erase sector, and takes the first erase
/// sector that holds a FAT boot sector whose BPB is consistent: 0x55AA, a jump instruction, 512..4096 bytes per sector,
/// a power-of-two cluster, 1 or 2 FATs, a FAT12/16 root directory, a volume that ends inside the flash, FATs large
/// enough for the clusters and a first FAT entry that repeats the media byte. Starting after the firmware keeps the
/// FAT-shaped constants inside the firmware itself (FatFs' boot-sector templates, the string "CIRCUITPY") out of the
/// search. FAT32 and exFAT are not supported: CircuitPython formats volumes this small as FAT12 or FAT16.</para>
/// </summary>
public sealed class FatVolume
{
    /// <summary>Longest file or directory name, in UTF-16 code units (the VFAT limit).</summary>
    public const int MaxNameLength = 255;
    /// <summary>Longest path accepted (Windows' MAX_PATH less "D:\" and the terminator), in UTF-16 code units.</summary>
    public const int MaxPathLength = 256;

    private const byte AttrDirectory = 0x10, AttrVolumeId = 0x08, AttrArchive = 0x20, AttrLfn = 0x0F;
    private const int Slot = 32;

    private readonly byte[] _v;
    private readonly int _fatStart, _rootStart, _dataStart, _rootEntries, _fatBytes, _numFats, _clusterCount;

    /// <summary>Flash offset of the boot sector (as given to <see cref="Open"/>).</summary>
    public int Offset { get; }
    /// <summary>12 or 16.</summary>
    public int FatBits { get; }
    public int ClusterBytes { get; }
    public int VolumeBytes { get; }
    /// <summary>The time stamped on the entries <see cref="Put"/> writes (local time, 2-second resolution).</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    private FatVolume(byte[] image, int offset, in Bpb b)
    {
        _v = image;
        Offset = offset;
        VolumeBytes = (int)b.VolumeBytes;
        FatBits = b.FatBits;
        ClusterBytes = b.BytesPerSector * b.SectorsPerCluster;
        _numFats = b.NumFats;
        _rootEntries = b.RootEntries;
        _fatBytes = b.SectorsPerFat * b.BytesPerSector;
        _fatStart = b.ReservedSectors * b.BytesPerSector;
        _rootStart = _fatStart + _numFats * _fatBytes;
        _dataStart = _rootStart + b.RootSectors * b.BytesPerSector;
        _clusterCount = b.Clusters;
    }

    private readonly record struct Bpb(int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int NumFats,
        int RootEntries, int RootSectors, int SectorsPerFat, long TotalSectors, int Clusters, int FatBits)
    {
        public long VolumeBytes => TotalSectors * BytesPerSector;
    }

    // ── Finding and opening ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Flash offset of the first FAT12/16 volume at or after <paramref name="searchFrom"/> (rounded up to 4 KiB), or -1.
    /// Pass the end of the firmware image (see the class remarks); 0 searches the whole flash.
    /// </summary>
    public static int Locate(ReadOnlySpan<byte> flash, int searchFrom)
    {
        for (long off = ((long)Math.Max(0, searchFrom) + 4095) & ~4095L; off + 512 <= flash.Length; off += 4096)
            if (TryParse(flash[(int)off..], out _)) return (int)off;
        return -1;
    }

    /// <summary>Open the volume found by <see cref="Locate"/>, or null when there is none (not formatted yet).</summary>
    public static FatVolume? Open(ReadOnlySpan<byte> flash, int searchFrom)
    {
        int off = Locate(flash, searchFrom);
        if (off < 0) return null;
        TryParse(flash[off..], out var b);
        return new FatVolume(flash.Slice(off, (int)b.VolumeBytes).ToArray(), off, b);
    }

    /// <summary>What <see cref="Probe"/> reports about a volume.</summary>
    public readonly record struct Summary(int Offset, int FatBits, int VolumeBytes, int ClusterBytes, long FreeBytes,
        IReadOnlyList<string> RootNames);

    /// <summary>
    /// The volume <see cref="Locate"/> finds, described from its boot sector, FATs and root directory only (a few KiB
    /// are read, not the whole volume), or null when there is none. Throws <see cref="FatException"/> on a root
    /// directory that cannot be read.
    /// </summary>
    public static Summary? Probe(ReadOnlySpan<byte> flash, int searchFrom)
    {
        int off = Locate(flash, searchFrom);
        if (off < 0) return null;
        TryParse(flash[off..], out var b);
        int meta = (b.ReservedSectors + b.NumFats * b.SectorsPerFat + b.RootSectors) * b.BytesPerSector;
        var v = new FatVolume(flash.Slice(off, meta).ToArray(), off, b);
        return new Summary(off, v.FatBits, v.VolumeBytes, v.ClusterBytes, v.FreeBytes, v.List(""));
    }

    /// <summary>Null when <paramref name="path"/> is one <see cref="Put"/> accepts, else why not.</summary>
    public static string? CheckPath(string path)
    {
        try { SplitPath(path); return null; }
        catch (FatException e) { return e.Message; }
    }

    /// <summary>True when two valid paths name the same file (FAT names compare without case).</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(string.Join('/', SplitPath(a)), string.Join('/', SplitPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Open a volume image whose boot sector is at index 0 (a whole volume, e.g. a test image).</summary>
    public static FatVolume FromImage(byte[] image)
    {
        if (!TryParse(image, out var b)) throw new FatException("not a FAT12/FAT16 volume");
        if (b.VolumeBytes != image.Length) throw new FatException($"the image is {image.Length} bytes, its boot sector says {b.VolumeBytes}");
        return new FatVolume(image, 0, b);
    }

    /// <summary>The volume's bytes (boot sector at 0), including what <see cref="Put"/> changed.</summary>
    public ReadOnlySpan<byte> Image => _v;

    /// <summary>Write the volume back at <see cref="Offset"/> of <paramref name="flash"/>.</summary>
    public void CopyTo(Span<byte> flash) => _v.CopyTo(flash[Offset..]);

    private static bool TryParse(ReadOnlySpan<byte> s, out Bpb b)
    {
        b = default;
        if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA || (s[0] != 0xEB && s[0] != 0xE9)) return false;
        int bps = U16(s, 11), spc = s[13], reserved = U16(s, 14), nFats = s[16], rootEnts = U16(s, 17);
        int spf = U16(s, 22), media = s[21];
        long tot = U16(s, 19) != 0 ? U16(s, 19) : U32(s, 32);
        if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0) return false;
        if (reserved < 1 || nFats is < 1 or > 2 || rootEnts == 0 || rootEnts * Slot % bps != 0) return false;
        if (spf == 0 || media is < 0xF8 and not 0xF0 || tot == 0 || tot * bps > s.Length) return false;
        int rootSectors = rootEnts * Slot / bps;
        long dataSectors = tot - reserved - (long)nFats * spf - rootSectors;
        if (dataSectors < spc) return false;
        long clusters = dataSectors / spc;
        int bits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 0;
        if (bits == 0 || (long)spf * bps * 8 / bits < clusters + 2) return false;   // FAT32 or a FAT too small
        if (s[reserved * bps] != media) return false;                               // FAT[0] repeats the media byte
        b = new Bpb(bps, spc, reserved, nFats, rootEnts, rootSectors, spf, tot, (int)clusters, bits);
        return true;
    }

    // ── Queries ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes in free clusters.</summary>
    public long FreeBytes
    {
        get
        {
            long free = 0;
            for (int c = 2; c < _clusterCount + 2; c++) if (Fat(c) == 0) free++;
            return free * ClusterBytes;
        }
    }

    /// <summary>The contents of the file at <paramref name="path"/>, or null when there is no such file.</summary>
    public byte[]? ReadFile(string path)
    {
        var parts = SplitPath(path);
        int dir = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (Find(dir, parts[i]) is not { } d || !d.IsDir) return null;
            dir = d.Cluster;
        }
        if (Find(dir, parts[^1]) is not { } f || f.IsDir) return null;
        var data = new byte[f.Size];
        if (data.Length == 0) return data;
        int done = 0;
        foreach (var c in Chain(f.Cluster))
        {
            if (done >= data.Length) break;
            int n = Math.Min(ClusterBytes, data.Length - done);
            _v.AsSpan(ClusterOffset(c), n).CopyTo(data.AsSpan(done));
            done += n;
        }
        if (done < data.Length) throw new FatException($"'{path}' is shorter on disk than its size says");
        return data;
    }

    /// <summary>The names in directory <paramref name="path"/> ("" or "/" = root), without "." and "..".</summary>
    public IReadOnlyList<string> List(string path)
    {
        int dir = 0;
        foreach (var p in path.Trim('/').Length == 0 ? [] : SplitPath(path))
        {
            if (Find(dir, p) is not { } d || !d.IsDir) throw new FatException($"no directory '{path}'");
            dir = d.Cluster;
        }
        var names = new List<string>();
        foreach (var e in Entries(dir)) if (e.Name is not ("." or "..")) names.Add(e.Name);
        return names;
    }

    // ── Writing ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create or replace the file <paramref name="path"/> ('/'-separated, relative to the root; missing directories are
    /// created) with <paramref name="data"/>. A replaced file keeps its name and directory entry and frees its old
    /// clusters first, so the new contents may reuse them. Throws <see cref="FatException"/> (the path is invalid or too
    /// long, a component is a file where a directory is needed, the target is a directory, the root directory or the
    /// volume is full); the volume may then be partly written, so discard it.
    /// </summary>
    public void Put(string path, ReadOnlySpan<byte> data)
    {
        var parts = SplitPath(path);
        int dir = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var sub = Find(dir, parts[i]);
            if (sub is { IsDir: false }) throw new FatException($"'{string.Join('/', parts[..(i + 1)])}' is a file, not a directory");
            dir = sub?.Cluster ?? MakeDirectory(dir, parts[i]);
        }

        var name = parts[^1];
        var existing = Find(dir, name);
        if (existing is { IsDir: true }) throw new FatException($"'{path}' is a directory");
        if (existing is { } old) FreeChain(old.Cluster);

        int first = data.Length == 0 ? 0 : Allocate((data.Length + ClusterBytes - 1) / ClusterBytes, path);
        int done = 0;
        foreach (var c in data.Length == 0 ? [] : Chain(first))
        {
            var dst = _v.AsSpan(ClusterOffset(c), ClusterBytes);
            int n = Math.Min(ClusterBytes, data.Length - done);
            data.Slice(done, n).CopyTo(dst);
            dst[n..].Clear();
            done += n;
        }

        int sfn = existing?.Sfn ?? CreateEntry(dir, name, AttrArchive);
        SetEntry(sfn, first, (uint)data.Length);
    }

    private int MakeDirectory(int parent, string name)
    {
        int c = Allocate(1, name);
        _v.AsSpan(ClusterOffset(c), ClusterBytes).Clear();
        WriteSfn(ClusterOffset(c), ".          "u8, AttrDirectory, 0);
        WriteSfn(ClusterOffset(c) + Slot, "..         "u8, AttrDirectory, 0);
        SetEntry(ClusterOffset(c), c, 0);
        SetEntry(ClusterOffset(c) + Slot, parent, 0);     // ".." of a top-level directory is cluster 0 (the root)
        int e = CreateEntry(parent, name, AttrDirectory);
        SetEntry(e, c, 0);
        return c;
    }

    private void SetEntry(int e, int cluster, uint size)
    {
        var t = Timestamp;
        ushort time = (ushort)(t.Hour << 11 | t.Minute << 5 | t.Second / 2);
        ushort date = (ushort)(Math.Clamp(t.Year - 1980, 0, 127) << 9 | t.Month << 5 | t.Day);
        W16(e + 14, time); W16(e + 16, date); W16(e + 18, date);    // created, accessed
        W16(e + 20, 0);                                             // high cluster word (FAT32 only)
        W16(e + 22, time); W16(e + 24, date);                       // written
        W16(e + 26, (ushort)cluster);
        W32(e + 28, size);
    }

    /// <summary>Write a new directory entry (with its long-name slots when needed) and return the short entry's offset.</summary>
    private int CreateEntry(int dir, string name, byte attr)
    {
        Span<byte> sfn = stackalloc byte[11];
        var taken = ShortNames(dir);
        byte caseBits = ShortNameOf(name, sfn);   // 0xFF: the name needs a long-name entry
        if (caseBits != 0xFF && taken.Contains(Encoding.Latin1.GetString(sfn))) caseBits = 0xFF;   // "a~1.py" vs a tail
        int lfnSlots = 0;
        if (caseBits == 0xFF)
        {
            caseBits = 0;
            MakeUniqueShortName(name, sfn, taken);
            lfnSlots = (name.Length + 12) / 13;
        }

        var run = FreeRun(dir, lfnSlots + 1);
        byte sum = Checksum(sfn);
        for (int i = 0; i < lfnSlots; i++)
        {
            int p = run[i], seq = lfnSlots - i;
            _v.AsSpan(p, Slot).Clear();
            _v[p] = (byte)(seq | (i == 0 ? 0x40 : 0));
            _v[p + 11] = AttrLfn;
            _v[p + 13] = sum;
            for (int k = 0; k < 13; k++)
            {
                int idx = (seq - 1) * 13 + k;
                ushort ch = idx < name.Length ? name[idx] : idx == name.Length ? (ushort)0 : (ushort)0xFFFF;
                W16(p + (k < 5 ? 1 + 2 * k : k < 11 ? 14 + 2 * (k - 5) : 28 + 2 * (k - 11)), ch);
            }
        }
        int e = run[lfnSlots];
        WriteSfn(e, sfn, attr, caseBits);
        return e;
    }

    private void WriteSfn(int e, ReadOnlySpan<byte> sfn, byte attr, byte caseBits)
    {
        _v.AsSpan(e, Slot).Clear();
        sfn.CopyTo(_v.AsSpan(e));
        _v[e + 11] = attr;
        _v[e + 12] = caseBits;
    }

    // ── Names ───────────────────────────────────────────────────────────────────────────────────────────

    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new FatException("empty path");
        var p = path.Replace('\\', '/');
        if (p.Length > MaxPathLength) throw new FatException($"path too long: {p.Length} characters, the limit is {MaxPathLength}");
        var parts = p.Trim('/').Split('/');
        foreach (var n in parts)
        {
            if (n.Length == 0) throw new FatException($"empty name in '{path}'");
            if (n is "." or "..") throw new FatException($"'{n}' is not allowed in '{path}'");
            if (n.Length > MaxNameLength) throw new FatException($"name too long: '{n[..16]}...' has {n.Length} characters, the limit is {MaxNameLength}");
            if (n[^1] is '.' or ' ') throw new FatException($"'{n}' ends in a dot or a space");
            foreach (char c in n)
                if (c < 0x20 || c == 0x7F || "\"*:<>?|".Contains(c)) throw new FatException($"'{n}' contains a character FAT does not allow");
        }
        return parts;
    }

    private static bool ShortChar(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || "$%'-_@~`!(){}^#&".Contains(c);

    /// <summary>The 8.3 form of <paramref name="name"/> when it is one (each part in a single case): returns the
    /// NT case bits (0x08 lower-case base, 0x10 lower-case extension). 0xFF when it needs a long name.</summary>
    private static byte ShortNameOf(string name, Span<byte> sfn)
    {
        int dot = name.IndexOf('.');
        if (dot != name.LastIndexOf('.') || dot == 0) return 0xFF;
        string b = dot < 0 ? name : name[..dot], x = dot < 0 ? "" : name[(dot + 1)..];
        if (b.Length is 0 or > 8 || x.Length > 3) return 0xFF;
        foreach (char c in name) if (c != '.' && !ShortChar(c)) return 0xFF;
        static int Case(string s) =>
            (s.Any(char.IsLower) ? 1 : 0) | (s.Any(char.IsUpper) ? 2 : 0);   // 3 = mixed
        int cb = Case(b), cx = Case(x);
        if (cb == 3 || cx == 3) return 0xFF;
        sfn.Fill((byte)' ');
        for (int i = 0; i < b.Length; i++) sfn[i] = (byte)char.ToUpperInvariant(b[i]);
        for (int i = 0; i < x.Length; i++) sfn[8 + i] = (byte)char.ToUpperInvariant(x[i]);
        if (sfn[0] == 0xE5) sfn[0] = 0x05;
        return (byte)((cb == 1 ? 0x08 : 0) | (cx == 1 ? 0x10 : 0));
    }

    /// <summary>The numeric-tail short name Windows makes for a long name ("TURBO_~1PY " style), unique in <paramref name="dir"/>.</summary>
    private HashSet<string> ShortNames(int dir)
    {
        var taken = new HashSet<string>();
        foreach (var e in Entries(dir)) taken.Add(e.Raw11);
        return taken;
    }

    private static void MakeUniqueShortName(string name, Span<byte> sfn, HashSet<string> taken)
    {
        var s = name.Replace(" ", "").TrimStart('.');
        int dot = s.LastIndexOf('.');
        string b = dot < 0 ? s : s[..dot], x = dot < 0 ? "" : s[(dot + 1)..];
        static string Clean(string t, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in t.Replace(".", ""))
            {
                if (sb.Length == max) break;
                sb.Append(ShortChar(c) ? char.ToUpperInvariant(c) : '_');
            }
            return sb.ToString();
        }
        string bb = Clean(b, 8), xx = Clean(x, 3);
        if (bb.Length == 0) bb = "_";
        for (int n = 1; n < 1_000_000; n++)
        {
            string tail = "~" + n;
            string raw = (bb[..Math.Min(bb.Length, 8 - tail.Length)] + tail).PadRight(8) + xx.PadRight(3);
            if (taken.Contains(raw)) continue;
            for (int i = 0; i < 11; i++) sfn[i] = (byte)raw[i];
            return;
        }
        throw new FatException($"no free short name for '{name}'");
    }

    private static byte Checksum(ReadOnlySpan<byte> sfn)
    {
        byte sum = 0;
        foreach (byte c in sfn) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + c);
        return sum;
    }

    // ── Directories ─────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Entry(string Name, string Raw11, int Sfn, byte Attr, int Cluster, uint Size)
    {
        public bool IsDir => (Attr & AttrDirectory) != 0;
    }

    /// <summary>Byte offsets of the 32-byte slots of directory <paramref name="dir"/> (0 = the fixed root).</summary>
    private List<int> Slots(int dir)
    {
        var slots = new List<int>();
        if (dir == 0) { for (int i = 0; i < _rootEntries; i++) slots.Add(_rootStart + i * Slot); return slots; }
        foreach (var c in Chain(dir))
            for (int i = 0; i < ClusterBytes; i += Slot) slots.Add(ClusterOffset(c) + i);
        return slots;
    }

    private IEnumerable<Entry> Entries(int dir)
    {
        char[]? lfn = null;
        int lfnLeft = 0;
        byte lfnSum = 0;
        foreach (int p in Slots(dir))
        {
            byte first = _v[p], attr = _v[p + 11];
            if (first == 0x00) yield break;
            if (first == 0xE5) { lfn = null; continue; }
            if (attr == AttrLfn)
            {
                int seq = first & 0x1F;
                if ((first & 0x40) != 0) { lfn = new char[seq * 13]; lfnLeft = seq; lfnSum = _v[p + 13]; }
                if (lfn == null || seq != lfnLeft || seq == 0 || _v[p + 13] != lfnSum) { lfn = null; continue; }
                for (int k = 0; k < 13; k++)
                    lfn[(seq - 1) * 13 + k] = (char)U16(_v, p + (k < 5 ? 1 + 2 * k : k < 11 ? 14 + 2 * (k - 5) : 28 + 2 * (k - 11)));
                lfnLeft--;
                continue;
            }
            var raw = Encoding.Latin1.GetString(_v, p, 11);
            string name;
            if (lfn != null && lfnLeft == 0 && Checksum(_v.AsSpan(p, 11)) == lfnSum)
            {
                int len = Array.IndexOf(lfn, '\0');
                name = new string(lfn, 0, len < 0 ? lfn.Length : len);
            }
            else
            {
                byte nt = _v[p + 12];
                string b = raw[..8].TrimEnd(), x = raw[8..].TrimEnd();
                if (b.Length > 0 && b[0] == '\x05') b = "\xE5" + b[1..];
                if ((nt & 0x08) != 0) b = b.ToLowerInvariant();
                if ((nt & 0x10) != 0) x = x.ToLowerInvariant();
                name = x.Length == 0 ? b : b + "." + x;
            }
            lfn = null;
            if ((attr & AttrVolumeId) != 0) continue;
            yield return new Entry(name, raw, p, attr, U16(_v, p + 26), U32(_v, p + 28));
        }
    }

    private Entry? Find(int dir, string name)
    {
        foreach (var e in Entries(dir))
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>Offsets of <paramref name="count"/> consecutive free slots in <paramref name="dir"/>; a subdirectory
    /// grows by a zeroed cluster when it has none, the root cannot.</summary>
    private List<int> FreeRun(int dir, int count)
    {
        for (;;)
        {
            var slots = Slots(dir);
            for (int i = 0, run = 0; i < slots.Count; i++)
            {
                run = _v[slots[i]] is 0x00 or 0xE5 ? run + 1 : 0;
                if (run == count) return slots.GetRange(i - count + 1, count);
            }
            if (dir == 0) throw new FatException($"the root directory is full ({_rootEntries} entries)");
            int last = dir;
            foreach (var c in Chain(dir)) last = c;
            int added = Allocate(1, "a directory");
            _v.AsSpan(ClusterOffset(added), ClusterBytes).Clear();
            SetFat(last, added);
        }
    }

    // ── Clusters ────────────────────────────────────────────────────────────────────────────────────────

    private int ClusterOffset(int c) => _dataStart + (c - 2) * ClusterBytes;

    private int Eoc => FatBits == 12 ? 0xFFF : 0xFFFF;

    private int Fat(int c)
    {
        if (FatBits == 16) return U16(_v, _fatStart + c * 2);
        int i = _fatStart + c * 3 / 2, v = _v[i] | _v[i + 1] << 8;
        return (c & 1) != 0 ? v >> 4 : v & 0xFFF;
    }

    private void SetFat(int c, int value)
    {
        for (int f = 0; f < _numFats; f++)
        {
            int start = _fatStart + f * _fatBytes;
            if (FatBits == 16) { W16(start + c * 2, (ushort)value); continue; }
            int i = start + c * 3 / 2;
            if ((c & 1) == 0) { _v[i] = (byte)value; _v[i + 1] = (byte)(_v[i + 1] & 0xF0 | value >> 8 & 0x0F); }
            else { _v[i] = (byte)(_v[i] & 0x0F | value << 4 & 0xF0); _v[i + 1] = (byte)(value >> 4); }
        }
    }

    /// <summary>The clusters of the chain starting at <paramref name="first"/>; a broken or looping chain throws.</summary>
    private IEnumerable<int> Chain(int first)
    {
        int c = first;
        for (int n = 0; n <= _clusterCount; n++)
        {
            if (c < 2 || c >= _clusterCount + 2) throw new FatException($"corrupt FAT: cluster {c} in a chain");
            yield return c;
            int next = Fat(c);
            if (next >= (FatBits == 12 ? 0xFF8 : 0xFFF8)) yield break;
            c = next;
        }
        throw new FatException("corrupt FAT: a cluster chain loops");
    }

    private void FreeChain(int first)
    {
        if (first == 0) return;   // an empty file has no clusters
        var chain = new List<int>(Chain(first));
        foreach (var c in chain) SetFat(c, 0);
    }

    /// <summary>Link <paramref name="count"/> free clusters into a chain and return its first one.</summary>
    private int Allocate(int count, string forWhat)
    {
        var free = new List<int>(count);
        for (int c = 2; c < _clusterCount + 2 && free.Count < count; c++) if (Fat(c) == 0) free.Add(c);
        if (free.Count < count)
            throw new FatException($"the volume is full: '{forWhat}' needs {(long)count * ClusterBytes} bytes, {FreeBytes} are free");
        for (int i = 0; i < count; i++) SetFat(free[i], i + 1 < count ? free[i + 1] : Eoc);
        return free[0];
    }

    // ── Little-endian helpers ───────────────────────────────────────────────────────────────────────────

    private static ushort U16(ReadOnlySpan<byte> b, int p) => (ushort)(b[p] | b[p + 1] << 8);
    private static uint U32(ReadOnlySpan<byte> b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);
    private void W16(int p, ushort v) { _v[p] = (byte)v; _v[p + 1] = (byte)(v >> 8); }
    private void W32(int p, uint v) { W16(p, (ushort)v); W16(p + 2, (ushort)(v >> 16)); }
}

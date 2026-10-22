using System.Text;
using FluentAssertions;
using RP2040.Peripherals.Fs;
using Xunit;

namespace RP2040.Peripherals.Tests.Fs;

/// <summary>
/// <see cref="FatVolume"/> on synthetic FAT12 / FAT16 images shaped as FatFs (CircuitPython) formats them: finding the
/// volume after the firmware, long names, subdirectories, replacing files, and the clean failures (not formatted, full,
/// root directory full, names and paths too long).
/// </summary>
public class FatVolumeTests
{
    /// <summary>A blank FAT volume: no MBR, media 0xF8, FAT[0..1] = media + end-of-chain, as f_mkfs leaves it.</summary>
    internal static byte[] Format(int totalSectors, int sectorsPerCluster = 1, int fats = 1, int rootEntries = 512, int bps = 512)
    {
        var v = new byte[totalSectors * bps];
        int rootSectors = rootEntries * 32 / bps;
        // FAT size: iterate until the clusters fit (FAT12 if < 4085 clusters, else FAT16).
        int spf = 1, clusters, bits;
        for (;;)
        {
            clusters = (totalSectors - 1 - fats * spf - rootSectors) / sectorsPerCluster;
            bits = clusters < 4085 ? 12 : 16;
            if ((long)spf * bps * 8 / bits >= clusters + 2) break;
            spf++;
        }
        v[0] = 0xEB; v[1] = 0xFE; v[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(v, 3);
        BitConverter.TryWriteBytes(v.AsSpan(11), (ushort)bps);
        v[13] = (byte)sectorsPerCluster;
        BitConverter.TryWriteBytes(v.AsSpan(14), (ushort)1);
        v[16] = (byte)fats;
        BitConverter.TryWriteBytes(v.AsSpan(17), (ushort)rootEntries);
        if (totalSectors < 0x10000) BitConverter.TryWriteBytes(v.AsSpan(19), (ushort)totalSectors);
        else BitConverter.TryWriteBytes(v.AsSpan(32), totalSectors);
        v[21] = 0xF8;
        BitConverter.TryWriteBytes(v.AsSpan(22), (ushort)spf);
        v[38] = 0x29;
        Encoding.ASCII.GetBytes("CIRCUITPY  " + (bits == 12 ? "FAT12   " : "FAT16   ")).CopyTo(v, 43);
        v[510] = 0x55; v[511] = 0xAA;
        for (int f = 0; f < fats; f++)
        {
            int p = bps + f * spf * bps;
            v[p] = 0xF8; v[p + 1] = 0xFF; v[p + 2] = 0xFF;
            if (bits == 16) v[p + 3] = 0xFF;
        }
        int root = bps * (1 + fats * spf);
        Encoding.ASCII.GetBytes("CIRCUITPY  ").CopyTo(v, root);
        v[root + 11] = 0x08;   // volume label
        return v;
    }

    public static TheoryData<int, int> Geometries => new()
    {
        { 2048, 1 },    // 1 MiB, 512 B clusters: FAT12 (CircuitPython 10 on a 2 MiB board)
        { 6144, 2 },    // 3 MiB, 1 KiB clusters: FAT12 (CircuitPython 11 on the 4 MiB Pico 2)
        { 16384, 1 },   // 8 MiB, 512 B clusters: FAT16
    };

    private static byte[] Bytes(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

    [Theory]
    [MemberData(nameof(Geometries))]
    public void Files_in_the_root_and_in_new_subdirectories_read_back(int sectors, int spc)
    {
        var vol = FatVolume.FromImage(Format(sectors, spc));
        vol.FatBits.Should().Be(sectors >= 16384 ? 16 : 12);

        var code = Encoding.ASCII.GetBytes("import x\nprint(x.VALUE)\n");
        var x = Bytes(3000, 1);
        var deep = Bytes(70_000, 2);
        vol.Put("code.py", code);
        vol.Put("lib/x.py", x);
        vol.Put("/lib/adafruit_display_text/bitmap_label.py", deep);
        vol.Put("lib/empty.txt", []);

        vol.ReadFile("code.py").Should().Equal(code);
        vol.ReadFile("CODE.PY").Should().Equal(code, "FAT names compare without case");
        vol.ReadFile("lib/x.py").Should().Equal(x);
        vol.ReadFile("lib/adafruit_display_text/bitmap_label.py").Should().Equal(deep);
        vol.ReadFile("lib/empty.txt").Should().BeEmpty();
        vol.List("/").Should().BeEquivalentTo("code.py", "lib");
        vol.List("lib").Should().BeEquivalentTo("x.py", "adafruit_display_text", "empty.txt");
    }

    [Fact]
    public void An_8_3_name_in_one_case_is_a_short_entry_with_the_NT_case_bits_others_get_a_long_name()
    {
        var vol = FatVolume.FromImage(Format(2048));
        vol.Put("code.py", [1]);
        vol.Put("README.TXT", [2]);
        vol.Put("Main.py", [3]);           // mixed case: long name
        vol.Put("very_long_module_name_1.py", [4]);
        vol.Put("very_long_module_name_2.py", [5]);
        vol.Put("café.py", [6]);           // not ASCII: long name

        var img = vol.Image.ToArray();
        int root = 512 * (1 + BitConverter.ToUInt16(img, 22));   // 1 reserved sector + 1 FAT
        string Raw(int slot) => Encoding.Latin1.GetString(img, root + slot * 32, 11);
        Raw(0).Should().Be("CIRCUITPY  ");
        Raw(1).Should().Be("CODE    PY ");
        img[root + 32 + 12].Should().Be(0x18, "lower-case base (0x08) and extension (0x10)");
        Raw(2).Should().Be("README  TXT");
        img[root + 64 + 12].Should().Be(0);
        img[root + 96 + 11].Should().Be(0x0F, "Main.py starts with a long-name slot");
        vol.List("").Should().Contain(["Main.py", "very_long_module_name_1.py", "very_long_module_name_2.py", "café.py"]);
        var all = Enumerable.Range(0, 20).Select(Raw).ToList();
        all.Should().Contain("VERY_L~1PY ").And.Contain("VERY_L~2PY ");
        vol.ReadFile("very_long_module_name_2.py").Should().Equal([5]);
        vol.ReadFile("CAFÉ.PY").Should().Equal([6]);
    }

    [Fact]
    public void Replacing_a_file_frees_its_old_clusters_and_keeps_every_FAT_copy_equal()
    {
        var vol = FatVolume.FromImage(Format(2048, fats: 2));
        long free0 = vol.FreeBytes;
        vol.Put("big.bin", Bytes(10 * 512, 3));
        vol.FreeBytes.Should().Be(free0 - 10 * 512);
        vol.Put("BIG.BIN", Bytes(100, 4));
        vol.FreeBytes.Should().Be(free0 - 512, "the 10 old clusters are free again, the new file takes one");
        vol.ReadFile("big.bin").Should().Equal(Bytes(100, 4));
        vol.List("").Should().Equal("big.bin");   // the name and entry of the replaced file stay

        var img = vol.Image.ToArray();
        int spf = BitConverter.ToUInt16(img, 22);
        img.AsSpan(512, spf * 512).SequenceEqual(img.AsSpan(512 + spf * 512, spf * 512)).Should().BeTrue();
    }

    [Fact]
    public void A_subdirectory_grows_by_clusters_as_entries_are_added()
    {
        var vol = FatVolume.FromImage(Format(2048));      // 512 B clusters: 16 slots each
        for (int i = 0; i < 40; i++) vol.Put($"d/module_with_a_long_name_{i:D2}.py", Bytes(10, i));
        vol.List("d").Should().HaveCount(40);
        for (int i = 0; i < 40; i++) vol.ReadFile($"d/module_with_a_long_name_{i:D2}.py").Should().Equal(Bytes(10, i));
    }

    [Fact]
    public void Locate_finds_the_volume_after_the_firmware_and_ignores_blank_flash()
    {
        var flash = new byte[2 * 1024 * 1024];
        flash.AsSpan().Fill(0xFF);
        FatVolume.Locate(flash, 0).Should().Be(-1, "nothing is formatted yet");
        FatVolume.Open(flash, 0).Should().BeNull();

        var vol = Format(2048);
        vol.CopyTo(flash, 0x100000);
        Format(64, rootEntries: 16).CopyTo(flash, 0x8000);   // a FAT-shaped template inside the "firmware"
        FatVolume.Locate(flash, 0x8000 + 1).Should().Be(0x100000, "the search starts after the firmware, on 4 KiB");
        FatVolume.Locate(flash, 0).Should().Be(0x8000);
        var opened = FatVolume.Open(flash, 0x9000)!;
        opened.Offset.Should().Be(0x100000);
        opened.VolumeBytes.Should().Be(1024 * 1024);

        opened.Put("code.py", "print(1)"u8);
        opened.CopyTo(flash);
        FatVolume.Open(flash, 0x9000)!.ReadFile("code.py").Should().Equal("print(1)"u8.ToArray());
    }

    [Fact]
    public void A_boot_sector_that_does_not_fit_the_flash_is_not_a_volume()
    {
        var flash = new byte[1024 * 1024];
        Format(2048).AsSpan(0, 4096).CopyTo(flash.AsSpan(0x80000));   // says 1 MiB, only 512 KiB left
        FatVolume.Locate(flash, 0).Should().Be(-1);
    }

    [Fact]
    public void A_full_volume_fails_with_a_clear_error()
    {
        var vol = FatVolume.FromImage(Format(256));
        var act = () => vol.Put("big.bin", new byte[200 * 1024]);
        act.Should().Throw<FatException>().WithMessage("*volume is full*big.bin*");
    }

    [Fact]
    public void A_full_root_directory_fails_with_a_clear_error()
    {
        var vol = FatVolume.FromImage(Format(2048, rootEntries: 16));
        for (int i = 0; i < 15; i++) vol.Put($"f{i}.txt", [1]);   // 15 + the volume label = 16
        var act = () => vol.Put("one_more.txt", [1]);
        act.Should().Throw<FatException>().WithMessage("*root directory is full*");
    }

    [Theory]
    [InlineData(256, 1, "*name too long*")]
    [InlineData(100, 3, "*path too long*")]
    public void Names_and_paths_over_the_limits_are_rejected(int nameLength, int depth, string message)
    {
        var path = string.Join('/', Enumerable.Repeat(new string('a', nameLength), depth));
        var act = () => FatVolume.FromImage(Format(2048)).Put(path, [1]);
        act.Should().Throw<FatException>().WithMessage(message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("../x.py")]
    [InlineData("bad:name.py")]
    [InlineData("trailing.")]
    public void Invalid_paths_are_rejected(string path)
    {
        var act = () => FatVolume.FromImage(Format(2048)).Put(path, [1]);
        act.Should().Throw<FatException>();
    }

    [Fact]
    public void A_file_where_a_directory_is_needed_and_a_directory_as_target_are_rejected()
    {
        var vol = FatVolume.FromImage(Format(2048));
        vol.Put("lib", [1]);
        vol.Invoking(v => v.Put("lib/x.py", [1])).Should().Throw<FatException>().WithMessage("*'lib' is a file*");
        vol.Put("src/a.py", [1]);
        vol.Invoking(v => v.Put("src", [1])).Should().Throw<FatException>().WithMessage("*is a directory*");
    }

    [Fact]
    public void A_looping_cluster_chain_is_reported_as_corrupt()
    {
        var vol = FatVolume.FromImage(Format(2048));
        vol.Put("a.bin", Bytes(1024, 0));      // clusters 2 -> 3
        var img = vol.Image.ToArray();
        // FAT12 entry 3 := 2 (a loop): bytes 4..5 of the FAT hold entries 2 and 3.
        int fat = 512, e3 = fat + 3 * 3 / 2;
        img[e3] = (byte)(img[e3] & 0x0F | 0x20); img[e3 + 1] = 0x00;
        var bad = FatVolume.FromImage(img);
        bad.Invoking(v => v.Put("a.bin", [1])).Should().Throw<FatException>().WithMessage("*corrupt*");
    }

    [Fact]
    public void Format_ProducesAnOpenableCircuitPythonVolume()
    {
        var vol = FatVolume.Format(0x100000);
        vol.FatBits.Should().Be(12);
        vol.ClusterBytes.Should().Be(512);
        vol.List("").Should().BeEmpty();
        FatVolume.FromImage(vol.Image.ToArray()).VolumeBytes.Should().Be(0x100000);
    }

    [Fact]
    public void Format_TooSmallOrTooLarge_Throws()
    {
        FluentActions.Invoking(() => FatVolume.Format(1024)).Should().Throw<FatException>();
        FluentActions.Invoking(() => FatVolume.Format(64 * 1024 * 1024)).Should().Throw<FatException>();
    }

    [Fact]
    public void ListFiles_RecursesIntoSubdirectories()
    {
        var vol = FatVolume.Format(0x100000);
        vol.Put("code.py", [1]);
        vol.Put("lib/pkg/mod.py", [2]);
        vol.Put("lib/other.py", [3]);
        vol.ListFiles().Should().BeEquivalentTo("code.py", "lib/pkg/mod.py", "lib/other.py");
    }
}

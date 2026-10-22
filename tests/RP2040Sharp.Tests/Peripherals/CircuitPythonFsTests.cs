using System.Security.Cryptography;
using System.Text;
using RP2040.Peripherals.Fs;

namespace RP2040Sharp.Tests.Peripherals;

public class CircuitPythonFsTests
{
    private const int Size = CircuitPythonFs.DefaultRegionSize;
    private const int RootStart = 8 * 512;
    private const int DataStart = RootStart + 512 * 32;
    private static readonly int[] LfnOffsets = { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 7 + seed);
        return data;
    }

    private static List<(string Name, byte[] Data)> Files(params (string, byte[])[] files) => files.ToList();

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static byte Checksum(byte[] img, int p)
    {
        byte sum = 0;
        for (int k = 0; k < 11; k++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + img[p + k]);
        return sum;
    }

    [Fact]
    public void BuildVolume_EmptyFileList_IsByteIdenticalToPreviousOutput()
    {
        Sha(CircuitPythonFs.BuildVolume(Size, Files())).Should().Be(
            "09A93E89194B8986C772CD5013B6A438A45BB090FEBE3D4F831C3F753704759F");
    }

    [Fact]
    public void BuildVolume_SingleCaseShortNames_RoundTripWithOriginalCaseViaNtFlags()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("code.py", B("a")), ("BOOT.PY", B("b")), ("README", B("c")), ("Boot.txt", B("d"))));

        img[RootStart + 32 + 12].Should().Be(0x18);
        img[RootStart + 64 + 12].Should().Be(0x00);
        img[RootStart + 96 + 12].Should().Be(0x00);
        CircuitPythonFs.ListFiles(img).Should().Equal("code.py", "BOOT.PY", "README", "Boot.txt");
    }

    [Fact]
    public void BuildVolume_EmptyFile_UsesClusterZeroAndSizeZero()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("__init__.py", Array.Empty<byte>()), ("a.py", B("x"))));

        img[RootStart + 32 + 26].Should().Be(0);
        img[RootStart + 32 + 28].Should().Be(0);
        img[RootStart + 64 + 26].Should().Be(2);
        CircuitPythonFs.ReadFile(img, "__init__.py").Should().BeEmpty();
    }

    [Theory]
    [InlineData("a.py.")]
    [InlineData("a.py ")]
    public void BuildVolume_TrailingDotOrSpace_ThrowsArgumentException(string path)
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files((path, B("x"))))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildVolume_RealisticProject_RoundTrips()
    {
        var big = Pattern(70_000, 3);
        var mpy = new byte[] { 0x43, 0x06, 0x00, 0x1F, 0xFF, 0x00, 0x80, 0x7F };
        var files = Files(
            ("code.py", B("import adafruit_display_text\nprint('hi')\n")),
            ("boot.py", B("import storage\n")),
            ("settings.toml", B("CIRCUITPY_WIFI_SSID = \"x\"\n")),
            ("lib/adafruit_display_text/__init__.py", Array.Empty<byte>()),
            ("lib/adafruit_display_text/label.py", Pattern(5000, 1)),
            ("lib/adafruit_display_text/bitmap_label.py", B("class BitmapLabel: pass\n")),
            ("lib/adafruit_bus_device/i2c_device.py", B("class I2CDevice: pass\n")),
            ("lib/adafruit_bus_device/__init__.py", B("")),
            ("lib/big_data.bin", big),
            ("lib/driver.mpy", mpy));

        var img = CircuitPythonFs.BuildVolume(Size, files);

        foreach (var (name, data) in files)
            CircuitPythonFs.ReadFile(img, name).Should().Equal(data, name);
        CircuitPythonFs.ListFiles(img).Should().BeEquivalentTo(files.Select(f => f.Name));
        CircuitPythonFs.ReadFile(img, "LIB/ADAFRUIT_DISPLAY_TEXT/LABEL.PY").Should().Equal(Pattern(5000, 1));
        CircuitPythonFs.ReadFile(img, "lib\\driver.mpy").Should().Equal(mpy);
        CircuitPythonFs.ReadFile(img, "lib/missing.py").Should().BeNull();
        CircuitPythonFs.ReadFile(img, "code.py/x").Should().BeNull();
        CircuitPythonFs.ReadFile(img, "lib").Should().BeNull();
    }

    [Fact]
    public void BuildVolume_MixedCaseAndSpaces_KeepOriginalNames()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("Code.py", B("a")), ("My File.TXT", B("b")), ("README", B("c"))));

        CircuitPythonFs.ListFiles(img).Should().BeEquivalentTo("Code.py", "My File.TXT", "README");
        CircuitPythonFs.ReadFile(img, "my file.txt").Should().Equal(B("b"));
    }

    [Fact]
    public void BuildVolume_SharedEightCharPrefixes_GetDistinctShortNames()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(
            ("adafruit_pioasm.py", B("1")),
            ("adafruit_pixelbuf.py", B("2")),
            ("adafruit_pio_uart.py", B("3"))));

        var shorts = new List<string>();
        for (int p = RootStart + 32; img[p] != 0; p += 32)
            if (img[p + 11] != 0x0F) shorts.Add(Encoding.ASCII.GetString(img, p, 11));

        shorts.Should().Equal("ADAFRU~1PY ", "ADAFRU~2PY ", "ADAFRU~3PY ");
        CircuitPythonFs.ReadFile(img, "adafruit_pioasm.py").Should().Equal(B("1"));
        CircuitPythonFs.ReadFile(img, "adafruit_pixelbuf.py").Should().Equal(B("2"));
        CircuitPythonFs.ReadFile(img, "adafruit_pio_uart.py").Should().Equal(B("3"));
    }

    [Fact]
    public void BuildVolume_LongName_WritesOrderedLfnSlotsWithChecksum()
    {
        const string name = "adafruit_pioasm.py";
        var img = CircuitPythonFs.BuildVolume(Size, Files((name, B("x"))));

        int first = RootStart + 32;
        int shortEntry = first + 64;
        img[first].Should().Be(0x42);
        img[first + 32].Should().Be(0x01);
        img[first + 11].Should().Be(0x0F);
        img[first + 32 + 11].Should().Be(0x0F);
        img[first + 13].Should().Be(Checksum(img, shortEntry));
        img[first + 32 + 13].Should().Be(Checksum(img, shortEntry));
        Encoding.ASCII.GetString(img, shortEntry, 11).Should().Be("ADAFRU~1PY ");

        var chars = new StringBuilder();
        foreach (int slot in new[] { first + 32, first })
            foreach (int o in LfnOffsets)
            {
                int c = img[slot + o] | (img[slot + o + 1] << 8);
                if (c == 0 || c == 0xFFFF) break;
                chars.Append((char)c);
            }
        chars.ToString().Should().Be(name);
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(13, 1)]
    [InlineData(14, 2)]
    [InlineData(26, 2)]
    [InlineData(27, 3)]
    public void BuildVolume_LfnTerminatorAndPadding_FollowTheSpec(int length, int slots)
    {
        string longName = new string('n', length);
        var img = CircuitPythonFs.BuildVolume(Size, Files((longName, B("x"))));

        int first = RootStart + 32;
        int usedSlots = 0;
        while (img[first + usedSlots * 32 + 11] == 0x0F) usedSlots++;
        usedSlots.Should().Be(slots);

        int lastSlot = first;
        int idxInLast = length - (slots - 1) * 13;
        if (idxInLast < 13)
        {
            int term = img[lastSlot + LfnOffsets[idxInLast]] | (img[lastSlot + LfnOffsets[idxInLast] + 1] << 8);
            term.Should().Be(0x0000);
            for (int j = idxInLast + 1; j < 13; j++)
                (img[lastSlot + LfnOffsets[j]] | (img[lastSlot + LfnOffsets[j] + 1] << 8)).Should().Be(0xFFFF);
        }
        CircuitPythonFs.ReadFile(img, longName).Should().Equal(B("x"));
    }

    [Fact]
    public void BuildVolume_DeepNesting_RoundTrips()
    {
        const string path = "lib/mypkg/sub/deeper/evenDeeper/deepest_module_name.py";
        var img = CircuitPythonFs.BuildVolume(Size, Files((path, B("W = 42")), ("lib/mypkg/__init__.py", B(""))));

        CircuitPythonFs.ReadFile(img, path).Should().Equal(B("W = 42"));
        CircuitPythonFs.ListFiles(img).Should().BeEquivalentTo(path, "lib/mypkg/__init__.py");
    }

    [Fact]
    public void BuildVolume_SubdirectoryNeedingSeveralClusters_RoundTrips()
    {
        var files = Enumerable.Range(0, 60)
            .Select(i => ($"lib/pkg/module_with_long_name_{i:D3}.py", B($"v = {i}")))
            .Append(("code.py", B("x")))
            .ToList();

        var img = CircuitPythonFs.BuildVolume(Size, files);

        CircuitPythonFs.ListFiles(img).Should().HaveCount(61);
        foreach (var (name, data) in files)
            CircuitPythonFs.ReadFile(img, name).Should().Equal(data, name);
    }

    [Fact]
    public void BuildVolume_SubdirectoryEntries_PointAtThemselvesAndParent()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("lib/pkg/mod.py", B("x"))));

        int lib = img[RootStart + 32 + 26];
        Encoding.ASCII.GetString(img, DataStart + (lib - 2) * 512, 11).Should().Be(".          ");
        (img[DataStart + (lib - 2) * 512 + 26]).Should().Be((byte)lib);
        Encoding.ASCII.GetString(img, DataStart + (lib - 2) * 512 + 32, 11).Should().Be("..         ");
        img[DataStart + (lib - 2) * 512 + 32 + 26].Should().Be(0);
        int pkg = img[DataStart + (lib - 2) * 512 + 64 + 26];
        img[DataStart + (pkg - 2) * 512 + 32 + 26].Should().Be((byte)lib);
    }

    [Fact]
    public void BuildVolume_RootEntryLimitIncludesLfnSlots()
    {
        var fits = Enumerable.Range(0, 511).Select(i => ($"f{i:D3}.py", B("x"))).ToList();
        var tooMany = Enumerable.Range(0, 512).Select(i => ($"f{i:D3}.py", B("x"))).ToList();
        var lfnTooMany = Enumerable.Range(0, 200).Select(i => ($"a_rather_long_name_{i:D3}.py", B("x"))).ToList();

        CircuitPythonFs.ListFiles(CircuitPythonFs.BuildVolume(Size, fits)).Should().HaveCount(511);
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, tooMany)).Should().Throw<InvalidOperationException>().WithMessage("*root*");
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, lfnTooMany)).Should().Throw<InvalidOperationException>().WithMessage("*root*");
    }

    [Fact]
    public void BuildVolume_ExceedingCapacity_Throws()
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files(("big.bin", new byte[Size]))))
            .Should().Throw<InvalidOperationException>().WithMessage("*fit*");
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files(("a.bin", new byte[600_000]), ("lib/b.bin", new byte[600_000]))))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/abs.py")]
    [InlineData("a//b.py")]
    [InlineData("a/")]
    [InlineData("./a.py")]
    [InlineData("a/../b.py")]
    [InlineData("..")]
    [InlineData("a:b.py")]
    [InlineData("a*.py")]
    [InlineData("a?.py")]
    [InlineData("a<b>.py")]
    [InlineData("a|b.py")]
    [InlineData("a\"b.py")]
    [InlineData("a\tb.py")]
    [InlineData("a\u0001.py")]
    public void BuildVolume_InvalidPath_ThrowsArgumentException(string path)
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files((path, B("x")))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildVolume_OverlongSegment_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files((new string('a', 256) + ".py", B("x")))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildVolume_BackslashSeparators_AreNormalized()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("lib\\pkg\\mod.py", B("x"))));

        CircuitPythonFs.ListFiles(img).Should().Equal("lib/pkg/mod.py");
    }

    [Theory]
    [InlineData("Code.py", "code.PY")]
    [InlineData("lib/a.py", "LIB/A.py")]
    public void BuildVolume_CaseInsensitiveDuplicates_ThrowArgumentException(string first, string second)
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files((first, B("1")), (second, B("2")))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildVolume_FileAndFolderWithSameName_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files(("lib", B("1")), ("lib/a.py", B("2")))))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolume(Size, Files(("lib/a.py", B("2")), ("lib", B("1")))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Bake_SplicesLongNamesIntoFlash()
    {
        var flash = new byte[2 * 1024 * 1024];

        CircuitPythonFs.Bake(flash, Files(("lib/adafruit_pioasm.py", B("Z = 1"))));

        CircuitPythonFs.ReadFile(flash, "lib/adafruit_pioasm.py").Should().Equal(B("Z = 1"));
        CircuitPythonFs.ListFiles(flash).Should().Equal("lib/adafruit_pioasm.py");
    }

    [Fact]
    public void ListRootEntries_StillReportsRootShortNames()
    {
        var img = CircuitPythonFs.BuildVolume(Size, Files(("code.py", B("x")), ("lib/a.py", B("y"))));

        CircuitPythonFs.ListRootEntries(img).Should().Equal("CODE.PY", "LIB");
    }

    [Fact]
    public void BuildVolumeFromDirectory_MapsTreeAndSkipsHiddenAndPycache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cpfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "lib", "adafruit_display_text"));
            Directory.CreateDirectory(Path.Combine(dir, "lib", "__pycache__"));
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, "code.py"), "print(1)");
            File.WriteAllText(Path.Combine(dir, ".DS_Store"), "junk");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "junk");
            File.WriteAllText(Path.Combine(dir, "lib", "__pycache__", "x.pyc"), "junk");
            File.WriteAllText(Path.Combine(dir, "lib", "adafruit_display_text", "label.py"), "L");
            File.WriteAllBytes(Path.Combine(dir, "lib", "driver.mpy"), new byte[] { 1, 2, 3 });

            var img = CircuitPythonFs.BuildVolumeFromDirectory(Size, dir);

            CircuitPythonFs.ListFiles(img).Should().BeEquivalentTo(
                "code.py", "lib/adafruit_display_text/label.py", "lib/driver.mpy");
            CircuitPythonFs.ReadFile(img, "lib/adafruit_display_text/label.py").Should().Equal(B("L"));
            CircuitPythonFs.ReadFile(img, "lib/driver.mpy").Should().Equal(new byte[] { 1, 2, 3 });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildVolumeFromDirectory_MissingDirectory_Throws()
    {
        FluentActions.Invoking(() => CircuitPythonFs.BuildVolumeFromDirectory(Size, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))))
            .Should().Throw<DirectoryNotFoundException>();
    }
}

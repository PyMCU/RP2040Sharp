using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>The C ABI at its boundary: handles, rejected arguments, UF2 families, time, pads.</summary>
public class ExportTests
{
    [Fact]
    public void Version_and_clock()
    {
        Assert.StartsWith("silicontwin-rp2040 ", Abi.Version());
        Assert.Equal(125_000_000u, Abi.ClockHz());
    }

    [Theory]
    [InlineData(1234u, 0u, "flash_bytes")]
    [InlineData(3u * 1024 * 1024, 0u, "flash_bytes")]
    [InlineData(32u * 1024 * 1024, 0u, "flash_bytes")]
    [InlineData(0u, 8u, "unknown flags")]
    [InlineData(0u, 0x80u, "unknown flags")]
    [InlineData(0u, 0x200u, "unknown flags")]
    public void Create_rejects_bad_arguments(uint flash, uint flags, string why)
    {
        Assert.Equal(0, Abi.CreateEx(flash, flags));
        Assert.Contains(why, Abi.LastError(0));
    }

    [Fact]
    public void Dead_handles_are_inert()
    {
        var h = Abi.Create(0);
        Assert.NotEqual(0, h);
        Assert.Equal(0, Abi.Status(h));
        Abi.Destroy(h);
        Abi.Destroy(h); // double destroy: no-op
        foreach (var dead in new nint[] { h, 0, 0x1234567, -1 })
        {
            Assert.Equal(2, Abi.Status(dead));
            Assert.NotNull(Abi.LastError(dead));
            Assert.Equal(0ul, Abi.Run(dead, 1000));
            Assert.Equal(0ul, Abi.RunUntilEdge(dead, 1000, 10, ~0u));
            Assert.Equal(0ul, Abi.Cycles(dead));
            Assert.Equal(0ul, Abi.TimePs(dead));
            Assert.Equal(0ul, Abi.CycleToPs(dead, 5));
            Assert.Equal(0ul, Abi.Instructions(dead, -1));
            Assert.Equal(0, Abi.PinGet(dead, 3, out var oe));
            Assert.Equal(0, oe);
            Assert.Equal(0, Abi.PinGetPull(dead, 3));
            Assert.Equal(-1, Abi.LoadUf2(dead, new byte[16]));
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(dead, ~0u));
            Abi.PinSetInput(dead, 3, 1);
            Abi.AdcSetVoltage(dead, 0, 1.0);
            Abi.SetFlashUniqueId(dead, new byte[8]);
            Abi.SerialWrite(dead, 2, "x");
            Abi.Destroy(dead);
        }
        Assert.Contains("invalid", Abi.LastError(h));
    }

    [Fact]
    public void Bad_arguments_are_rejected_with_a_reason()
    {
        var h = Abi.Create(0);
        try
        {
            Assert.Equal(0, Abi.PinGet(h, 30, out var oe));
            Assert.Equal(0, oe);
            Assert.Contains("out of range", Abi.LastError(h));
            Abi.PinSetInput(h, 3, 2);
            Assert.Contains("level 2", Abi.LastError(h));
            Abi.PinSetInput(h, -1, 1);
            Assert.Contains("gpio -1", Abi.LastError(h));
            Abi.AdcSetVoltage(h, 4, 1.0);
            Assert.Contains("channel 4", Abi.LastError(h));
            Abi.SerialWrite(h, 3, "x");
            Assert.Contains("invalid channel 3", Abi.LastError(h));
            Abi.SerialWrite(h, 2, null, 4);
            Assert.Contains("NULL", Abi.LastError(h));
            Assert.Equal(-1, Abi.LoadUf2(h, null));
            Assert.Contains("NULL", Abi.LastError(h));
            Abi.SetFlashUniqueId(h, null);
            Assert.Contains("NULL", Abi.LastError(h));
            Abi.SetFlashUniqueId(h, [1, 2, 3, 4, 5, 6, 7, 8]);
            Assert.Equal(0, Abi.Status(h)); // rejections never fault the chip
        }
        finally { Abi.Destroy(h); }
    }

    [Fact]
    public void Uf2_of_another_chip_is_rejected_and_rp2040_loads()
    {
        var h = Abi.Create(0);
        try
        {
            var image = Firmware.SquareWave(20, 37);
            Assert.Equal(0, Abi.LoadUf2(h, new byte[1024]));           // no UF2 blocks at all
            Assert.Equal(-1, Abi.LoadUf2(h, Firmware.Uf2(image, Firmware.FamilyRp2350Arm)));
            Assert.Contains("0xE48BFF59", Abi.LastError(h));
            Assert.Contains("not RP2040", Abi.LastError(h));
            Assert.Equal(0, Abi.Status(h));
            Assert.Equal(1, Abi.LoadUf2(h, Firmware.Uf2(image)));
            Abi.Run(h, 100);
            Assert.Equal(1, Abi.PinGet(h, 15, out var oe));
            Assert.Equal(1, oe);
            // A block past the end of a small flash does not fit.
            var small = Abi.Create(4096);
            try
            {
                Assert.Equal(-1, Abi.LoadUf2(small, Firmware.Uf2(new byte[8192])));
                Assert.Contains("does not fit", Abi.LastError(small));
            }
            finally { Abi.Destroy(small); }
        }
        finally { Abi.Destroy(h); }
    }

    [Fact]
    public void Time_is_cycles_at_125_MHz()
    {
        var h = Abi.Create(0);
        try
        {
            Assert.Equal(125_000_000u, Abi.ClkSysHz(h));
            Assert.Equal(1_000_000_000_000ul, Abi.CycleToPs(h, 125_000_000));
            Assert.Equal(8000ul, Abi.CycleToPs(h, 1));
            Assert.Equal(ulong.MaxValue, Abi.CyclesUntilEdge(h, ~0u));
            Abi.LoadUf2(h, Firmware.Uf2(Firmware.SquareWave(20, 37)));
            var c = Abi.Run(h, 10_000);
            Assert.True(c >= 10_000);
            Assert.Equal(c, Abi.Cycles(h));
            Assert.Equal(c * 8000, Abi.TimePs(h));
            Assert.Equal(10_000ul, Abi.Instructions(h, 0));
            Assert.Equal(0ul, Abi.Instructions(h, 1)); // core 1 never launched
        }
        finally { Abi.Destroy(h); }
    }

    [Fact]
    public void Released_input_reads_its_pull_and_a_driven_one_its_level()
    {
        var h = Abi.Create(0);
        try
        {
            var chip = Exports.Lookup(h)!;
            Assert.Equal(2, Abi.PinGetPull(h, 5)); // pads reset with the pull-down
            Abi.PinSetInput(h, 5, 1);
            Assert.NotEqual(0u, chip.Machine.Sio.GpioIn & 1u << 5);
            Abi.PinSetInput(h, 5, -1);
            Assert.Equal(0u, chip.Machine.Sio.GpioIn & 1u << 5);
            chip.Machine.PadsBank0.WriteWord(4u + 4u * 5, (0x56u & ~(1u << 2)) | 1u << 3); // PUE, no PDE
            Assert.Equal(1, Abi.PinGetPull(h, 5));
            Assert.NotEqual(0u, chip.Machine.Sio.GpioIn & 1u << 5);
        }
        finally { Abi.Destroy(h); }
    }

    [Fact]
    public void Run_from_a_callback_is_rejected_and_destroy_from_one_is_deferred()
    {
        var h = Abi.Create(0);
        using var host = new Host();
        try
        {
            Abi.LoadUf2(h, Firmware.Uf2(Firmware.SquareWave(20, 37)));
            Abi.Attach(h, host);
            host.OnPinHook = (_, _, _, _) => Abi.Run(h, 10);
            Abi.Run(h, 100);
            Assert.NotEmpty(host.Pins);
            Assert.Contains("re-entrantly", Abi.LastError(h));
            Assert.Equal(0, Abi.Status(h));

            host.OnPinHook = (_, _, _, _) => Abi.Destroy(h);
            Abi.RunUntilEdge(h, 200_000, 75, 1u << 15);
            Assert.Equal(2, Abi.Status(h));
        }
        finally { Abi.Destroy(h); }
    }

#if ST_ENABLE_TEST_HOOKS
    [Theory]
    [InlineData(1, "injected fault inside Run")]
    [InlineData(2, "NullReferenceException")]
    [InlineData(3, "injected fault inside ScanPads")]
    [InlineData(4, "injected fault in export")]
    public void Injected_faults_make_the_chip_inert(int mode, string message)
    {
        var h = Abi.Create(0);
        try
        {
            Abi.LoadUf2(h, Firmware.Uf2(Firmware.SquareWave(20, 37)));
            var before = Abi.Run(h, 1000);
            Assert.Equal(0, Abi.TestInjectFault(h, mode));
            var after = Abi.Run(h, 1000);
            Assert.Equal(1, Abi.Status(h));
            Assert.Contains(message, Abi.LastError(h));
            Assert.Contains("guest pc", Abi.LastError(h));
            Assert.Equal(after, Abi.Run(h, 1000)); // inert
            Assert.True(after >= before);
        }
        finally { Abi.Destroy(h); }
    }
#endif
}

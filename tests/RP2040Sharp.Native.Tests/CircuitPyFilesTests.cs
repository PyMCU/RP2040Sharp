using System.Text;
using SiliconTwin.Native;
using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// st_rp2040_fs_status / _fs_put / _fs_commit through the C ABI: argument checks on a blank chip, then CircuitPython
/// 9.2.1 for the Pico boots from blank flash and formats CIRCUITPY; once fs_status reports the volume with boot_out.txt
/// and the flash has been quiet for 100 ms, the host queues code.py and lib/x.py and commits with a reset, and the new
/// code.py runs and imports lib/x.
/// </summary>
public class CircuitPyFilesTests(ITestOutputHelper output)
{
    [Fact]
    public void Arguments_are_checked_and_a_commit_without_a_volume_fails_cleanly()
    {
        var h = Abi.Create(0);
        try
        {
            Assert.Equal(-1, Abi.FsStatus(h, out _, size: 7));
            Assert.Equal(0, Abi.FsStatus(h, out var st));
            Assert.Equal(48u, st.Size);
            Assert.Equal(0u, st.FatBits);
            Assert.Equal(-1, Abi.FsPut(h, null, [1]));
            Assert.Equal(-1, Abi.FsPut(h, "a//b.py", [1]));
            Assert.Contains("empty name", Abi.LastError(h));
            Assert.Equal(-1, Abi.FsPut(h, new string('n', 300), [1]));
            Assert.Contains("too long", Abi.LastError(h));
            Assert.Equal(0, Abi.FsPut(h, "code.py", "print(1)"u8.ToArray()));
            Assert.Equal(0, Abi.FsPut(h, "CODE.PY", "print(2)"u8.ToArray()));
            Assert.Equal(0, Abi.FsStatus(h, out st));
            Assert.Equal(1u, st.StagedFiles);
            Assert.Equal(-1, Abi.FsCommit(h, 1));
            Assert.Contains("no FAT volume", Abi.LastError(h));
            Assert.Equal(0, Abi.FsStatus(h, out st));
            Assert.Equal(0u, st.StagedFiles);
            Assert.Equal(0, Abi.Status(h));
        }
        finally { Abi.Destroy(h); }
        Assert.Equal(-1, Abi.FsCommit(h, 0));   // destroyed handle
    }

    [SkippableFact]
    public void A_host_puts_code_py_and_lib_x_py_and_the_rebooted_board_runs_them()
    {
        var uf2 = Firmware.CircuitPythonPico();
        Skip.If(uf2 is null, "CircuitPython 9.2.1 (raspberry_pi_pico) UF2 not found (set RP2040_CIRCUITPYTHON_UF2)");
        var h = Abi.Create(0);
        var host = new Host();
        try
        {
            Abi.Attach(h, host);
            Assert.True(Abi.LoadUf2(h, File.ReadAllBytes(uf2!)) > 1000);
            Assert.Equal(0, Abi.FsPut(h, "code.py", Encoding.ASCII.GetBytes(
                "import supervisor, time\nt = time.monotonic()\n" +
                "while not supervisor.runtime.serial_connected and time.monotonic() - t < 5:\n    pass\n" +
                "import x\nprint('ABI-PUT', x.VALUE * 6)\n")));
            Assert.Equal(0, Abi.FsPut(h, "lib/x.py", "VALUE = 7\n"u8.ToArray()));

            FsStatus st;
            ulong cycles = 0, quietSince = 0, lastWrites = ulong.MaxValue;
            for (;;)
            {
                cycles = Abi.Run(h, 1_000_000);
                Assert.True(Abi.Status(h) == 0, Abi.LastError(h));
                Assert.True(cycles < 125_000_000UL * 15, "CIRCUITPY never formatted: " + host.Cdc);
                int found = Abi.FsStatus(h, out st);
                ulong writes = st.FlashErases + st.FlashProgrammed;
                if (writes != lastWrites) { lastWrites = writes; quietSince = cycles; continue; }
                if (found == 1 && (st.Flags & FsStatus.BootOut) != 0 && cycles - quietSince >= 12_500_000) break;
            }
            output.WriteLine($"fs_status: FAT{st.FatBits} at 0x{st.VolumeOffset:X}, {st.VolumeBytes} B, cluster {st.ClusterBytes}, " +
                             $"{st.FreeBytes} B free, {st.StagedFiles} queued, at {cycles / 125_000.0:F0} ms");
            Assert.Equal(0x100000u, st.VolumeOffset);   // CircuitPython's CIRCUITPY on the 2 MiB Pico, found by the search

            Assert.Equal(0, Abi.FsCommit(h, 1));
            int from = host.Cdc.Length;
            for (ulong end = cycles + 125_000_000UL * 15; cycles < end && host.Cdc.IndexOf("ABI-PUT 42", from, StringComparison.Ordinal) < 0; )
                cycles = Abi.Run(h, 1_000_000);
            output.WriteLine(host.Cdc[from..]);
            Assert.Contains("ABI-PUT 42", host.Cdc[from..]);
        }
        finally { Abi.Destroy(h); host.Dispose(); }
    }
}

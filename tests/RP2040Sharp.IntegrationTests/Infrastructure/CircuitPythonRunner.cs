using RP2040.Peripherals.Fs;
using RP2040.TestKit;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Probes;

namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// High-level runner that boots CircuitPython on the RP2040 emulator and exposes a REPL
/// interface for driving tests via UART injection.
///
/// CircuitPython routes its REPL through USB-CDC (TinyUSB) when USB is available,
/// falling back to the hardware UART otherwise.  The runner detects which transport
/// the REPL prompt appears on and routes all subsequent I/O through the same channel.
///
/// Usage:
/// <code>
/// await using var cp = await CircuitPythonRunner.CreateAsync("9.2.1");
/// cp.Should().NotBeNull("firmware should be available");
///
/// bool booted = cp.WaitForPrompt();
/// booted.Should().BeTrue();
///
/// cp.Execute("print('hello')");
/// cp.WaitForOutput("hello").Should().BeTrue();
/// </code>
/// </summary>
public sealed class CircuitPythonRunner : IAsyncDisposable
{
    private readonly PicoSimulation _sim;

    // CircuitPython also routes its REPL through USB-CDC when available.
    private bool _replViaUsbCdc;

    public UartProbe Uart    => _sim.Uart0;
    public UsbCdcProbe UsbCdc => _sim.UsbCdc;
    public PicoSimulation Simulation => _sim;

    private CircuitPythonRunner(PicoSimulation sim)
    {
        _sim = sim;
    }

    /// <summary>
    /// Create a runner loaded with CircuitPython <paramref name="version"/> (e.g. "9.2.1").
    /// Returns <c>null</c> when the firmware is not available (no network / not cached).
    /// </summary>
    /// <param name="version">CircuitPython version string.</param>
    /// <param name="withUsbCdc">
    /// When <c>true</c> (default) the simulation includes a USB-CDC host, giving access to
    /// the USB REPL but also causing CircuitPython to lock the CIRCUITPY filesystem
    /// read-only (USB-MSC prevents Python code from writing).
    /// Pass <c>false</c> to run without a USB host: CircuitPython falls back to the
    /// UART0 REPL and the filesystem remains writable from Python code.
    /// </param>
    public static async Task<CircuitPythonRunner?> CreateAsync(string version, bool withUsbCdc = true)
    {
        var uf2Path = await FirmwareCache.GetCircuitPythonAsync(version);
        if (uf2Path is null)
            return null;

        var uf2Bytes = await File.ReadAllBytesAsync(uf2Path);
        var flashImage = Uf2Reader.ToFlashImage(uf2Bytes);

        var sim = new PicoSimulation(withUsbCdc);
        sim.LoadFlash(flashImage);
        return new CircuitPythonRunner(sim);
    }

    public static async Task<CircuitPythonRunner?> CreateWithFilesAsync(
        string version, IReadOnlyList<(string Name, byte[] Data)> files, bool withUsbCdc = true)
    {
        var uf2Path = await FirmwareCache.GetCircuitPythonAsync(version);
        if (uf2Path is null)
            return null;

        var flashImage = Uf2Reader.ToFlashImage(await File.ReadAllBytesAsync(uf2Path));
        Array.Resize(ref flashImage, Math.Max(flashImage.Length, CircuitPythonFs.DefaultRegionOffset + CircuitPythonFs.DefaultRegionSize));
        CircuitPythonFs.Bake(flashImage, files);

        var sim = new PicoSimulation(withUsbCdc);
        sim.LoadFlash(flashImage);
        return new CircuitPythonRunner(sim);
    }

    // ── REPL helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Run the simulation until the CircuitPython REPL prompt (<c>&gt;&gt;&gt; </c>) appears on
    /// UART or USB-CDC, or until <paramref name="timeoutMs"/> elapses.
    /// CircuitPython may also emit a "Press any key to enter the REPL" message before the
    /// prompt; this helper sends a key press automatically when that message is detected.
    /// </summary>
    public bool WaitForPrompt(double timeoutMs = 20_000)
    {
        const double batchMs = 100.0;
        var elapsed = 0.0;
        var keySent = false;
        while (elapsed < timeoutMs)
        {
            _sim.RunMilliseconds(batchMs);
            elapsed += batchMs;

            // CircuitPython may ask for a keypress to enter the REPL when no code.py exists.
            // Only send it once — the text is accumulated so "Press any key" stays visible
            // across iterations and we must not flood the fifo with extra keypresses.
            if (!keySent)
            {
                if (UsbCdc.Text.Contains("Press any key", StringComparison.OrdinalIgnoreCase))
                {
                    UsbCdc.InjectString("\r");
                    keySent = true;
                }
                else if (Uart.Text.Contains("Press any key", StringComparison.OrdinalIgnoreCase))
                {
                    Uart.InjectString("\r");
                    keySent = true;
                }
            }

            if (Uart.Text.Contains(">>> ", StringComparison.Ordinal))
            {
                _replViaUsbCdc = false;
                // Run one extra batch so any pending USB endpoint reads that
                // accumulated during the wait are drained before the caller
                // injects the next command.  Without this drain, the first
                // Execute() after WaitForPrompt can be partially swallowed by
                // a stale ZLP→re-arm cycle in the USB peripheral.
                _sim.RunMilliseconds(100);
                return true;
            }
            if (UsbCdc.Text.Contains(">>> ", StringComparison.Ordinal))
            {
                _replViaUsbCdc = true;
                _sim.RunMilliseconds(100); // same drain on the CDC path
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Inject a line of Python code into the REPL (appends <c>\r\n</c>).
    /// Call <see cref="WaitForPrompt"/> first to ensure the REPL is ready.
    /// </summary>
    public void Execute(string pythonLine)
    {
        if (_replViaUsbCdc)
        {
            UsbCdc.Clear();
            UsbCdc.InjectString(pythonLine + "\r\n");
        }
        else
        {
            Uart.Clear();
            Uart.InjectString(pythonLine + "\r\n");
        }
    }

    /// <summary>
    /// Run the simulation until <paramref name="expectedText"/> appears in the output
    /// captured since the last <see cref="Execute"/> call.
    /// </summary>
    public bool WaitForOutput(string expectedText, double timeoutMs = 5_000) =>
        _replViaUsbCdc
            ? _sim.RunUntilOutput(UsbCdc, expectedText, timeoutMs)
            : _sim.RunUntilOutput(Uart, expectedText, timeoutMs);

    /// <summary>
    /// Inject a Python line and wait for <paramref name="expectedOutput"/>.
    /// Returns <c>true</c> if the expected text appeared before the timeout.
    /// </summary>
    public bool ExecuteAndWait(string pythonLine, string expectedOutput, double timeoutMs = 5_000)
    {
        Execute(pythonLine);
        return WaitForOutput(expectedOutput, timeoutMs);
    }

    /// <summary>
    /// Run simulation in batches until <paramref name="predicate"/> over the output text returns true.
    /// </summary>
    public bool WaitForOutput(Func<string, bool> predicate, double timeoutMs = 5_000) =>
        _replViaUsbCdc
            ? _sim.RunUntilOutput(UsbCdc, predicate, timeoutMs)
            : _sim.RunUntilOutput(Uart, predicate, timeoutMs);

    /// <summary>
    /// Inject a compound statement (<c>def</c>, <c>for</c>, <c>class</c>, <c>if</c>, etc.) into
    /// the REPL.  Sends the statement, waits for the <c>... </c> continuation prompt, then sends
    /// a blank line to execute it, and finally waits for the next <c>&gt;&gt;&gt; </c> prompt.
    /// Returns <c>true</c> if the REPL prompt was seen before the timeout.
    /// </summary>
    public bool ExecuteCompound(string pythonLine, double timeoutMs = 15_000)
    {
        if (_replViaUsbCdc)
        {
            UsbCdc.Clear();
            UsbCdc.InjectString(pythonLine + "\r\n");
        }
        else
        {
            Uart.Clear();
            Uart.InjectString(pythonLine + "\r\n");
        }

        const double batchMs = 100.0;
        var elapsed = 0.0;
        var gotContinuation = false;
        while (elapsed < timeoutMs)
        {
            _sim.RunMilliseconds(batchMs);
            var text = _replViaUsbCdc ? UsbCdc.Text : Uart.Text;
            if (text.Contains(">>> ", StringComparison.Ordinal)) return true;
            if (text.Contains("... ", StringComparison.Ordinal)) { gotContinuation = true; break; }
            elapsed += batchMs;
        }

        if (!gotContinuation) return false;

        if (_replViaUsbCdc)
            UsbCdc.InjectString("\r\n");
        else
            Uart.InjectString("\r\n");

        return WaitForPrompt(timeoutMs - elapsed);
    }

    // ── Filesystem helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> on the CircuitPython
    /// virtual filesystem (FAT, exposed as the <c>CIRCUITPY</c> drive, mounted after boot).
    ///
    /// The primary entry point for CircuitPython is <c>code.py</c> (with <c>main.py</c>,
    /// <c>code.txt</c>, and <c>main.txt</c> as fall-backs in that order).
    ///
    /// The file is written via REPL injection.  Call <see cref="WaitForPrompt"/> first
    /// to ensure the REPL is ready.  Content is sent in chunks of 150 escaped characters
    /// so it always fits within the REPL line buffer.
    /// </summary>
    /// <returns><c>true</c> if the file was written successfully before the timeout.</returns>
    public bool WriteFile(string path, string content, double timeoutMs = 5_000)
    {
        const int chunkSize = 150;

        var escapedPath    = EscapePythonString(path);
        var escapedContent = EscapePythonString(content);

        if (!ExecuteAndWait($"_wf=open('{escapedPath}','w')", ">>> ", timeoutMs))
            return false;

        var pos = 0;
        while (pos < escapedContent.Length)
        {
            var end = Math.Min(pos + chunkSize, escapedContent.Length);

            // Don't split in the middle of a \-escape sequence.
            while (end > pos + 1)
            {
                var slashes = 0;
                for (var k = end - 1; k >= pos && escapedContent[k] == '\\'; k--)
                    slashes++;
                if (slashes % 2 == 0) break;
                end--;
            }

            var chunk = escapedContent.Substring(pos, end - pos);
            if (!ExecuteAndWait($"_wf.write('{chunk}')", ">>> ", timeoutMs))
                return false;

            pos = end;
        }

        return ExecuteAndWait("_wf.close()", ">>> ", timeoutMs);
    }

    /// <summary>
    /// Perform a CircuitPython soft reset (CTRL+D).  The VM re-runs <c>code.py</c>
    /// (or the first available fall-back: <c>main.py</c>, <c>code.txt</c>,
    /// <c>main.txt</c>).  Any output is captured in <see cref="UsbCdc"/> /
    /// <see cref="Uart"/>.  Returns <c>true</c> when the <c>&gt;&gt;&gt;&nbsp;</c>
    /// prompt reappears within <paramref name="timeoutMs"/>.
    /// </summary>
    public bool SoftReset(double timeoutMs = 20_000)
    {
        if (_replViaUsbCdc)
        {
            UsbCdc.Clear();
            UsbCdc.InjectString("\x04");
        }
        else
        {
            Uart.Clear();
            Uart.InjectString("\x04");
        }
        return WaitForPrompt(timeoutMs);
    }

    // ── Writable-FS factory ───────────────────────────────────────────────────

    /// <summary>
    /// Create a runner where the CircuitPython filesystem is writable from Python code.
    ///
    /// Strategy (two-phase):
    /// <list type="number">
    ///   <item>Boot CircuitPython once so it initialises (or mounts) the FAT filesystem.</item>
    ///   <item>Copy the full 2 MB flash image, inject a <c>boot.py</c> into the copy,
    ///         then restart with the modified image.</item>
    /// </list>
    /// <c>boot.py</c> runs on every <em>hard reset</em> (power-up / <c>LoadFlash</c>),
    /// before TinyUSB is initialised.  Calling <c>storage.disable_usb_drive()</c> there
    /// suppresses the USB-MSC descriptor and leaves the CIRCUITPY drive writable from
    /// Python code.
    ///
    /// Note: a soft reset (CTRL-D) does NOT re-run <c>boot.py</c>; that is why the
    /// second phase creates a fresh simulation rather than calling <c>SoftReset()</c>.
    /// </summary>
    public static async Task<CircuitPythonRunner?> CreateWithWritableFsAsync(string version)
    {
        // ── Phase 1: boot once so the FAT is initialised ──────────────────────
        var stage1 = await CreateAsync(version);
        if (stage1 is null) return null;

        if (!stage1.WaitForPrompt(timeoutMs: 20_000))
        {
            await stage1.DisposeAsync();
            return null;
        }

        // ── Phase 2: snapshot flash, inject boot.py, discard first simulation ─
        var flashSize = (int)stage1.Simulation.Rp2040.Bus.FlashSize;
        var fullFlash = new byte[flashSize];
        unsafe
        {
            new ReadOnlySpan<byte>(stage1.Simulation.Rp2040.Bus.PtrFlash, flashSize)
                .CopyTo(fullFlash);
            fixed (byte* ptr = fullFlash)
                InjectBootPy(ptr, "import storage\nstorage.disable_usb_drive()\n");
        }
        await stage1.DisposeAsync();

        // ── Phase 3: restart with modified flash ──────────────────────────────
        // boot.py is run on the very first hard reset, before TinyUSB initialises,
        // so storage.disable_usb_drive() takes effect and the FS is writable.
        var sim2   = new PicoSimulation(withUsbCdc: true);
        sim2.LoadFlash(fullFlash);
        var runner = new CircuitPythonRunner(sim2);

        if (!runner.WaitForPrompt(timeoutMs: 20_000))
            return null;

        runner.Simulation.RunMilliseconds(200);
        runner.UsbCdc.Clear();
        return runner;
    }

    // ── FAT12 boot.py injection ───────────────────────────────────────────────

    /// <summary>
    /// Writes a <c>boot.py</c> file into the CIRCUITPY FAT12 partition inside a raw
    /// flash image.  Works on the raw <c>byte[]</c> (via a pinned pointer) or on the
    /// live emulated flash — either way <paramref name="flashPtr"/> must point to
    /// the base of the 2 MB flash image.
    /// </summary>
    private static unsafe void InjectBootPy(byte* flashPtr, string content)
    {
        // BPB parameters discovered empirically from a live CircuitPython 9.2.1 image.
        const uint FatFlashOffset = 0x100000u; // CIRCUITPY FAT partition at 1 MB in flash
        const int  Bps            = 512;       // BPB_BytsPerSec
        const int  Spc            = 1;         // BPB_SecPerClus
        const int  RsvdSectors    = 1;         // BPB_RsvdSecCnt
        const int  NumFats        = 1;         // BPB_NumFATs
        const int  SectorsPerFat  = 7;         // BPB_FATSz16
        const int  RootDirEntries = 512;       // BPB_RootEntCnt

        // Sector layout (all offsets relative to the start of the FAT partition):
        //   Sector  1 : FAT1  (7 sectors)
        //   Sector  8 : root directory  (512 entries × 32 B / 512 B/sector = 32 sectors)
        //   Sector 40 : data area (cluster 2 = first data cluster)
        const int FatSector  = RsvdSectors;
        const int RootSector = RsvdSectors + NumFats * SectorsPerFat;
        const int DataSector = RootSector  + RootDirEntries * 32 / Bps;

        byte* bpb  = flashPtr + FatFlashOffset;
        byte* fat  = bpb + FatSector  * Bps;
        byte* root = bpb + RootSector * Bps;
        byte* data = bpb + DataSector * Bps;

        // Find the first free cluster (FAT12 allocatable entries start at cluster 2).
        int freeClu = -1;
        for (int clu = 2; clu < 2048; clu++)
        {
            if (Fat12Get(fat, clu) == 0x000u) { freeClu = clu; break; }
        }
        if (freeClu < 0) return; // FAT full — cannot inject

        // Write file content into the allocated cluster (clear first, then copy).
        byte[] contentBytes = System.Text.Encoding.ASCII.GetBytes(content);
        byte*  clusterData  = data + (freeClu - 2) * Spc * Bps;
        new Span<byte>(clusterData, Spc * Bps).Clear();
        contentBytes.AsSpan().CopyTo(new Span<byte>(clusterData, contentBytes.Length));

        // Mark cluster as end-of-chain in FAT12.
        Fat12Set(fat, freeClu, 0xFFFu);

        // Add a root-directory entry for BOOT.PY (8.3 short name).
        for (int e = 0; e < RootDirEntries - 1; e++)
        {
            byte* entry    = root + e * 32;
            bool  isEnd     = entry[0] == 0x00;
            bool  isDeleted = entry[0] == 0xE5;
            if (!isEnd && !isDeleted) continue;

            // 8.3 name: "BOOT    " + "PY "
            "BOOT    "u8.CopyTo(new Span<byte>(entry,     8));
            "PY "u8.CopyTo(new Span<byte>(entry + 8,  3));
            entry[11] = 0x20;                               // ATTR_ARCHIVE
            new Span<byte>(entry + 12, 14).Clear();         // reserved / time / date
            *(ushort*)(entry + 26) = (ushort)freeClu;       // first cluster (low word)
            *(uint*)  (entry + 28) = (uint)contentBytes.Length; // file size

            // When overwriting the end-of-directory marker the next slot must also
            // be marked as end-of-directory (entry+32 is the first byte of entry e+1).
            if (isEnd)
                *(entry + 32) = 0x00;

            break;
        }
    }

    private static unsafe uint Fat12Get(byte* fat, int cluster)
    {
        int  byteOff = cluster * 3 / 2;
        uint raw     = (uint)fat[byteOff] | ((uint)fat[byteOff + 1] << 8);
        return (cluster & 1) == 0 ? raw & 0xFFFu : (raw >> 4) & 0xFFFu;
    }

    private static unsafe void Fat12Set(byte* fat, int cluster, uint value)
    {
        int byteOff = cluster * 3 / 2;
        if ((cluster & 1) == 0)
        {
            fat[byteOff]     = (byte)(value & 0xFF);
            fat[byteOff + 1] = (byte)(((uint)fat[byteOff + 1] & 0xF0u) | ((value >> 8) & 0x0Fu));
        }
        else
        {
            fat[byteOff]     = (byte)(((uint)fat[byteOff] & 0x0Fu) | ((value << 4) & 0xF0u));
            fat[byteOff + 1] = (byte)((value >> 4) & 0xFF);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string EscapePythonString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        sb.Append($"\\x{(int)c:x2}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _sim.Dispose();
        return ValueTask.CompletedTask;
    }
}

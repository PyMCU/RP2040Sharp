using RP2040.TestKit;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Probes;

namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// High-level runner that boots MicroPython on the RP2040 emulator and exposes a REPL
/// interface for driving tests via UART injection.
///
/// Usage:
/// <code>
/// await using var mp = await MicroPythonRunner.CreateAsync("v1.21.0");
/// mp.Should().NotBeNull("firmware should be available");
///
/// bool booted = mp.WaitForPrompt();
/// booted.Should().BeTrue();
///
/// mp.Execute("print('hello')");
/// mp.WaitForOutput("hello").Should().BeTrue();
/// </code>
/// </summary>
public sealed class MicroPythonRunner : IAsyncDisposable
{
    private readonly PicoSimulation _sim;

    // MicroPython routes its REPL through USB-CDC (TinyUSB) when USB is available.
    // Track which transport the REPL prompt appeared on so Execute/WaitForOutput
    // use the correct channel.
    private bool _replViaUsbCdc;

    public UartProbe Uart => _sim.Uart0;
    public UsbCdcProbe UsbCdc => _sim.UsbCdc;
    public PicoSimulation Simulation => _sim;

    private MicroPythonRunner(PicoSimulation sim)
    {
        _sim = sim;
    }

    /// <summary>
    /// Create a runner loaded with MicroPython <paramref name="version"/>.
    /// Returns <c>null</c> when the firmware is not available (no network / not cached).
    /// </summary>
    public static async Task<MicroPythonRunner?> CreateAsync(string version, bool picoW = false)
    {
        var uf2Path = picoW ? await FirmwareCache.GetMicroPythonPicoWAsync(version) : await FirmwareCache.GetMicroPythonAsync(version);
        if (uf2Path is null)
            return null;

        var uf2Bytes = await File.ReadAllBytesAsync(uf2Path);
        var flashImage = Uf2Reader.ToFlashImage(uf2Bytes);

        var sim = new PicoSimulation();
        sim.LoadFlash(flashImage);
        return new MicroPythonRunner(sim);
    }

    // ── REPL helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Run the simulation until the MicroPython REPL prompt (<c>&gt;&gt;&gt; </c>) appears on UART
    /// or USB-CDC, or until <paramref name="timeoutMs"/> elapses.
    /// </summary>
    public bool WaitForPrompt(double timeoutMs = 15_000)
    {
        const double batchMs = 100.0;
        var elapsed = 0.0;
        while (elapsed < timeoutMs)
        {
            _sim.RunMilliseconds(batchMs);
            if (Uart.Text.Contains(">>> ", StringComparison.Ordinal))
            {
                _replViaUsbCdc = false;
                return true;
            }
            if (UsbCdc.Text.Contains(">>> ", StringComparison.Ordinal))
            {
                _replViaUsbCdc = true;
                return true;
            }
            elapsed += batchMs;
        }
        return false;
    }

    /// <summary>
    /// Inject a line of Python code into the REPL (appends <c>\r\n</c>).
    /// Call <see cref="WaitForPrompt"/> first to ensure the REPL is ready.
    /// Routes the injection to the transport where the REPL was detected (UART or USB-CDC).
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
            _sim.Uart0.InjectString(pythonLine + "\r\n");
        }
    }

    /// <summary>
    /// Run the simulation until <paramref name="expectedText"/> appears in the output
    /// captured since the last <see cref="Execute"/> call.
    /// Uses the same transport (UART or USB-CDC) where the REPL was detected.
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
    /// Uses the same transport (UART or USB-CDC) where the REPL was detected.
    /// </summary>
    public bool WaitForOutput(Func<string, bool> predicate, double timeoutMs = 5_000) =>
        _replViaUsbCdc
            ? _sim.RunUntilOutput(UsbCdc, predicate, timeoutMs)
            : _sim.RunUntilOutput(Uart, predicate, timeoutMs);

    /// <summary>
    /// Inject a compound statement (<c>def</c>, <c>for</c>, <c>class</c>, <c>if</c>, etc.) into
    /// the REPL.  Sends the statement, waits for the <c>... </c> continuation prompt, then sends
    /// a blank line to execute it, and finally waits for the next <c>>>> </c> prompt.
    /// Returns <c>true</c> if the REPL prompt was seen before the timeout.
    /// </summary>
    public bool ExecuteCompound(string pythonLine, double timeoutMs = 15_000)
    {
        // Inject the first line (opens the compound statement)
        if (_replViaUsbCdc)
        {
            UsbCdc.Clear();
            UsbCdc.InjectString(pythonLine + "\r\n");
        }
        else
        {
            Uart.Clear();
            _sim.Uart0.InjectString(pythonLine + "\r\n");
        }

        // Wait for the continuation prompt ("... ") or an immediate ">>> " (single-line executed)
        const double batchMs = 100.0;
        var elapsed = 0.0;
        var gotContinuation = false;
        while (elapsed < timeoutMs)
        {
            _sim.RunMilliseconds(batchMs);
            var text = _replViaUsbCdc ? UsbCdc.Text : Uart.Text;
            if (text.Contains(">>> ", StringComparison.Ordinal)) return true; // executed immediately
            if (text.Contains("... ", StringComparison.Ordinal)) { gotContinuation = true; break; }
            elapsed += batchMs;
        }

        if (!gotContinuation) return false;

        // Send a blank line to close the compound block
        if (_replViaUsbCdc)
            UsbCdc.InjectString("\r\n");
        else
            _sim.Uart0.InjectString("\r\n");

        // Wait for the final ">>> " prompt confirming execution
        return WaitForPrompt(timeoutMs - elapsed);
    }

    // ── Filesystem helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> on the MicroPython
    /// virtual filesystem (LittleFS, mounted after boot).
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

        // Open the file (use a deliberately odd name to avoid clobbering user variables)
        if (!ExecuteAndWait($"_wf=open('{escapedPath}','w')", ">>> ", timeoutMs))
            return false;

        // Write content in 150-char chunks (safe for any REPL line-buffer size)
        var pos = 0;
        while (pos < escapedContent.Length)
        {
            var end = Math.Min(pos + chunkSize, escapedContent.Length);

            // Don't split in the middle of a \-escape sequence:
            // an ODD run of trailing backslashes means the last one starts an escape.
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
    /// Perform a MicroPython soft reset (CTRL+D).  The VM re-runs <c>boot.py</c> then
    /// <c>main.py</c> if they exist; any output is captured in <see cref="UsbCdc"/> /
    /// <see cref="Uart"/>.  Returns <c>true</c> when the <c>&gt;&gt;&gt;&nbsp;</c> prompt
    /// reappears within <paramref name="timeoutMs"/>.
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Escape <paramref name="s"/> so it is safe to embed inside a Python single-quoted
    /// string literal (e.g. <c>f.write('…')</c>).
    /// </summary>
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

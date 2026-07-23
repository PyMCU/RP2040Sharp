// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
using System.Diagnostics;
using System.Globalization;
using RP2040.Gdb;
using RP2040.Peripherals;
using RP2040.Peripherals.Usb;
using RP2040Sharp.GdbHost;

const int ExitOk = 0, ExitUsage = 64, ExitNoInput = 66;

string? imagePath = null, elfPath = null;
var port = 3333;
var channel = "uart";
var startRunning = false;
long? diagnoseAfter = null;
var deployments = new List<string>();
var peInputs = new List<string>();
string? emitDeployment = null;
var injections = new List<(byte[] Payload, uint Address, string Label)>();

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a)
    {
        case "-h" or "--help":
            PrintUsage();
            return ExitOk;
        case "--port":
            if (++i >= args.Length || !int.TryParse(args[i], out port)) return Usage("--port requires a number");
            break;
        case "--elf":
            if (++i >= args.Length) return Usage("--elf requires a path");
            elfPath = args[i];
            break;
        case "--channel":
            if (++i >= args.Length) return Usage("--channel requires a value");
            channel = args[i].ToLowerInvariant();
            if (channel is not ("uart" or "usb" or "none")) return Usage($"unknown channel '{channel}' (uart|usb|none)");
            break;
        case "--run":
            startRunning = true;
            break;
        case "--deploy":
            if (++i >= args.Length) return Usage("--deploy requires <file>[@<hex-addr>]");
            deployments.Add(args[i]);
            break;
        case "--pe":
            if (++i >= args.Length) return Usage("--pe requires a .pe file or a directory");
            peInputs.Add(args[i]);
            break;
        case "--emit-deployment":
            if (++i >= args.Length) return Usage("--emit-deployment requires an output path");
            emitDeployment = args[i];
            break;
        case "--diagnose":
            if (++i >= args.Length || !long.TryParse(args[i], out var n)) return Usage("--diagnose requires an instruction count");
            diagnoseAfter = n;
            break;
        default:
            if (a.StartsWith('-')) return Usage($"unknown option '{a}'");
            if (imagePath != null) return Usage("more than one image given");
            imagePath = a;
            break;
    }
}

if (imagePath is null) return Usage("no firmware image given");
if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"error: image not found: {imagePath}");
    return ExitNoInput;
}

#region Machine

var bytes = File.ReadAllBytes(imagePath);
var machine = new RP2040Machine();
var flash = RP2040Machine.Uf2ToFlash(bytes) ?? bytes;   // Uf2ToFlash returns null for raw .bin

// --deploy lets you drop a managed deployment (concatenated .pe files) straight into the
// flash image, which is how you get an application running without the wire protocol.
// The RP2040 deployment region starts at 0x100FC000 — the value derived from sector 252 in
// the target's Device_BlockStorage.c, not the stale 0x10100000 in that target's README.
// --pe assembles a deployment image from compiled assemblies, so nobody has to concatenate
// and pad .pe files by hand. A directory contributes every .pe inside it.
if (peInputs.Count > 0)
{
    var paths = new List<string>();
    foreach (var input in peInputs)
    {
        if (Directory.Exists(input))
            paths.AddRange(Directory.GetFiles(input, "*.pe"));
        else if (File.Exists(input))
            paths.Add(input);
        else
        {
            Console.Error.WriteLine($"error: no such file or directory: {input}");
            return ExitNoInput;
        }
    }

    if (paths.Count == 0)
    {
        Console.Error.WriteLine("error: --pe matched no .pe files");
        return ExitNoInput;
    }

    List<DeploymentBuilder.Assembly> assemblies;
    try
    {
        assemblies = DeploymentBuilder.Order(paths).Select(DeploymentBuilder.Load).ToList();
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return ExitUsage;
    }

    var deploymentImage = DeploymentBuilder.Build(assemblies);

    foreach (var a in assemblies)
        Console.Error.WriteLine($"  assembly {a.Name} ({a.TotalSize:N0} bytes)");
    Console.Error.WriteLine($"deployment image: {deploymentImage.Length:N0} bytes from {assemblies.Count} assemblies");

    if (emitDeployment is not null)
    {
        File.WriteAllBytes(emitDeployment, deploymentImage);
        Console.Error.WriteLine($"wrote {emitDeployment}");
    }

    injections.Add((deploymentImage, DeploymentBuilder.DeploymentAddress, "deployment"));
}

foreach (var spec in deployments)
{
    var at = spec.LastIndexOf('@');
    var path = at < 0 ? spec : spec[..at];
    var addr = DeploymentBuilder.DeploymentAddress;

    if (at >= 0 && !TryParseAddr(spec[(at + 1)..], out addr))
        return Usage($"bad address in '{spec}'");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"error: deployment not found: {path}");
        return ExitNoInput;
    }

    injections.Add((File.ReadAllBytes(path), addr, Path.GetFileName(path)));
}

if (injections.Count > 0)
{
    const uint FlashBase = 0x10000000, FlashSize = 2 * 1024 * 1024;

    // Erased flash reads 0xFF, and the CLR relies on that to know where the assemblies end.
    var image = new byte[FlashSize];
    Array.Fill(image, (byte)0xFF);
    flash.AsSpan(0, Math.Min(flash.Length, image.Length)).CopyTo(image);

    foreach (var (payload, addr, label) in injections)
    {
        var offset = addr - FlashBase;
        if (offset + payload.Length > FlashSize)
        {
            Console.Error.WriteLine($"error: {label} at 0x{addr:X8} runs past the end of flash");
            return ExitUsage;
        }

        payload.CopyTo(image, (int)offset);
        Console.Error.WriteLine($"injected {label} ({payload.Length:N0} bytes) at 0x{addr:X8}");
    }

    flash = image;
}

machine.LoadFlash(flash);

if (channel == "uart")
{
    machine.Uart0.OnByteTransmit += b => { Console.Out.Write((char)b); Console.Out.Flush(); };
}
else if (channel == "usb")
{
    var cdc = new UsbCdcHost(machine.Usb);
    cdc.OnSerialData += data => { foreach (var b in data) Console.Out.Write((char)b); Console.Out.Flush(); };
}

var gate = new GdbHostTarget(machine);
var simLock = new object();

#endregion

#region Non-interactive diagnosis

if (diagnoseAfter is { } budget)
{
    var sw = Stopwatch.StartNew();
    var start = machine.InstructionCount;
    const int batch = 100_000;
    while (machine.InstructionCount - start < budget && !machine.Cpu.IsLockedUp)
        machine.Run(batch);

    Console.WriteLine();
    Console.WriteLine($"State after {machine.InstructionCount - start:N0} instructions ({sw.Elapsed.TotalSeconds:F1}s wall):");
    Console.WriteLine(DumpCpu());
    Console.WriteLine(PioInspector.Dump(machine.Pio0, 0, includeInstrMem: false));
    Console.WriteLine(PioInspector.Dump(machine.Pio1, 1, includeInstrMem: false));
    return ExitOk;
}

#endregion

#region GDB server and simulation thread

var server = new GdbTcpServer(gate, port);
server.OnLog = m => Console.Error.WriteLine($"[gdb] {m}");
server.Start();

if (startRunning) gate.Execute();

Console.Error.WriteLine($"rp2040-gdb — {Path.GetFileName(imagePath)} loaded, GDB on :{port}, CPU {(startRunning ? "running" : "halted")}.");
Console.Error.WriteLine("Type 'help' for commands. Attach with: arm-none-eabi-gdb -ex 'target remote :" + port + "'");

// Sim thread: only advances while the GDB gate is open.
//
// Two speeds. With no breakpoints armed we hand the CPU a big batch and let it run flat out.
// With breakpoints armed we must inspect PC before every instruction, which costs an outer
// call per instruction — the price of exact "stop before it executes" semantics.
const int Slice = 100_000;
var steppingOffBreakpoint = false;

var simThread = new Thread(() =>
{
    while (true)
    {
        if (!gate.Executing || machine.Cpu.IsLockedUp)
        {
            Thread.Sleep(5);
            continue;
        }

        lock (simLock)
        {
            if (server.Breakpoints.Count == 0)
            {
                machine.Run(Slice);
                continue;
            }

            // Resuming while parked on a breakpoint: advance one instruction first, or we
            // would re-report the same hit forever and 'continue' would never make progress.
            if (steppingOffBreakpoint)
            {
                machine.Run(1);
                steppingOffBreakpoint = false;
            }

            for (var i = 0; i < Slice; i++)
            {
                if (server.Breakpoints.Contains(machine.Cpu.Registers.PC))
                {
                    server.ReportBreakpointHit();
                    steppingOffBreakpoint = true;
                    break;
                }

                machine.Run(1);

                if (!gate.Executing || machine.Cpu.IsLockedUp)
                    break;
            }
        }
    }
}) { IsBackground = true, Name = "rp2040-sim" };
simThread.Start();

#endregion

#region REPL

// When stdin is not a console — an IDE launching us as a GDB server, a CI job, a pipe —
// there is no REPL to run, and reading it would hit EOF immediately and kill the server
// out from under the debugger. Park the main thread instead and let GDB drive.
if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("stdin is not a terminal: REPL disabled, serving GDB until killed.");
    Thread.Sleep(Timeout.Infinite);
}

while (Console.ReadLine() is { } line)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;

    // Inspection commands take the sim lock so the state they read is self-consistent.
    lock (simLock)
    {
        switch (parts[0])
        {
            case "help" or "?":
                PrintCommands();
                break;
            case "run" or "c":
                gate.Execute();
                Console.Error.WriteLine("running");
                break;
            case "halt" or "stop":
                gate.Stop();
                Console.Error.WriteLine($"halted at PC=0x{machine.Cpu.Registers.PC:X8}");
                break;
            case "step" or "s":
                gate.Stop();
                var count = parts.Length > 1 && int.TryParse(parts[1], out var sc) ? sc : 1;
                machine.Run(count);
                Console.WriteLine(DumpCpu());
                break;
            case "cpu" or "regs":
                Console.WriteLine(DumpCpu());
                break;
            case "pio":
                var withMem = parts.Contains("--mem");
                var blocks = parts.Length > 1 && parts[1] is "0" or "1"
                    ? [int.Parse(parts[1])]
                    : new[] { 0, 1 };
                foreach (var blk in blocks)
                    Console.WriteLine(PioInspector.Dump(blk == 0 ? machine.Pio0 : machine.Pio1, blk, withMem));
                break;
            case "mem" or "x":
                if (parts.Length < 2) { Console.Error.WriteLine("usage: mem <addr> [words]"); break; }
                DumpMem(parts[1], parts.Length > 2 && int.TryParse(parts[2], out var mw) ? mw : 8);
                break;
            case "where" or "bt":
                Symbolize(machine.Cpu.Registers.PC);
                break;
            case "quit" or "exit" or "q":
                return ExitOk;
            default:
                Console.Error.WriteLine($"unknown command '{parts[0]}' — try 'help'");
                break;
        }
    }
}

return ExitOk;

#endregion

#region Helpers

string DumpCpu()
{
    ref var r = ref machine.Cpu.Registers;
    var state = machine.Cpu.IsLockedUp ? "LOCKED UP" : r.Waiting ? "SLEEPING (WFI/WFE)" : gate.Executing ? "running" : "halted";
    return $"""
            CPU core0 — {state}   cycles={machine.InstructionCount:N0}
              PC=0x{r.PC:X8}  SP=0x{r.SP:X8}  LR=0x{r.LR:X8}  IPSR={r.IPSR} ({(r.IPSR == 0 ? "thread" : "handler")})
              R0=0x{r.R0:X8} R1=0x{r.R1:X8} R2=0x{r.R2:X8} R3=0x{r.R3:X8}
              R4=0x{r.R4:X8} R5=0x{r.R5:X8} R6=0x{r.R6:X8} R7=0x{r.R7:X8}
              PRIMASK={r.PRIMASK} CONTROL=0x{r.CONTROL:X}  N={(r.N ? 1 : 0)} Z={(r.Z ? 1 : 0)} C={(r.C ? 1 : 0)} V={(r.V ? 1 : 0)}
              pending IRQ=0x{r.PendingInterrupts:X8}  enabled=0x{r.EnabledInterrupts:X8}  VTOR=0x{r.VTOR:X8}
            """;
}

void DumpMem(string addrText, int words)
{
    if (!TryParseAddr(addrText, out var addr)) { Console.Error.WriteLine($"bad address '{addrText}'"); return; }
    for (var i = 0; i < words; i++)
    {
        var a = addr + (uint)(i * 4);
        Console.WriteLine($"  0x{a:X8}: 0x{machine.Bus.ReadWord(a):X8}");
    }
}

void Symbolize(uint pc)
{
    if (elfPath is null)
    {
        Console.Error.WriteLine($"PC=0x{pc:X8} — pass --elf <nanoCLR.elf> to resolve symbols");
        return;
    }
    try
    {
        var psi = new ProcessStartInfo("arm-none-eabi-addr2line")
        {
            RedirectStandardOutput = true,
            ArgumentList = { "-f", "-C", "-i", "-e", elfPath, $"0x{pc:X}" },
        };
        using var p = Process.Start(psi)!;
        Console.WriteLine($"PC=0x{pc:X8}\n{p.StandardOutput.ReadToEnd().TrimEnd()}");
        p.WaitForExit();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"addr2line failed: {ex.Message}");
    }
}

static bool TryParseAddr(string s, out uint addr) =>
    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr)
        : uint.TryParse(s, out addr);

static int Usage(string message)
{
    Console.Error.WriteLine($"error: {message}");
    PrintUsage();
    return ExitUsage;
}

static void PrintUsage() => Console.Error.WriteLine("""
    Usage: rp2040-gdb <image.uf2|image.bin> [options]

      --port <n>         GDB server port (default: 3333)
      --elf <path>       ELF with symbols, for the 'where' command
      --channel <c>      Serial output to echo: uart|usb|none (default: uart)
      --run              Start the CPU immediately (default: halted, so you can
                         set breakpoints before the first instruction runs)
      --pe <file|dir>    Build a deployment from compiled .pe assemblies and inject it
                         at 0x100FC000. Validates marker and CRCs, orders mscorlib
                         first, pads to 4 bytes. Repeatable; a directory takes
                         every .pe inside it.
      --emit-deployment <path>
                         Also write the assembled deployment image to disk, e.g. to
                         feed bin2uf2 --merge for the real board.
      --deploy <f>[@hex] Inject a raw binary at an address (default 0x100FC000)
      --diagnose <n>     Non-interactive: run n instructions, dump CPU + PIO, exit
      -h, --help
    """);

static void PrintCommands() => Console.Error.WriteLine("""
    run | c              resume execution
    halt | stop          pause execution
    step [n] | s [n]     run n instructions (default 1) and dump CPU
    cpu | regs           dump core registers and sleep/lockup state
    pio [0|1] [--mem]    dump PIO block registers (--mem also dumps INSTR_MEM)
    mem <addr> [words]   dump memory words (addr in 0x… or decimal)
    where | bt           resolve PC to a symbol via addr2line (needs --elf)
    quit | q             exit
    """);

#endregion

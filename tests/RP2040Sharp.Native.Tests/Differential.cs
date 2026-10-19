using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RP2040.Core.Cpu;
using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>How a chip runs its slices: the reference, or one of the speed-ups that must give the same result.</summary>
public enum SliceMode
{
    /// <summary>Every peripheral ticked after every slice, every slice run on its own (phase 1).</summary>
    Eager,
    /// <summary>Deferred peripheral ticks, every slice run on its own.</summary>
    Deferred,
    /// <summary>Deferred ticks, sleeping stretches and bounded awake runs (the default).</summary>
    Fast,
}

/// <summary>
/// One chip driven through the C ABI in a given <see cref="SliceMode"/>, recording everything a host could see (every
/// call's return cycle, pad events with their cycles, serial chunks with the cycle they arrived at) and, at the end,
/// the whole machine state that can be read without disturbing it: both cores' registers and cycle counts, retired
/// counts, SRAM, and the registers of every time-aware peripheral.
/// </summary>
internal sealed class DiffRun : IDisposable
{
    public const uint AllPads = (1u << 30) - 1;
    public readonly nint H;
    public readonly Host Host = new();
    public readonly Chip Chip;
    public readonly List<string> Calls = [];

    public DiffRun(SliceMode mode, uint flags = 0)
    {
        H = Abi.CreateEx(0, flags);
        Chip = Exports.Lookup(H)!;
        Chip.DeferPeripheralTicks = mode != SliceMode.Eager;
        Chip.SleepingStretches = Chip.AwakeBoundedRuns = mode == SliceMode.Fast;
        // The flash unique ID is random per machine (machine.unique_id(), MicroPython seeds from it): pin it.
        Chip.Machine.Ssi.UniqueId = [0xE6, 0x60, 0x58, 0x38, 0x83, 0x4F, 0x2A, 0x2D];
        // So is the CYW43439's MAC (a process-wide counter).
        if (Chip.Cyw43 is { } wifi) wifi.Sdpcm.MacAddress = [0x28, 0xCD, 0xC1, 0x00, 0x00, 0x01];
        Host.Chip = H;
        Abi.Attach(H, Host);
    }

    public ulong Cycles => Abi.Cycles(H);

    public ulong RunUntilEdge(uint max, uint guard, uint watch = AllPads)
    {
        int before = Host.Pins.Count;
        var c = Abi.RunUntilEdge(H, max, guard, watch);
        Calls.Add($"{c} +{Host.Pins.Count - before}");
        Assert.Equal(0, Abi.Status(H));
        return c;
    }

    public ulong Run(uint n)
    {
        var c = Abi.Run(H, n);
        Calls.Add($"run {c}");
        Assert.Equal(0, Abi.Status(H));
        return c;
    }

    /// <summary>Every line a host or a debugger could compare, in order.</summary>
    public List<string> Trace()
    {
        var t = new List<string>();
        t.AddRange(Calls.Select((c, i) => $"call {i}: {c}"));
        t.AddRange(Host.Pins.Select(p => $"pad {p.Gpio}={p.Level} oe {p.Oe} @{p.Cycle}"));
        t.AddRange(Host.SerialLog.Select(s => $"serial {s.Channel} @{s.Cycle}: {Escape(s.Data)}"));
        t.AddRange(State());
        return t;
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private unsafe List<string> State()
    {
        var m = Chip.Machine;
        var t = new List<string>
        {
            $"core0 cycles {m.Cpu.Cycles} {Regs(m.Cpu)}",
            $"core1 cycles {m.Cpu1.Cycles} launched {m.Core1Launched} {Regs(m.Cpu1)}",
            $"retired {Abi.Instructions(H, 0)} {Abi.Instructions(H, 1)}",
            $"swept {m.SweptCycles}",
            $"sram {Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(m.Bus.PtrSram, (int)RP2040.Core.Memory.BusInterconnect.SRAM_SIZE)))}",
        };
        foreach (var (name, addrs) in Registers())
        {
            var sb = new StringBuilder(name);
            foreach (var a in addrs) sb.Append(' ').Append(m.Bus.ReadWord(a).ToString("X"));
            t.Add(sb.ToString());
        }
        return t;
    }

    private static string Regs(CortexM0Plus cpu)
    {
        var r = cpu.Registers;
        var sb = new StringBuilder();
        foreach (var f in typeof(Registers).GetFields(BindingFlags.Public | BindingFlags.Instance))
            sb.Append(f.Name).Append('=').Append(f.GetValue(r)).Append(' ');
        sb.Append("locked=").Append(cpu.IsLockedUp);
        return sb.ToString();
    }

    private static IEnumerable<uint> Range(uint from, uint to, uint step = 4)
    {
        for (var a = from; a <= to; a += step) yield return a;
    }

    /// <summary>Registers that read back without side effects (no FIFO pops, no COUNTFLAG clears).</summary>
    private static IEnumerable<(string, IEnumerable<uint>)> Registers()
    {
        yield return ("timer", [.. Range(0x40054010, 0x40054028), .. Range(0x40054034, 0x40054040)]);
        yield return ("pwm", Range(0x40050000, 0x400500B0));
        foreach (var pio in new uint[] { 0x50200000, 0x50300000 })
            yield return ($"pio{(pio >> 20) & 1}", [pio, pio + 4, pio + 8, pio + 0xC, pio + 0x30, .. Range(pio + 0xC8, pio + 0x140)]);
        yield return ("rtc", Range(0x4005C004, 0x4005C02C));
        yield return ("watchdog", Range(0x40058000, 0x4005802C));
        yield return ("adc", [0x4004C000, 0x4004C004, 0x4004C008, .. Range(0x4004C010, 0x4004C020)]);
        yield return ("usb", Range(0x50110000, 0x5011009C));
        yield return ("dpram", Range(0x50100000, 0x50100FFC));
        yield return ("dma", [.. Range(0x50000000, 0x500002FC), .. Range(0x50000400, 0x50000448)]);
        foreach (var spi in new uint[] { 0x4003C000, 0x40040000 })
            yield return ($"spi{(spi >> 14) & 1}", [spi, spi + 4, .. Range(spi + 0xC, spi + 0x24)]);
        // UART and I2C registers that read back without side effects (no UARTDR, IC_DATA_CMD or IC_CLR_* reads).
        foreach (var (name, uart) in new[] { ("uart0", 0x40034000u), ("uart1", 0x40038000u) })
            yield return (name, [uart + 0x04, uart + 0x18, .. Range(uart + 0x24, uart + 0x40), uart + 0x48]);
        foreach (var (name, i2c) in new[] { ("i2c0", 0x40044000u), ("i2c1", 0x40048000u) })
            yield return (name, [i2c, i2c + 4, i2c + 8, .. Range(i2c + 0x14, i2c + 0x3C), .. Range(i2c + 0x6C, i2c + 0x9C)]);
        yield return ("ppb", [0xE000E014, 0xE000E018, 0xE000E100, 0xE000E200, 0xE000ED04]);
        yield return ("iobank0", Range(0x40014000, 0x400140E8, 8));
        yield return ("sio", [0xD0000004, 0xD0000010, 0xD0000020]);
    }

    public void Dispose()
    {
        Abi.Destroy(H);
        Host.Dispose();
    }

    /// <summary>Assert two traces are identical, naming the first line that differs.</summary>
    public static void AssertSame(List<string> expected, List<string> actual, string what)
    {
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
            if (expected[i] != actual[i])
                Assert.Fail($"{what}: line {i} differs\n  expected: {Clip(expected[i])}\n  actual:   {Clip(actual[i])}");
        Assert.True(expected.Count == actual.Count, $"{what}: {expected.Count} lines expected, {actual.Count} got");
    }

    private static string Clip(string s) => s.Length > 1500 ? s[..1500] + "..." : s;
}

/// <summary>Drives the same host script in every <see cref="SliceMode"/> and checks the traces agree.</summary>
internal static class Differential
{
    /// <summary>
    /// Run <paramref name="script"/> on an Eager, a Deferred and a Fast chip; Deferred and Fast must reproduce the
    /// Eager trace line for line. Returns the Fast chip's probe counts (stretched, bounded, plain slices).
    /// </summary>
    public static (long Stretched, long Bounded, long Plain) Check(Action<DiffRun> script, uint flags = 0,
        SliceMode[]? modes = null)
    {
        modes ??= [SliceMode.Eager, SliceMode.Deferred, SliceMode.Fast];
        List<string>? reference = null;
        (long, long, long) probe = default;
        foreach (var mode in modes)
        {
            using var run = new DiffRun(mode, flags);
            script(run);
            var trace = run.Trace();
            if (reference is null) reference = trace;
            else DiffRun.AssertSame(reference, trace, $"{mode} against {modes[0]}");
            if (mode == SliceMode.Fast) probe = (run.Chip.StretchedSlices, run.Chip.BoundedSlices, run.Chip.PlainSlices);
        }
        return probe;
    }
}

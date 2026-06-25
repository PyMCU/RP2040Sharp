using NanoFramework.Clr;
using RP2040.TestKit.Boards;

namespace RP2040Sharp.NanoFramework.TestKit;

/// <summary>
/// Boots a deployed nanoFramework app on the RP2040Sharp emulator and drives it to points of interest
/// inside the running CLR — a generic condition (<see cref="RunUntil"/>), a native function by symbol
/// (<see cref="RunUntilNativeCall"/>), a managed method, or a static-field predicate — so a test can
/// assert on what the firmware is actually doing, not just on wall-clock slices.
/// </summary>
/// <remarks>
/// This is the RP2040 façade over the chip-agnostic <see cref="ClrSession"/>: it owns the emulator and
/// the boot, and adapts them through <see cref="Rp2040ClrHost"/>. All the run/snapshot/profile logic
/// lives in NanoFramework.Clr.Core and is shared with the other chips' kits and the profiler. The run
/// methods drive the emulator's hook-aware execution path (the one that services the bootrom/flash
/// hooks the firmware needs to boot).
/// </remarks>
public sealed class NanoClrHarness : IDisposable
{
    /// <summary>The emulated Pico. Add probes (e.g. <c>AddPioProbe</c>) before running.</summary>
    public PicoSimulation Pico { get; }

    public NanoFirmware Firmware { get; }
    public NanoApp App { get; }

    /// <summary>The chip-agnostic session driving the CLR — exposed for advanced/cross-chip use.</summary>
    public ClrSession Session { get; }

    private NanoClrHarness(PicoSimulation pico, NanoFirmware firmware, NanoApp app)
    {
        Pico = pico;
        Firmware = firmware;
        App = app;
        Session = new ClrSession(new Rp2040ClrHost(pico, firmware));
    }

    /// <summary>
    /// Creates the emulator and flashes booter + nanoCLR + deployment (after the checksum guard), but
    /// does not run — wire up probes via <see cref="Pico"/>, then call a <c>RunUntil…</c> method.
    /// </summary>
    public static NanoClrHarness Boot(NanoFirmware firmware, NanoApp app, bool withUsbCdc = false)
    {
        var pico = new PicoSimulation(withUsbCdc: withUsbCdc);
        firmware.BootInto(pico, app);
        return new NanoClrHarness(pico, firmware, app);
    }

    /// <summary>The symbol last reached by <see cref="RunUntilNativeCall"/> (null if none).</summary>
    public string? LastReachedSymbol => Session.LastReachedSymbol;

    /// <summary>The cycle count when <see cref="LastReachedSymbol"/> was reached (-1 if none).</summary>
    public long LastReachedCycle => Session.LastReachedCycle;

    /// <summary>The current CPU program counter.</summary>
    public uint Pc => Session.Pc;

    public bool IsLockedUp => Session.IsLockedUp;

    public long InstructionCount => Session.InstructionCount;

    /// <summary>Reads managed CLR state out of the emulator (needs <c>g_CLR_RT_TypeSystem</c> in the manifest).</summary>
    public ClrInspector Clr => Session.Clr;

    /// <inheritdoc cref="ClrSession.RunUntil"/>
    public bool RunUntil(Func<bool> ready, long maxInstructions = 200_000_000, int slice = 100_000)
        => Session.RunUntil(ready, maxInstructions, slice);

    /// <inheritdoc cref="ClrSession.RunUntilNativeCall"/>
    public bool RunUntilNativeCall(string symbol, long maxInstructions = 200_000_000)
        => Session.RunUntilNativeCall(symbol, maxInstructions);

    /// <inheritdoc cref="ClrSession.RunUntilManagedMethod"/>
    public bool RunUntilManagedMethod(string methodFqn, long maxInstructions = 200_000_000)
        => Session.RunUntilManagedMethod(methodFqn, maxInstructions);

    /// <inheritdoc cref="ClrSession.ProfileCalls"/>
    public ClrCallProfile ProfileCalls(Func<bool> until, long maxInstructions = 200_000_000)
        => Session.ProfileCalls(until, maxInstructions);

    /// <inheritdoc cref="ClrSession.CaptureHeap()"/>
    public ClrInspector.HeapSnapshot CaptureHeap() => Session.CaptureHeap();

    /// <inheritdoc cref="ClrSession.CaptureHeap(uint)"/>
    public ClrInspector.HeapSnapshot CaptureHeap(uint executionEngineAddress) => Session.CaptureHeap(executionEngineAddress);

    /// <inheritdoc cref="ClrSession.StaticArrayLength"/>
    public uint StaticArrayLength(string assembly, string field) => Session.StaticArrayLength(assembly, field);

    /// <inheritdoc cref="ClrSession.ReadStaticArrayInt32"/>
    public int ReadStaticArrayInt32(string assembly, string field, int index)
        => Session.ReadStaticArrayInt32(assembly, field, index);

    /// <inheritdoc cref="ClrSession.ReadStatic"/>
    public ClrInspector.HeapValue ReadStatic(string assembly, string field) => Session.ReadStatic(assembly, field);

    /// <inheritdoc cref="ClrSession.ReadStaticInt32"/>
    public int ReadStaticInt32(string assembly, string field) => Session.ReadStaticInt32(assembly, field);

    /// <inheritdoc cref="ClrSession.ReadStaticInt64"/>
    public long ReadStaticInt64(string assembly, string field) => Session.ReadStaticInt64(assembly, field);

    /// <inheritdoc cref="ClrSession.ReadInstance"/>
    public ClrInspector.HeapValue ReadInstance(string assembly, string staticObjectField, string typeName, string instanceField)
        => Session.ReadInstance(assembly, staticObjectField, typeName, instanceField);

    /// <inheritdoc cref="ClrSession.ReadInstanceInt32"/>
    public int ReadInstanceInt32(string assembly, string staticObjectField, string typeName, string instanceField)
        => Session.ReadInstanceInt32(assembly, staticObjectField, typeName, instanceField);

    /// <inheritdoc cref="ClrSession.RunUntilStatic"/>
    public bool RunUntilStatic(
        string assembly,
        string field,
        Func<ClrInspector.HeapValue, bool> predicate,
        long maxInstructions = 200_000_000,
        int slice = 100_000)
        => Session.RunUntilStatic(assembly, field, predicate, maxInstructions, slice);

    public void Dispose() => Pico.Dispose();
}

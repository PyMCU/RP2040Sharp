using RP2040.Core.Cpu;
using RP2040.TestKit.Boards;

namespace RP2040.NanoFramework.TestKit;

/// <summary>
/// Boots a deployed nanoFramework app on the RP2040Sharp emulator and drives it to points of
/// interest inside the running CLR — a generic condition (<see cref="RunUntil"/>) or a native CLR
/// function located by symbol (<see cref="RunUntilNativeCall"/>) — so a test can assert on what the
/// firmware is actually doing, not just on wall-clock slices.
/// </summary>
/// <remarks>
/// Both run methods drive the emulator's hook-aware execution path (the one that services the
/// bootrom/flash native hooks the firmware needs to boot). <see cref="RunUntilNativeCall"/> watches
/// every executed instruction's PC through the profiling observer, so the crossing into the native
/// function is detected exactly — never sampled past.
/// </remarks>
public sealed class NanoClrHarness : IDisposable
{
    /// <summary>The emulated Pico. Add probes (e.g. <c>AddPioProbe</c>) before running.</summary>
    public PicoSimulation Pico { get; }

    public NanoFirmware Firmware { get; }
    public NanoApp App { get; }

    /// <summary>The symbol last reached by <see cref="RunUntilNativeCall"/> (null if none).</summary>
    public string? LastReachedSymbol { get; private set; }

    /// <summary>The cycle count when <see cref="LastReachedSymbol"/> was reached (-1 if none).</summary>
    public long LastReachedCycle { get; private set; } = -1;

    private NanoClrHarness(PicoSimulation pico, NanoFirmware firmware, NanoApp app)
    {
        Pico = pico;
        Firmware = firmware;
        App = app;
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

    /// <summary>The current CPU program counter.</summary>
    public uint Pc => Pico.Cpu.Registers.PC;

    public bool IsLockedUp => Pico.Cpu.IsLockedUp;

    public long InstructionCount => Pico.InstructionCount;

    /// <summary>
    /// Runs the CLR in hook-aware slices until <paramref name="ready"/> holds (checked at slice
    /// boundaries — apt for monotonic, observable state such as a probe's captured-word count).
    /// Returns false if <paramref name="maxInstructions"/> is reached first.
    /// </summary>
    public bool RunUntil(Func<bool> ready, long maxInstructions = 200_000_000, int slice = 100_000)
    {
        long ran = 0;
        while (ran < maxInstructions && !ready())
        {
            Pico.RunInstructions(slice);
            ran += slice;
        }

        return ready();
    }

    /// <summary>
    /// Runs the CLR until the CPU executes the native function <paramref name="symbol"/> (resolved
    /// from the firmware manifest) — i.e. until managed code crosses into that InternalCall. Every
    /// executed instruction's PC is watched, so the crossing is caught exactly. On success
    /// <see cref="LastReachedSymbol"/>/<see cref="LastReachedCycle"/> record where and when.
    /// </summary>
    public bool RunUntilNativeCall(string symbol, long maxInstructions = 200_000_000)
    {
        uint target = Firmware.ResolveSymbol(symbol);
        var watch = new PcWatch(target);

        long remaining = maxInstructions;
        while (remaining > 0 && !watch.Hit)
        {
            int chunk = (int)Math.Min(remaining, 1_000_000);
            Pico.Rp2040.RunProfiled(chunk, watch);
            remaining -= chunk;
        }

        if (watch.Hit)
        {
            LastReachedSymbol = symbol;
            LastReachedCycle = watch.HitCycle;
        }

        return watch.Hit;
    }

    /// <summary>
    /// Runs the CLR until a managed method executes — i.e. until <c>CLR_RT_Thread::Execute_IL</c> is
    /// entered for a stack frame whose method is <paramref name="methodFqn"/> ("Assembly!Method", e.g.
    /// a generated <c>AppSymbols.Methods.Main</c>). Needs <c>Execute_IL</c> in the firmware manifest.
    /// </summary>
    public bool RunUntilManagedMethod(string methodFqn, long maxInstructions = 200_000_000)
    {
        uint executeIl = Firmware.ResolveSymbol("Execute_IL");
        var watch = new MethodWatch(Pico.Cpu, Clr, executeIl, methodFqn);

        long remaining = maxInstructions;
        while (remaining > 0 && !watch.Hit)
        {
            int chunk = (int)Math.Min(remaining, 1_000_000);
            Pico.Rp2040.RunProfiled(chunk, watch);
            remaining -= chunk;
        }

        if (watch.Hit)
        {
            LastReachedSymbol = methodFqn;
            LastReachedCycle = watch.HitCycle;
        }

        return watch.Hit;
    }

    /// <summary>Element count of a managed <c>static int[]</c> field (a heap-allocated array object).</summary>
    public uint StaticArrayLength(string assembly, string field) => Clr.ReadArrayLength(ResolveStaticArray(assembly, field));

    /// <summary>Reads element <paramref name="index"/> of a managed <c>static int[]</c> field.</summary>
    public int ReadStaticArrayInt32(string assembly, string field, int index)
        => Clr.ReadArrayInt32(ResolveStaticArray(assembly, field), index);

    private uint ResolveStaticArray(string assembly, string field)
    {
        uint asm = Clr.FindAssembly(assembly);
        if (asm == 0 || !Clr.TryResolveStaticSlot(asm, field, out int slot))
        {
            throw new InvalidOperationException($"Static array '{field}' not found/ready in '{assembly}'.");
        }

        uint arrayObject = Clr.ReadStatic(asm, slot).Raw; // the static cell holds the array reference
        if (arrayObject == 0)
        {
            throw new InvalidOperationException($"Static array '{field}' is null.");
        }

        return arrayObject;
    }

    // Watches every Execute_IL entry; flags when the running frame's method matches the target. The
    // CLR_RT_StackFrame& is the call argument, so it sits in R0 or R1 — check both.
    private sealed class MethodWatch(CortexM0Plus cpu, ClrInspector clr, uint executeIl, string fqn) : IProfilingObserver
    {
        public bool Hit { get; private set; }
        public long HitCycle { get; private set; } = -1;

        public void OnInstruction(uint pc, ushort opcode, long cycles)
        {
            if (Hit || pc != executeIl)
            {
                return;
            }

            if (clr.MethodAt(cpu.Registers.R1) == fqn || clr.MethodAt(cpu.Registers.R0) == fqn)
            {
                Hit = true;
                HitCycle = cycles;
            }
        }
    }

    private ClrInspector? _inspector;

    /// <summary>Reads managed CLR state out of the emulator (needs <c>g_CLR_RT_TypeSystem</c> in the manifest).</summary>
    public ClrInspector Clr => _inspector ??= new ClrInspector(Pico.Rp2040, Firmware.ResolveSymbol("g_CLR_RT_TypeSystem"));

    /// <summary>Reads a managed static field's cell (data type + raw value) by name (assembly must be loaded).</summary>
    public ClrInspector.HeapValue ReadStatic(string assembly, string field)
    {
        uint asm = Clr.FindAssembly(assembly);
        if (asm == 0)
        {
            throw new InvalidOperationException($"Assembly '{assembly}' is not loaded yet.");
        }

        if (!Clr.TryResolveStaticSlot(asm, field, out int slot))
        {
            throw new InvalidOperationException($"Static field '{field}' not found/ready in '{assembly}'.");
        }

        return Clr.ReadStatic(asm, slot);
    }

    /// <summary>Reads a managed <c>static int</c> field by name (the assembly must already be loaded).</summary>
    public int ReadStaticInt32(string assembly, string field) => ReadStatic(assembly, field).AsInt32;

    /// <summary>Reads a managed <c>static long</c> field by name (its full 64-bit value).</summary>
    public long ReadStaticInt64(string assembly, string field)
    {
        uint asm = Clr.FindAssembly(assembly);
        if (asm == 0 || !Clr.TryResolveStaticSlot(asm, field, out int slot))
        {
            throw new InvalidOperationException($"Static field '{field}' not found/ready in '{assembly}'.");
        }

        return Clr.ReadStaticInt64(asm, slot);
    }

    /// <summary>
    /// Reads an instance field by name from the object held in a static reference field — e.g. the
    /// <paramref name="instanceField"/> of type <paramref name="typeName"/> on the object in
    /// <paramref name="staticObjectField"/>.
    /// </summary>
    public ClrInspector.HeapValue ReadInstance(string assembly, string staticObjectField, string typeName, string instanceField)
    {
        uint asm = Clr.FindAssembly(assembly);
        if (asm == 0 || !Clr.TryResolveStaticSlot(asm, staticObjectField, out int objSlot))
        {
            throw new InvalidOperationException($"Static field '{staticObjectField}' not found/ready in '{assembly}'.");
        }

        uint obj = Clr.ReadStatic(asm, objSlot).Raw; // the static cell holds the object reference
        if (obj == 0)
        {
            throw new InvalidOperationException($"Static object '{staticObjectField}' is null.");
        }

        if (!Clr.TryResolveInstanceSlot(asm, typeName, instanceField, out int fieldSlot))
        {
            throw new InvalidOperationException($"Instance field '{typeName}.{instanceField}' not found in '{assembly}'.");
        }

        return Clr.ReadInstance(obj, fieldSlot);
    }

    /// <summary>Reads an <c>int</c> instance field by name from the object in a static reference field.</summary>
    public int ReadInstanceInt32(string assembly, string staticObjectField, string typeName, string instanceField)
        => ReadInstance(assembly, staticObjectField, typeName, instanceField).AsInt32;

    /// <summary>
    /// Runs the CLR until a managed static field satisfies <paramref name="predicate"/>. The assembly
    /// is loaded and the field resolved lazily (once the CLR has deployed it), then read at each slice
    /// boundary. Returns false if the budget is exhausted first.
    /// </summary>
    public bool RunUntilStatic(
        string assembly,
        string field,
        Func<ClrInspector.HeapValue, bool> predicate,
        long maxInstructions = 200_000_000,
        int slice = 100_000)
    {
        // Resolve fresh each time: the CLR may relocate the assembly object and (re)allocate its
        // statics during load, so a cached pointer/slot can go stale.
        bool Satisfied()
        {
            uint asm = Clr.FindAssembly(assembly);
            return asm != 0
                && Clr.TryResolveStaticSlot(asm, field, out int slot)
                && predicate(Clr.ReadStatic(asm, slot));
        }

        long ran = 0;
        while (ran < maxInstructions && !Satisfied())
        {
            Pico.RunInstructions(slice);
            ran += slice;
        }

        return Satisfied();
    }

    public void Dispose() => Pico.Dispose();

    // Stops at the first instruction whose PC equals the target (the profiling observer sees every
    // executed instruction's PC with the Thumb bit already stripped).
    private sealed class PcWatch(uint target) : IProfilingObserver
    {
        public bool Hit { get; private set; }
        public long HitCycle { get; private set; } = -1;

        public void OnInstruction(uint pc, ushort opcode, long cycles)
        {
            if (!Hit && pc == target)
            {
                Hit = true;
                HitCycle = cycles;
            }
        }
    }
}

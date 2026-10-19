using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RP2040.Peripherals;
using RP2040.Peripherals.PinLevel;
using RP2040.Peripherals.Usb;
using RP2040Sharp.Wireless.Cyw43;

namespace SiliconTwin.Native;

/// <summary>One emulated RP2040 behind an opaque C handle.</summary>
internal sealed unsafe class Chip
{
    public const int GpioCount = 30;
    private const uint AllPads = (1u << GpioCount) - 1;

    /// <summary>
    /// Most instructions a slice runs before the peripherals advance (they tick once per slice). USB enumeration and
    /// the MicroPython REPL need them at least this often; the TestKit and the CYW43 tests use the same order.
    /// </summary>
    internal int PeripheralQuantum = 512;

    // ADC code of the temperature sensor at 27 degrees C (0.706 V, RP2040 datasheet 4.9.5), as the core reads it
    // without a host: setting AdcPeripheral.ReadChannel replaces that default for every channel.
    private const ushort TempSensor27C = 876;

    public readonly RP2040Machine Machine;
    public readonly UsbCdcHost Cdc;
    public readonly Cyw43439Device? Cyw43;
    private readonly ushort[] _adc = new ushort[4];

    /// <summary>Every pad change with its own cycle, reported in cycle order after each call (PadEventRecorder).</summary>
    public readonly PadEventRecorder Pads;

    // ---- fault isolation -------------------------------------------------------------------------
    // Any managed exception inside an export lands in Fault(): the chip becomes inert ("faulted", sticky)
    // and keeps a short error text for st_rp2040_last_error. Nothing may ever propagate out of an export:
    // in NativeAOT that would terminate the whole host process.
    public const int ErrorCapacity = 256;
    private byte* _err = (byte*)NativeMemory.AllocZeroed(ErrorCapacity);
    public volatile bool Faulted;
    public bool Running, DestroyPending, Freed;
    public ulong LastCycles, LastPs;
#if ST_ENABLE_TEST_HOOKS
    public int InjectOnRun, InjectOnScan;
#endif

    public byte* ErrorPtr => _err;

    public void SetError(string msg)
    {
        var p = _err;
        if (p == null) return;
        int n = 0;
        foreach (char c in msg)
        {
            if (n >= ErrorCapacity - 1) break;
            p[n++] = c < 0x20 || c > 0x7E ? (byte)' ' : (byte)c; // one ASCII line
        }
        p[n] = 0;
    }

    /// <summary>Put the chip in the sticky faulted state, keeping the first error only.</summary>
    public void Fault(string where, Exception e)
    {
        if (Faulted) return;
        string pc = "";
        try { pc = " (guest pc 0x" + Machine.Cpu.Registers.PC.ToString("X8") + ")"; } catch { }
        try { LastCycles = (ulong)Machine.Cpu.Cycles; LastPs = TimePs; } catch { }
        SetError(where + ": " + e.GetType().Name + ": " + e.Message + pc);
        Faulted = true;
    }

    /// <summary>Record a rejected argument; never overrides the message of a faulted chip.</summary>
    public void Reject(string msg) { if (!Faulted) SetError(msg); }

    public void Free()
    {
        if (Freed) return;
        Freed = true;
        try { Machine.Dispose(); } catch { }
        var p = _err; _err = null;
        // the error buffer is released with the chip; st_rp2040_last_error on a stale handle never reaches it
        NativeMemory.Free(p);
    }

    public delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void> PinCb;
    public nint PinUser;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> SerialCb;
    public nint SerialUser;

    private readonly Action<PadEvent> _report;

    public Chip(uint flashBytes, bool cyw43, PinLevelBus buses = PinLevelBus.None)
    {
        _report = Report;
        Machine = new RP2040Machine(flashBytes, pinLevelBuses: buses) { DeferPeripheralTicks = true };
        Machine.Adc.ReadChannel = ch => (uint)ch < (uint)_adc.Length ? _adc[ch] : ch == 4 ? TempSensor27C : (ushort)0;
        Machine.Uart0.OnByteTransmit = b => EmitSerial(0, b);
        Machine.Uart1.OnByteTransmit = b => EmitSerial(1, b);
        Cdc = new UsbCdcHost(Machine.Usb);
        Cdc.OnSerialData = data => EmitSerial(2, data);
        // Every pad change that goes through IO_BANK0 (SIO writes of either core, PIO, FUNCSEL / override / PADS
        // writes, DMA into SIO, the pin-level UART / SPI / I2C engines) is recorded while it happens, so it is
        // stamped with its own cycle (an engine's: the cycle of its edge). PWM-muxed pads follow the counter instead
        // and are polled (Pads.Poll). A watched pad's change ends a bounded run and cuts a cut slice after the
        // instruction that made it.
        Pads = new PadEventRecorder(Machine)
        {
            WatchedEdge = () => { Machine.EndBoundedRun = true; Machine.RequestCut(); },
        };
        Pads.Attach(baseline: false);
        // A bounded run books slice ends without coming back here: poll the watched PWM pads at each of them.
        Machine.BoundaryCheck = () =>
        {
            uint pwm = Machine.IoBank0.PwmMuxedPins & Pads.Watch;
            if (pwm != 0) Pads.Poll(pwm);
            return Pads.WatchedChanged;
        };
        if (cyw43)
        {
            // Pico W wiring, as RP2040Sharp.Wireless' tests and the Silicon Twin demo do it: IO_BANK0 hears SIO only
            // for the pads muxed to SIO, so forward every SIO write (WL_REG_ON, the gSPI chip select) to the device.
            var m = Machine;
            m.Sio.OnGpioChanged += mask => m.IoBank0.NotifyPads(mask);
            Cyw43 = new Cyw43439Device(m.IoBank0);
        }
    }

    // ---- time ------------------------------------------------------------------------------------
    // The core runs clk_sys at a fixed 125 MHz (it does not model clock reconfiguration), so time is cycles * 8 ns.

    public static ulong CycleToPs(ulong cycle)
    {
        const uint hz = RP2040Machine.CLK_HZ;
        // exact to the picosecond without 128-bit math: split into whole seconds, then two 1e6 stages
        ulong sec = cycle / hz, rem = cycle % hz;
        ulong a = rem * 1_000_000UL, us = a / hz;
        return sec * 1_000_000_000_000UL + us * 1_000_000UL + (a % hz) * 1_000_000UL / hz;
    }

    public ulong Cycles => (ulong)Machine.Cpu.Cycles;

    public ulong TimePs => CycleToPs(Cycles);

    // ---- running ---------------------------------------------------------------------------------

    /// <summary>Conservative per-core retired-instruction count (see <see cref="RunSlice"/>).</summary>
    public readonly ulong[] Retired = new ulong[2];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Parked(int core) => core == 0
        ? Machine.Cpu.Registers.Waiting || Machine.Cpu.IsLockedUp
        : !Machine.Core1Launched || Machine.Cpu1.Registers.Waiting || Machine.Cpu1.IsLockedUp;

    /// <summary>
    /// One machine slice of <paramref name="n"/> instructions per core, then the peripheral ticks. The core has no
    /// retired-instruction counter and credits a sleeping core's slots as cycles, so a slice is credited to a core
    /// only if that core was neither asleep (WFI/WFE), locked up nor (core 1) unlaunched at its start and at its end:
    /// a lower bound, exact for CPU-bound code, low by up to one slice per sleep/wake transition.
    /// </summary>
    private int RunSlice(int n, bool cut = false)
    {
        bool p0 = Parked(0), p1 = Parked(1);
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnRun == 1) { InjectOnRun = 0; throw new InvalidOperationException("injected fault inside Run"); }
        if (InjectOnRun == 2) { InjectOnRun = 0; Chip? none = null; _ = none!.Machine; }
#endif
        int k = n;
        if (cut) k = Machine.RunCut(n, Pads.Watch);
        else Machine.Run(n);
        if (k < n) CutSlices++;
        if (!p0 && !Parked(0)) Retired[0] += (ulong)k;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)k;
        return k;
    }

    /// <summary>Run <paramref name="max"/> instructions per core in peripheral-quantum slices (st_rp2040_run).</summary>
    public void Run(int max) => RunUntilEdge(max, 0, 0);

    // ---- co-simulation speed-ups (docs/peripheral-deadlines.md) -----------------------------------------------------
    // Each gives exactly the result of the plain slices; turning them off is the reference the tests compare against.

    /// <summary>Peripheral ticks put off until their next event or access (RP2040Machine.DeferPeripheralTicks).</summary>
    internal bool DeferPeripheralTicks
    {
        get => Machine.DeferPeripheralTicks;
        set => Machine.DeferPeripheralTicks = value;
    }

    /// <summary>While both cores sleep quietly, the sleeping slices before the next event are booked at once.</summary>
    internal bool SleepingStretches = true;

    /// <summary>While core 0 is awake and core 1 is not running, its slices run in one bounded interpreter loop.</summary>
    internal bool AwakeBoundedRuns = true;

    /// <summary>Test probe: slices booked by stretches and by bounded runs, and slices run plainly.</summary>
    internal long StretchedSlices, BoundedSlices, PlainSlices;

    /// <summary>
    /// Event-bounded guard (docs/pin-level-buses.md). The host answers a pad edge only after the call returns, so a
    /// slice must not run from a watched pad's edge into the sample of an input the circuit may be answering on (an
    /// SPI MISO edge, an I2C ACK, a UART echo). With pin-level buses and a guard above 1, every slice of core 0 also
    /// ends before the first instruction at or past the next edge a pin-level engine makes on a watched pad, and
    /// right after an instruction that moved a watched pad: the call returns where guard 1 would have after each such
    /// edge, whatever the guard. Core 1's turn, interrupt latency and the sweeps are still bounded by the guard.
    /// </summary>
    internal bool EventGuard = true;

    /// <summary>Test probe: slices that a cut ended early.</summary>
    internal long CutSlices;

    /// <summary>
    /// Run up to <paramref name="max"/> instructions in slices of <paramref name="guard"/> whenever a pad is watched
    /// and the guard is nonzero (twice that while both cores sleep), stopping after the slice in which a watched pad
    /// changed. A slice never exceeds <see cref="PeripheralQuantum"/>. Peripherals tick at the end of every slice,
    /// before the pads are looked at, so there is nothing pending when the call returns. Pad changes are reported at
    /// the end, in cycle order. Sleeping stretches and bounded runs take several of those slices at once, with the
    /// same result.
    /// </summary>
    public void RunUntilEdge(int max, int guard, uint watch)
    {
        bool armed = watch != 0 && guard > 0;
        Pads.Watch = armed ? watch & AllPads : 0;
        Pads.WatchedChanged = false;
        uint watched = Pads.Watch;
        var m = Machine;
        // The event-bounded guard: guard 1 already ends every slice after one instruction.
        bool events = armed && EventGuard && guard > 1 && m.PinLevelBuses != PinLevelBus.None && watched != 0;
        try
        {
            int quantum = Math.Max(1, PeripheralQuantum);
            for (int done = 0; done < max;)
            {
                bool sleeping = Parked(0) && Parked(1);
                int slice = quantum;
                if (armed)
                {
                    slice = sleeping ? (int)Math.Min(2L * guard, int.MaxValue) : guard;
                    slice = Math.Min(slice, quantum);
                }
                int n = Math.Min(slice, max - done);
                int whole = (max - done) / slice;   // full slices left in the call
                if (whole >= 2 && m.DeferPeripheralTicks)
                {
                    if (sleeping && SleepingStretches && m.CanSkipSleepingSlices())
                    {
                        // The first sweep with something due is the j-th slice from here; the ones before it only count.
                        long until = m.SweptCyclesUntilEvent();
                        long j = until == long.MaxValue ? long.MaxValue : Math.Max(1, (until + slice - 1) / slice);
                        long skip = Math.Min(j - 1, whole - 1);
                        if (skip > 0)
                        {
                            m.SkipSleepingSlices(skip, slice);
                            StretchedSlices += skip;
                            done += (int)(skip * slice);
                            continue;
                        }
                    }
                    else if (!Parked(0) && AwakeBoundedRuns && m.Core1Inert)
                    {
                        RunBounded(slice, whole, events);
                        done += _boundedDone;
                        if (armed)
                        {
                            uint pwmB = m.IoBank0.PwmMuxedPins & watched;
                            if (pwmB != 0) Pads.Poll(pwmB);
                            if (Pads.WatchedChanged) break;
                        }
                        continue;
                    }
                }
                PlainSlices++;
                done += RunSlice(n, events);
                if (!armed) continue;
                // PWM-muxed pads move with the counter, which IO_BANK0 never reports: compare the watched ones.
                uint pwm = m.IoBank0.PwmMuxedPins & watched;
                if (pwm != 0) Pads.Poll(pwm);
                if (Pads.WatchedChanged) break;
            }
        }
        finally { Pads.Watch = 0; }
        ScanPads();
    }

    private int _boundedDone;

    /// <summary>
    /// Up to <paramref name="maxSlices"/> slices of <paramref name="n"/> with core 0 awake and core 1 inert, in one
    /// bounded run: the slices whose ends change nothing are booked (core 0 credited <paramref name="n"/> retired
    /// instructions each, as <see cref="RunSlice"/> would), the last one is finished as a plain slice.
    /// </summary>
    private void RunBounded(int n, int maxSlices, bool cut)
    {
        var m = Machine;
        bool p1 = Parked(1);
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnRun != 0) { _boundedDone = RunSlice(n, cut); return; }
#endif
        int booked = m.RunAwakeBounded(n, maxSlices, cut ? Pads.Watch : 0);
        int k = m.BoundedSliceSlots;
        BoundedSlices += booked;
        PlainSlices++;
        if (k < n) CutSlices++;
        Retired[0] += (ulong)booked * (ulong)n;
        // The last slice: core 0's part ran in the bounded loop; core 1's turn and the sweep run as in a plain slice.
        m.FinishSlice(k, m.BoundedSliceDelta);
        if (!Parked(0)) Retired[0] += (ulong)k;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)k;
        _boundedDone = booked * n + k;
    }

    // ---- pads ------------------------------------------------------------------------------------

    /// <summary>Test oracle: sees every pad change (gpio, level, output enable, core-0 cycle) that
    /// <see cref="ScanPads"/> reports, with or without a host callback.</summary>
    internal Action<int, bool, bool, ulong>? PinObserver;

    /// <summary>Report every pad change since the last call, in cycle order, then forget them.</summary>
    public void ScanPads()
    {
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnScan != 0) { InjectOnScan = 0; throw new InvalidOperationException("injected fault inside ScanPads"); }
#endif
        Pads.Poll(AllPads); // anything that changed without IO_BANK0 noticing (PWM counters)
        // A callback may run the pads again (pin_set_input): those changes land in the next report.
        Pads.Drain(_report);
    }

    private void Report(PadEvent e)
    {
        PinObserver?.Invoke(e.Gpio, e.Level, e.OutputEnable, e.Cycle);
        var cb = PinCb; // the host may clear the callback from inside it
        if (cb == null) return;
        cb(PinUser, e.Gpio, e.Level ? 1 : 0, e.OutputEnable ? 1 : 0, e.Cycle);
    }

    public void SetAdc(int channel, double volts)
    {
        if ((uint)channel < _adc.Length)
            _adc[channel] = double.IsNaN(volts) ? (ushort)0 : (ushort)Math.Clamp(Math.Round(volts / 3.3 * 4095.0), 0, 4095);
    }

    /// <summary>PADS_BANK0 pull configuration of a pad: bit 0 = pull-up (PUE), bit 1 = pull-down (PDE).</summary>
    public int GetPull(int gpio)
    {
        var pads = Machine.PadsBank0;
        return (int)((pads.PullUpMask >> gpio) & 1u | ((pads.PullDownMask >> gpio) & 1u) << 1);
    }

    /// <summary>level 0/1 drives the pad from outside; -1 releases it: nobody drives it and the pad reads as its
    /// pulls say, tracking later PADS_BANK0 changes by itself.</summary>
    public void SetInput(int gpio, int level)
    {
        // The cycles already run belong to the old level: bring every deferred peripheral (and pin-level engine) up
        // to now first, or a later tick would sample the new level retroactively.
        Machine.SyncPeripherals();
        if (level < 0) Machine.IoBank0.ReleaseExternalInput(1u << gpio);
        else Machine.IoBank0.SetExternalInput(gpio, level != 0);
    }

    /// <summary>Core-0 cycles until the next edge a pin-level engine makes on a pad in <paramref name="watch"/>
    /// (<see cref="RP2040Machine.CyclesUntilPadEdge"/>), ulong.MaxValue when none has one coming.</summary>
    public ulong CyclesUntilEdge(uint watch)
    {
        var c = Machine.CyclesUntilPadEdge(watch & AllPads);
        return c == long.MaxValue ? ulong.MaxValue : (ulong)c;
    }

    private void EmitSerial(int channel, byte b)
    {
        var cb = SerialCb;
        if (cb == null) return;
        cb(SerialUser, channel, &b, 1);
    }

    private void EmitSerial(int channel, byte[] data)
    {
        var cb = SerialCb;
        if (cb == null || data.Length == 0) return;
        fixed (byte* p = data) cb(SerialUser, channel, p, (nuint)data.Length);
    }

    // ---- UF2 -------------------------------------------------------------------------------------

    public const uint FamilyRp2040 = 0xE48BFF56u;
    private const uint FamilyAbsolute = 0xE48BFF57u, FamilyData = 0xE48BFF58u;
    private const uint Uf2FlagNotMainFlash = 0x1u, Uf2FlagFamilyId = 0x2000u;
    private const uint FlashBase = 0x10000000u, XipWindow = 16u * 1024 * 1024;

    /// <summary>
    /// Load the RP2040 blocks of a UF2 file into flash (see st_rp2040_load_uf2). Returns the blocks loaded, or null
    /// with <paramref name="error"/> set when the file targets another chip or does not fit the flash.
    /// </summary>
    public int? LoadUf2(ReadOnlySpan<byte> uf2, out string? error)
    {
        const uint Magic0 = 0x0A324655u, Magic1 = 0x9E5D5157u;
        error = null;
        uint flashSize = Machine.Bus.FlashSize;
        uint end = 0;
        int blocks = 0;
        // First pass: the family of every block decides whether the file is ours, and where its data ends.
        for (int i = 0; i + 512 <= uf2.Length; i += 512)
        {
            var b = uf2.Slice(i, 512);
            if (U32(b, 0) != Magic0 || U32(b, 4) != Magic1) continue;
            uint flags = U32(b, 8), addr = U32(b, 12), size = U32(b, 16);
            if ((flags & Uf2FlagFamilyId) != 0)
            {
                uint family = U32(b, 28);
                if (family == FamilyAbsolute || family == FamilyData) continue; // not for the RP2040 bootrom either
                if (family != FamilyRp2040)
                {
                    error = "UF2 family 0x" + family.ToString("X8") + " (" + FamilyName(family) +
                            ") is not RP2040 (0xE48BFF56): this image is for another chip";
                    return null;
                }
            }
            if ((flags & Uf2FlagNotMainFlash) != 0 || size == 0 || size > 476) continue;
            if (addr < FlashBase || addr - FlashBase >= XipWindow) continue;
            if (addr - FlashBase + size > flashSize)
            {
                error = "UF2 block at 0x" + addr.ToString("X8") + " does not fit the " + flashSize / 1024 + " KiB flash";
                return null;
            }
            end = Math.Max(end, addr - FlashBase + size);
            blocks++;
        }
        if (blocks == 0) return 0;

        // Second pass: the image from the start of flash, erased (0xFF) where no block lands.
        var image = new byte[end];
        image.AsSpan().Fill(0xFF);
        for (int i = 0; i + 512 <= uf2.Length; i += 512)
        {
            var b = uf2.Slice(i, 512);
            if (U32(b, 0) != Magic0 || U32(b, 4) != Magic1) continue;
            uint flags = U32(b, 8), addr = U32(b, 12), size = U32(b, 16);
            if ((flags & Uf2FlagFamilyId) != 0 && U32(b, 28) != FamilyRp2040) continue;
            if ((flags & Uf2FlagNotMainFlash) != 0 || size == 0 || size > 476) continue;
            if (addr < FlashBase || addr - FlashBase >= flashSize) continue;
            b.Slice(32, (int)size).CopyTo(image.AsSpan((int)(addr - FlashBase)));
        }
        Machine.LoadFlash(image);
        return blocks;
    }

    private static string FamilyName(uint family) => family switch
    {
        0xE48BFF59u => "RP2350 Arm secure",
        0xE48BFF5Au => "RP2350 RISC-V",
        0xE48BFF5Bu => "RP2350 Arm non-secure",
        _ => "unknown family",
    };

    private static uint U32(ReadOnlySpan<byte> d, int o) =>
        (uint)d[o] | (uint)d[o + 1] << 8 | (uint)d[o + 2] << 16 | (uint)d[o + 3] << 24;
}

internal static unsafe class Exports
{
    // Upper bounds for caller-supplied sizes (anything larger is rejected, never read).
    private const uint MaxFlashBytes = 16 * 1024 * 1024;  // the XIP window
    private const uint MinFlashBytes = 4096;
    private const uint DefaultFlashBytes = 2 * 1024 * 1024; // Pico, Pico W
    private const nuint MaxUf2Bytes = 256u * 1024 * 1024;
    private const nuint MaxSerialBytes = 64u * 1024 * 1024;
    // Pin-level bus flags (ST_RP2040_BUS_*, the bit values of RP2350's ST_BUS_* and of PinLevelBus) and the board flag
    // (ST_RP2040_CYW43, RP2350's ST_RP2350_CYW43 value).
    private const uint BusFlagsMask = (uint)PinLevelBus.All;
    private const uint FlagCyw43 = 0x100;
    private const uint KnownFlags = BusFlagsMask | FlagCyw43;

    private static readonly byte[] s_version = "silicontwin-rp2040 0.1.0\0"u8.ToArray();
    private static readonly GCHandle s_versionPin = GCHandle.Alloc(s_version, GCHandleType.Pinned);
    private static readonly byte[] s_invalid = "invalid, destroyed or NULL chip handle\0"u8.ToArray();
    private static readonly GCHandle s_invalidPin = GCHandle.Alloc(s_invalid, GCHandleType.Pinned);

    // ---- handle registry ---------------------------------------------------------------------
    // A handle is an opaque scrambled counter, never a pointer: a stale, freed, double-destroyed or
    // garbage value is simply not in the table. Ids are not reused (32-bit wrap aside, and live ones are
    // skipped), so a freed handle cannot alias a newer chip. The table is concurrent: instances may be
    // created and destroyed from different threads.
    private static readonly ConcurrentDictionary<nint, Chip> s_chips = new();
    private static int s_nextId;

    private static nint NewHandle(Chip chip)
    {
        for (;;)
        {
            uint id = (uint)Interlocked.Increment(ref s_nextId);
            if (id == 0) continue;
            nint h = (nint)(int)(id * 2654435761u); // odd multiplier: a bijection, never 0
            if (s_chips.TryAdd(h, chip)) return h;
        }
    }

    internal static Chip? Lookup(nint h) => h != 0 && s_chips.TryGetValue(h, out var c) ? c : null;

    // Error text for calls that have no chip (create failed): last one wins.
    private static readonly byte* s_globalErr = (byte*)NativeMemory.AllocZeroed(Chip.ErrorCapacity);
    private static readonly object s_globalLock = new();

    private static void SetGlobalError(string msg)
    {
        lock (s_globalLock)
        {
            int n = 0;
            foreach (char c in msg)
            {
                if (n >= Chip.ErrorCapacity - 1) break;
                s_globalErr[n++] = c < 0x20 || c > 0x7E ? (byte)' ' : (byte)c;
            }
            s_globalErr[n] = 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_version", CallConvs = [typeof(CallConvCdecl)])]
    public static byte* Version() => (byte*)s_versionPin.AddrOfPinnedObject();

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_clock_hz", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ClockHz() => RP2040Machine.CLK_HZ;

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_status", CallConvs = [typeof(CallConvCdecl)])]
    public static int Status(nint h)
    {
        var chip = Lookup(h);
        return chip == null ? 2 : chip.Faulted ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static byte* LastError(nint h)
    {
        if (h == 0) return s_globalErr;
        var chip = Lookup(h);
        return chip == null || chip.Freed || chip.ErrorPtr == null ? (byte*)s_invalidPin.AddrOfPinnedObject() : chip.ErrorPtr;
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_clk_sys_hz", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ClkSysHz(nint h) => RP2040Machine.CLK_HZ;

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_time_ps", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong TimePs(nint h)
    {
        var chip = Lookup(h);
        if (chip == null) return 0;
        if (chip.Faulted) return chip.LastPs;
        try { return chip.TimePs; }
        catch (Exception e) { chip.Fault("st_rp2040_time_ps", e); return chip.LastPs; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_cycle_to_ps", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong CycleToPs(nint h, ulong cycle) => Lookup(h) == null ? 0 : Chip.CycleToPs(cycle); // pure arithmetic

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_create", CallConvs = [typeof(CallConvCdecl)])]
    public static nint Create(uint flashBytes) => CreateCore("st_rp2040_create", flashBytes, BusFlagsMask);

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_create_ex", CallConvs = [typeof(CallConvCdecl)])]
    public static nint CreateEx(uint flashBytes, uint flags) => CreateCore("st_rp2040_create_ex", flashBytes, flags);

    private static nint CreateCore(string fn, uint flashBytes, uint flags)
    {
        try
        {
            if (flashBytes != 0 && (flashBytes < MinFlashBytes || flashBytes > MaxFlashBytes || !BitOperations.IsPow2(flashBytes)))
            { SetGlobalError(fn + ": flash_bytes " + flashBytes + " is not 0 or a power of two in 4096..16777216"); return 0; }
            if ((flags & ~KnownFlags) != 0)
            { SetGlobalError(fn + ": unknown flags 0x" + flags.ToString("X") + " (valid: 0x7 ST_RP2040_BUS_*, 0x100 ST_RP2040_CYW43)"); return 0; }
            var chip = new Chip(flashBytes == 0 ? DefaultFlashBytes : flashBytes, (flags & FlagCyw43) != 0,
                                (PinLevelBus)(flags & BusFlagsMask));
            return NewHandle(chip);
        }
        catch (Exception e)
        {
            try { SetGlobalError(fn + ": " + e.GetType().Name + ": " + e.Message); } catch { }
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void Destroy(nint h)
    {
        try
        {
            if (h == 0 || !s_chips.TryRemove(h, out var chip)) return; // NULL / unknown / already destroyed: no-op
            if (chip.Running) { chip.DestroyPending = true; return; }  // destroyed from inside a callback: freed when run returns
            chip.Free();
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_load_uf2", CallConvs = [typeof(CallConvCdecl)])]
    public static int LoadUf2(nint h, byte* data, nuint len)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return -1;
        if (chip.Running) { chip.Reject("st_rp2040_load_uf2: called re-entrantly from a callback"); return -1; }
        try
        {
            if (data == null) { chip.Reject("st_rp2040_load_uf2: data is NULL"); return -1; }
            if (len > MaxUf2Bytes) { chip.Reject("st_rp2040_load_uf2: length " + len + " exceeds the 256 MiB limit"); return -1; }
            var loaded = chip.LoadUf2(new ReadOnlySpan<byte>(data, (int)len), out var error);
            if (loaded is null) { chip.Reject("st_rp2040_load_uf2: " + error); return -1; }
            return loaded.Value;
        }
        catch (Exception e) { chip.Fault("st_rp2040_load_uf2", e); return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_run", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong Run(nint h, uint instructions)
    {
        var chip = Lookup(h);
        if (chip == null) return 0;
        if (chip.Faulted) return chip.LastCycles;
        if (chip.Running) { chip.Reject("st_rp2040_run: called re-entrantly from a callback"); return chip.LastCycles; }
        chip.Running = true;
        try
        {
            chip.Run((int)Math.Min(instructions, int.MaxValue));
            return chip.LastCycles = chip.Cycles;
        }
        catch (Exception e) { chip.Fault("st_rp2040_run", e); return chip.LastCycles; }
        finally { EndRun(chip); }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_run_until_edge", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong RunUntilEdge(nint h, uint maxInstructions, uint guardInstructions, uint watch)
    {
        var chip = Lookup(h);
        if (chip == null) return 0;
        if (chip.Faulted) return chip.LastCycles;
        if (chip.Running) { chip.Reject("st_rp2040_run_until_edge: called re-entrantly from a callback"); return chip.LastCycles; }
        chip.Running = true;
        try
        {
            chip.RunUntilEdge((int)Math.Min(maxInstructions, int.MaxValue),
                              (int)Math.Min(guardInstructions, int.MaxValue), watch);
            return chip.LastCycles = chip.Cycles;
        }
        catch (Exception e) { chip.Fault("st_rp2040_run_until_edge", e); return chip.LastCycles; }
        finally { EndRun(chip); }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_cycles_until_edge", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong CyclesUntilEdge(nint h, uint watch)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return ulong.MaxValue;
        try { return chip.CyclesUntilEdge(watch); }
        catch (Exception e) { chip.Fault("st_rp2040_cycles_until_edge", e); return ulong.MaxValue; }
    }

    private static void EndRun(Chip chip)
    {
        chip.Running = false;
        if (chip.DestroyPending) { chip.DestroyPending = false; chip.Free(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_cycles", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong Cycles(nint h)
    {
        var chip = Lookup(h);
        if (chip == null) return 0;
        if (chip.Faulted) return chip.LastCycles;
        try { return chip.Cycles; }
        catch (Exception e) { chip.Fault("st_rp2040_cycles", e); return chip.LastCycles; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_instructions", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong Instructions(nint h, int core)
    {
        var chip = Lookup(h);
        if (chip == null) return 0;
        return core == 0 || core == 1 ? chip.Retired[core] : chip.Retired[0] + chip.Retired[1];
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_pin_get", CallConvs = [typeof(CallConvCdecl)])]
    public static int PinGet(nint h, int gpio, int* outputEnable)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) { if (outputEnable != null) *outputEnable = 0; return 0; }
        try
        {
            if ((uint)gpio >= Chip.GpioCount)
            {
                chip.Reject("st_rp2040_pin_get: gpio " + gpio + " out of range 0..29");
                if (outputEnable != null) *outputEnable = 0;
                return 0;
            }
            if (outputEnable != null) *outputEnable = chip.Machine.IoBank0.GetPadOutputEnable(gpio) ? 1 : 0;
            return chip.Machine.IoBank0.GetPadOutputLevel(gpio) ? 1 : 0;
        }
        catch (Exception e) { chip.Fault("st_rp2040_pin_get", e); if (outputEnable != null) *outputEnable = 0; return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_pin_get_pull", CallConvs = [typeof(CallConvCdecl)])]
    public static int PinGetPull(nint h, int gpio)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return 0;
        try
        {
            if ((uint)gpio >= Chip.GpioCount) { chip.Reject("st_rp2040_pin_get_pull: gpio " + gpio + " out of range 0..29"); return 0; }
            return chip.GetPull(gpio);
        }
        catch (Exception e) { chip.Fault("st_rp2040_pin_get_pull", e); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_pin_set_input", CallConvs = [typeof(CallConvCdecl)])]
    public static void PinSetInput(nint h, int gpio, int level)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return;
        try
        {
            if ((uint)gpio >= Chip.GpioCount) { chip.Reject("st_rp2040_pin_set_input: gpio " + gpio + " out of range 0..29"); return; }
            if (level < -1 || level > 1) { chip.Reject("st_rp2040_pin_set_input: level " + level + " is not -1, 0 or 1"); return; }
            chip.SetInput(gpio, level);
        }
        catch (Exception e) { chip.Fault("st_rp2040_pin_set_input", e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_adc_set_voltage", CallConvs = [typeof(CallConvCdecl)])]
    public static void AdcSetVoltage(nint h, int channel, double volts)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return;
        try
        {
            if ((uint)channel > 3) { chip.Reject("st_rp2040_adc_set_voltage: channel " + channel + " out of range 0..3"); return; }
            chip.SetAdc(channel, volts);
        }
        catch (Exception e) { chip.Fault("st_rp2040_adc_set_voltage", e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_set_pin_callback", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetPinCallback(nint h, delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void> cb, nint user)
    {
        var chip = Lookup(h);
        if (chip == null) return;
        chip.PinCb = cb; chip.PinUser = user; // NULL disables
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_set_serial_callback", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetSerialCallback(nint h, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void> cb, nint user)
    {
        var chip = Lookup(h);
        if (chip == null) return;
        chip.SerialCb = cb; chip.SerialUser = user;
    }

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_serial_write", CallConvs = [typeof(CallConvCdecl)])]
    public static void SerialWrite(nint h, int channel, byte* data, nuint len)
    {
        var chip = Lookup(h);
        if (chip == null || chip.Faulted) return;
        try
        {
            if (len == 0) return;
            if (data == null) { chip.Reject("st_rp2040_serial_write: data is NULL"); return; }
            if (len > MaxSerialBytes) { chip.Reject("st_rp2040_serial_write: length " + len + " exceeds the 64 MiB limit"); return; }
            if ((uint)channel > 2) { chip.Reject("st_rp2040_serial_write: invalid channel " + channel); return; }
            var span = new ReadOnlySpan<byte>(data, (int)len);
            switch (channel)
            {
                case 0: foreach (var b in span) chip.Machine.Uart0.InjectByte(b); break;
                case 1: foreach (var b in span) chip.Machine.Uart1.InjectByte(b); break;
                case 2: chip.Cdc.SendSerialBytes(span); break;
            }
        }
        catch (Exception e) { chip.Fault("st_rp2040_serial_write", e); }
    }

#if ST_ENABLE_TEST_HOOKS
    // TEST ONLY (compiled with -p:StTestHooks=true, never in Release packages): make the core throw so the
    // fault path can be exercised end to end. mode 1 = InvalidOperationException inside the next run,
    // 2 = NullReferenceException inside the next run, 3 = exception while scanning pads after the next run,
    // 4 = exception raised directly in this export. Returns 0 if armed / done, -1 for a bad handle or mode.
    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_test_inject_fault", CallConvs = [typeof(CallConvCdecl)])]
    public static int TestInjectFault(nint h, int mode)
    {
        var chip = Lookup(h);
        if (chip == null) return -1;
        try
        {
            switch (mode)
            {
                case 1: chip.InjectOnRun = 1; return 0;
                case 2: chip.InjectOnRun = 2; return 0;
                case 3: chip.InjectOnScan = 1; return 0;
                case 4: throw new InvalidOperationException("injected fault in export");
                default: return -1;
            }
        }
        catch (Exception e) { chip.Fault("st_rp2040_test_inject_fault", e); return 0; }
    }
#endif
}

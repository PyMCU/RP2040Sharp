using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RP2040.Peripherals;
using RP2040.Peripherals.Usb;
using RP2040Sharp.Wireless.Cyw43;

namespace SiliconTwin.Native;

/// <summary>A pad change: the pad's effective output and the core-0 cycle it changed at.</summary>
internal readonly record struct PadEvent(int Gpio, bool Level, bool OutputEnable, ulong Cycle);

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
    private readonly bool[] _lastLevel = new bool[GpioCount];
    private readonly bool[] _lastOe = new bool[GpioCount];

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

    public Chip(uint flashBytes, bool cyw43)
    {
        Machine = new RP2040Machine(flashBytes) { DeferPeripheralTicks = true };
        Machine.Adc.ReadChannel = ch => (uint)ch < (uint)_adc.Length ? _adc[ch] : ch == 4 ? TempSensor27C : (ushort)0;
        Machine.Uart0.OnByteTransmit = b => EmitSerial(0, b);
        Machine.Uart1.OnByteTransmit = b => EmitSerial(1, b);
        Cdc = new UsbCdcHost(Machine.Usb);
        Cdc.OnSerialData = data => EmitSerial(2, data);
        // Every pad change that goes through IO_BANK0 (SIO writes of either core, PIO, FUNCSEL / override / PADS
        // writes, DMA into SIO) arrives here while it happens, so it is stamped with its own cycle. PWM-muxed pads
        // follow the counter instead and are polled (see PollPads).
        Machine.IoBank0.PadsChanged += OnPadsChanged;
        // A bounded run books slice ends without coming back here: poll the watched PWM pads at each of them.
        Machine.BoundaryCheck = () =>
        {
            uint pwm = Machine.IoBank0.PwmMuxedPins & _watch;
            if (pwm != 0) PollPads(pwm);
            return _watchedChanged;
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

    // Core-0 / core-1 cycle counts at the start of the slice in flight, and whether one is in flight.
    private long _slice0Start, _slice1Start;
    private bool _inSlice;

    /// <summary>
    /// The core-0 cycle of whatever the machine is doing right now. Core 0 runs its whole slice first, then core 1,
    /// then the peripherals tick: a write by core 0 happens at its own cycle count, one by core 1 at the slice start
    /// plus the cycles core 1 has run in it, and anything a peripheral does at the end of the slice.
    /// </summary>
    private ulong NowCycle()
    {
        var m = Machine;
        if (_inSlice && m.Sio.GetActiveCoreId?.Invoke() == 1)
            return (ulong)(_slice0Start + (m.Cpu1.Cycles - _slice1Start));
        return (ulong)m.Cpu.Cycles;
    }

    /// <summary>
    /// One machine slice of <paramref name="n"/> instructions per core, then the peripheral ticks. The core has no
    /// retired-instruction counter and credits a sleeping core's slots as cycles, so a slice is credited to a core
    /// only if that core was neither asleep (WFI/WFE), locked up nor (core 1) unlaunched at its start and at its end:
    /// a lower bound, exact for CPU-bound code, low by up to one slice per sleep/wake transition.
    /// </summary>
    private void RunSlice(int n)
    {
        bool p0 = Parked(0), p1 = Parked(1);
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnRun == 1) { InjectOnRun = 0; throw new InvalidOperationException("injected fault inside Run"); }
        if (InjectOnRun == 2) { InjectOnRun = 0; Chip? none = null; _ = none!.Machine; }
#endif
        _slice0Start = Machine.Cpu.Cycles;
        _slice1Start = Machine.Cpu1.Cycles;
        _inSlice = true;
        try { Machine.Run(n); }
        finally { _inSlice = false; }
        if (!p0 && !Parked(0)) Retired[0] += (ulong)n;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)n;
    }

    /// <summary>Run <paramref name="max"/> instructions per core in peripheral-quantum slices (st_rp2040_run).</summary>
    public void Run(int max) => RunUntilEdge(max, 0, 0);

    // Pads RunUntilEdge watches in the call in flight (0 outside one), and whether one of them changed.
    private uint _watch;
    private bool _watchedChanged;

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
        _watch = armed ? watch & AllPads : 0;
        _watchedChanged = false;
        var m = Machine;
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
                        RunBounded(slice, whole);
                        done += _boundedDone;
                        if (armed)
                        {
                            uint pwmB = m.IoBank0.PwmMuxedPins & _watch;
                            if (pwmB != 0) PollPads(pwmB);
                            if (_watchedChanged) break;
                        }
                        continue;
                    }
                }
                PlainSlices++;
                RunSlice(n);
                done += n;
                if (!armed) continue;
                // PWM-muxed pads move with the counter, which IO_BANK0 never reports: compare the watched ones.
                uint pwm = m.IoBank0.PwmMuxedPins & _watch;
                if (pwm != 0) PollPads(pwm);
                if (_watchedChanged) break;
            }
        }
        finally { _watch = 0; }
        ScanPads();
    }

    private int _boundedDone;

    /// <summary>
    /// Up to <paramref name="maxSlices"/> slices of <paramref name="n"/> with core 0 awake and core 1 inert, in one
    /// bounded run: the slices whose ends change nothing are booked (core 0 credited <paramref name="n"/> retired
    /// instructions each, as <see cref="RunSlice"/> would), the last one is finished as a plain slice.
    /// </summary>
    private void RunBounded(int n, int maxSlices)
    {
        var m = Machine;
        bool p1 = Parked(1);
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnRun != 0) { _boundedDone = 0; RunSlice(n); _boundedDone = n; return; }
#endif
        int booked = m.RunAwakeBounded(n, maxSlices);
        BoundedSlices += booked;
        PlainSlices++;
        Retired[0] += (ulong)booked * (ulong)n;
        // The last slice: core 0's part ran in the bounded loop; core 1's turn and the sweep run as in a plain slice.
        _slice0Start = m.BoundedSliceStart;
        _slice1Start = m.Cpu1.Cycles;
        _inSlice = true;
        try { m.FinishSlice(n, m.BoundedSliceDelta); }
        finally { _inSlice = false; }
        if (!Parked(0)) Retired[0] += (ulong)n;
        if (!p1 && !Parked(1)) Retired[1] += (ulong)n;
        _boundedDone = (booked + 1) * n;
    }

    // ---- pads ------------------------------------------------------------------------------------

    // Changes found since the last report, in the order they happened per core (core 0's, then core 1's, then the
    // peripherals' of each slice): sorted by cycle when reported.
    private List<PadEvent> _pending = new();
    private List<PadEvent> _reporting = new();

    private void OnPadsChanged(uint mask)
    {
        ulong cycle = NowCycle();
        Record(mask & AllPads, cycle);
    }

    /// <summary>Compare the pads in <paramref name="mask"/> with what was last recorded and record the changes at
    /// <paramref name="cycle"/>.</summary>
    private void Record(uint mask, ulong cycle)
    {
        var io = Machine.IoBank0;
        for (; mask != 0; mask &= mask - 1)
        {
            int g = BitOperations.TrailingZeroCount(mask);
            bool oe = io.GetPadOutputEnable(g), lvl = io.GetPadOutputLevel(g);
            if (oe == _lastOe[g] && lvl == _lastLevel[g]) continue;
            _lastOe[g] = oe; _lastLevel[g] = lvl;
            _pending.Add(new PadEvent(g, lvl, oe, cycle));
            if ((_watch >> g & 1) != 0) { _watchedChanged = true; Machine.EndBoundedRun = true; }
        }
    }

    private void PollPads(uint mask) => Record(mask, (ulong)Machine.Cpu.Cycles);

    /// <summary>Test oracle: sees every pad change (gpio, level, output enable, core-0 cycle) that
    /// <see cref="ScanPads"/> reports, with or without a host callback.</summary>
    internal Action<int, bool, bool, ulong>? PinObserver;

    /// <summary>Report every pad change since the last call, in cycle order, then forget them.</summary>
    public void ScanPads()
    {
#if ST_ENABLE_TEST_HOOKS
        if (InjectOnScan != 0) { InjectOnScan = 0; throw new InvalidOperationException("injected fault inside ScanPads"); }
#endif
        PollPads(AllPads); // anything that changed without IO_BANK0 noticing (PWM counters)
        if (_pending.Count == 0) return;
        // Swap first: a callback may run the pads again (pin_set_input) and record into a fresh list.
        (_reporting, _pending) = (_pending, _reporting);
        var events = _reporting;
        ulong now = (ulong)Machine.Cpu.Cycles;
        SortByCycle(events);
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            // Core 1 may run more cycles in a slice than core 0: nothing is reported past core 0's own clock.
            ulong cycle = Math.Min(e.Cycle, now);
            PinObserver?.Invoke(e.Gpio, e.Level, e.OutputEnable, cycle);
            var cb = PinCb; // the host may clear the callback from inside it
            if (cb == null) continue;
            cb(PinUser, e.Gpio, e.Level ? 1 : 0, e.OutputEnable ? 1 : 0, cycle);
        }
        events.Clear();
    }

    // Stable insertion sort: the list is already in order but for core 1's changes, which follow core 0's.
    private static void SortByCycle(List<PadEvent> events)
    {
        for (int i = 1; i < events.Count; i++)
        {
            var e = events[i];
            int j = i - 1;
            if (events[j].Cycle <= e.Cycle) continue;
            while (j >= 0 && events[j].Cycle > e.Cycle) { events[j + 1] = events[j]; j--; }
            events[j + 1] = e;
        }
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
        if (level < 0) Machine.IoBank0.ReleaseExternalInput(1u << gpio);
        else Machine.IoBank0.SetExternalInput(gpio, level != 0);
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
    private const uint FlagCyw43 = 1;
    private const uint KnownFlags = FlagCyw43;

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
    public static nint Create(uint flashBytes) => CreateCore("st_rp2040_create", flashBytes, 0);

    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_create_ex", CallConvs = [typeof(CallConvCdecl)])]
    public static nint CreateEx(uint flashBytes, uint flags) => CreateCore("st_rp2040_create_ex", flashBytes, flags);

    private static nint CreateCore(string fn, uint flashBytes, uint flags)
    {
        try
        {
            if (flashBytes != 0 && (flashBytes < MinFlashBytes || flashBytes > MaxFlashBytes || !BitOperations.IsPow2(flashBytes)))
            { SetGlobalError(fn + ": flash_bytes " + flashBytes + " is not 0 or a power of two in 4096..16777216"); return 0; }
            if ((flags & ~KnownFlags) != 0)
            { SetGlobalError(fn + ": unknown flags 0x" + flags.ToString("X") + " (valid: 1 ST_RP2040_CYW43)"); return 0; }
            var chip = new Chip(flashBytes == 0 ? DefaultFlashBytes : flashBytes, (flags & FlagCyw43) != 0);
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

    // No pin-level engines in this core: no engine edge is ever coming.
    [UnmanagedCallersOnly(EntryPoint = "st_rp2040_cycles_until_edge", CallConvs = [typeof(CallConvCdecl)])]
    public static ulong CyclesUntilEdge(nint h, uint watch) => ulong.MaxValue;

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

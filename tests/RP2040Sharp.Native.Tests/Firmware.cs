namespace RP2040Sharp.Native.Tests;

/// <summary>Firmware for the tests: hand-assembled Thumb programs, UF2 packing and the MicroPython images.</summary>
internal static class Firmware
{
    public const uint FamilyRp2040 = 0xE48BFF56u;
    public const uint FamilyRp2350Arm = 0xE48BFF59u;

    /// <summary>Pack <paramref name="image"/> (from the start of flash) into 256-byte UF2 blocks.</summary>
    public static byte[] Uf2(byte[] image, uint family = FamilyRp2040, uint baseAddr = 0x10000000u)
    {
        int blocks = (image.Length + 255) / 256;
        var uf2 = new byte[blocks * 512];
        for (int i = 0; i < blocks; i++)
        {
            var b = uf2.AsSpan(i * 512, 512);
            uint[] w = [0x0A324655u, 0x9E5D5157u, 0x2000u, baseAddr + (uint)i * 256, 256, (uint)i, (uint)blocks, family];
            for (int k = 0; k < w.Length; k++) BitConverter.TryWriteBytes(b.Slice(k * 4), w[k]);
            image.AsSpan(i * 256, Math.Min(256, image.Length - i * 256)).CopyTo(b.Slice(32));
            BitConverter.TryWriteBytes(b.Slice(508), 0x0AB16F30u);
        }
        return uf2;
    }

    /// <summary>
    /// GP15 as a square wave timed by the microsecond TIMER (busy-wait on TIMERAWL, no interrupts): GP15 is routed to
    /// SIO, set high and enabled, then repeatedly held high for <paramref name="highUs"/> and low for
    /// <paramref name="lowUs"/>. Runs from flash at 0x10000000 (no boot2, no vector table).
    /// </summary>
    public static byte[] SquareWave(byte highUs, byte lowUs)
    {
        var a = new Thumb();
        a.LdrLit(0, 0x4001407Cu);        // IO_BANK0 GPIO15_CTRL
        a.Emit(0x2405);                  // movs r4, #5 (FUNCSEL SIO)
        a.Emit(0x6004);                  // str r4, [r0]
        a.LdrLit(1, 0xD0000000u);        // SIO
        a.LdrLit(2, 1u << 15);
        a.Emit(Str(2, 1, 0x14));         // GPIO_OUT_SET
        a.Emit(Str(2, 1, 0x24));         // GPIO_OE_SET
        a.LdrLit(3, 0x40054028u);        // TIMERAWL
        int top = a.Here;
        a.Emit(Ldr(5, 3, 0));            // t0
        a.Emit((ushort)(0x3500 | highUs)); // adds r5, #high
        int waitHigh = a.Here;
        a.Emit(Ldr(6, 3, 0));
        a.Emit(0x1B77);                  // subs r7, r6, r5
        a.Bcc(4, waitHigh);              // bmi
        a.Emit(Str(2, 1, 0x18));         // GPIO_OUT_CLR
        a.Emit(Ldr(5, 3, 0));
        a.Emit((ushort)(0x3500 | lowUs));
        int waitLow = a.Here;
        a.Emit(Ldr(6, 3, 0));
        a.Emit(0x1B77);
        a.Bcc(4, waitLow);
        a.Emit(Str(2, 1, 0x14));         // GPIO_OUT_SET
        a.B(top);
        return a.Build();
    }

    /// <summary>
    /// Core 0 goes to sleep in WFI with nothing to wake it, after routing GP15 to SIO and driving it low.
    /// </summary>
    public static byte[] SleepForever()
    {
        var a = new Thumb();
        a.LdrLit(0, 0x4001407Cu);
        a.Emit(0x2405);
        a.Emit(0x6004);
        a.LdrLit(1, 0xD0000000u);
        a.LdrLit(2, 1u << 15);
        a.Emit(Str(2, 1, 0x24));         // OE_SET: GP15 driven low
        int top = a.Here;
        a.Emit(0xBF30);                  // wfi
        a.B(top);
        return a.Build();
    }

    /// <summary>Core 0 computes forever (an add and an SRAM load/store per pass), touching no pad and no peripheral.</summary>
    public static byte[] BusyLoop()
    {
        var a = new Thumb();
        a.LdrLit(1, 0x20001000u);        // SRAM scratch word
        int top = a.Here;
        a.Emit(0x3001);                  // adds r0, #1
        a.Emit(Ldr(2, 1, 0));            // ldr r2, [r1]
        a.Emit(0x1812);                  // adds r2, r2, r0
        a.Emit(Str(2, 1, 0));            // str r2, [r1]
        a.B(top);
        return a.Build();
    }

    private const uint Sio = 0xD0000000u, Gp15Mask = 1u << 15;

    /// <summary>Routes GP15 to SIO and drives it low; VTOR = 0x20000000 (an SRAM vector table).</summary>
    private static void Prologue(Thumb a)
    {
        a.LdrLit(0, 0x4001407Cu);        // GPIO15_CTRL
        a.Emit(0x2405);                  // movs r4, #5 (SIO)
        a.Emit(0x6004);                  // str r4, [r0]
        a.LdrLit(0, Sio);
        a.LdrLit(1, Gp15Mask);
        a.Emit(Str(1, 0, 0x24));         // GPIO_OE_SET
        a.LdrLit(0, 0xE000ED08u);        // SCB_VTOR
        a.LdrLit(1, 0x20000000u);
        a.Emit(Str(1, 0, 0));
    }

    /// <summary>Install the handler whose (Thumb) address is literal <paramref name="handlerLit"/> as <paramref name="exception"/>.</summary>
    private static void Vector(Thumb a, int exception, int handlerLit)
    {
        a.LdrLit(0, 0x20000000u + (uint)exception * 4);
        a.LdrLitRef(1, handlerLit);
        a.Emit(Str(1, 0, 0));
    }

    /// <summary>Handler epilogue: toggle GP15 and return.</summary>
    private static void ToggleAndReturn(Thumb a)
    {
        a.LdrLit(0, Sio);
        a.LdrLit(1, Gp15Mask);
        a.Emit(Str(1, 0, 0x1C));         // GPIO_OUT_XOR
        a.Emit(0x4770);                  // bx lr
    }

    private static int HandlerLit(Thumb a, int handler) => a.Lit(0x10000001u + (uint)handler * 2);

    /// <summary>
    /// TIMER alarm 0 every <paramref name="periodUs"/> microseconds: the handler acknowledges it, re-arms it one period
    /// later and toggles GP15; core 0 sleeps in WFI in between (or, with <paramref name="poll"/>, polls TIMERAWL into
    /// SRAM).
    /// </summary>
    public static byte[] AlarmIsr(byte periodUs, bool poll = false)
    {
        var a = new Thumb();
        int toMain = a.Placeholder();
        int handler = a.Here;
        a.LdrLit(0, 0x40054034u);        // INTR
        a.Emit(0x2101);                  // movs r1, #1
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x40054010u);        // ALARM0
        a.Emit(Ldr(1, 0, 0));
        a.Emit((ushort)(0x3100 | periodUs)); // adds r1, #period
        a.Emit(Str(1, 0, 0));
        ToggleAndReturn(a);
        a.PatchB(toMain, a.Here);
        Prologue(a);
        Vector(a, 16, HandlerLit(a, handler));
        a.LdrLit(0, 0x40054038u);        // INTE
        a.Emit(0x2101);
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0xE000E100u);        // NVIC_ISER: IRQ 0
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x40054028u);        // TIMERAWL
        a.Emit(Ldr(1, 0, 0));
        a.Emit((ushort)(0x3100 | periodUs));
        a.LdrLit(0, 0x40054010u);
        a.Emit(Str(1, 0, 0));            // ALARM0 = now + period
        EndLoop(a, poll, 0x40054028u);
        return a.Build();
    }

    /// <summary>
    /// PWM slice 7 on GP14 (DIV 3, TOP 1499, CC 500): the wrap interrupt acknowledges, adds 7 to CC and toggles GP15;
    /// core 0 sleeps in WFI (or, with <paramref name="poll"/>, polls CTR into SRAM).
    /// </summary>
    public static byte[] PwmIsr(bool poll = false)
    {
        var a = new Thumb();
        int toMain = a.Placeholder();
        int handler = a.Here;
        a.LdrLit(0, 0x400500A4u);        // INTR
        a.Emit(0x2180);                  // movs r1, #0x80 (slice 7)
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x40050098u);        // CH7_CC
        a.Emit(Ldr(1, 0, 0));
        a.Emit(0x3107);                  // adds r1, #7
        a.Emit(Str(1, 0, 0));
        ToggleAndReturn(a);
        a.PatchB(toMain, a.Here);
        Prologue(a);
        Vector(a, 16 + 4, HandlerLit(a, handler));
        a.LdrLit(0, 0x40014074u);        // GPIO14_CTRL
        a.Emit(0x2404);                  // movs r4, #4 (PWM)
        a.Emit(0x6004);
        a.LdrLit(0, 0x40050090u);        // CH7_DIV = 3.0
        a.Emit(0x2130);                  // movs r1, #0x30
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x4005009Cu);        // CH7_TOP
        a.LdrLit(1, 1499);
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x40050098u);        // CH7_CC
        a.LdrLit(1, 500);
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x400500A8u);        // INTE
        a.Emit(0x2180);
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0xE000E100u);        // NVIC_ISER: IRQ 4
        a.Emit(0x2110);                  // movs r1, #0x10
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0x4005008Cu);        // CH7_CSR = EN
        a.Emit(0x2101);
        a.Emit(Str(1, 0, 0));
        EndLoop(a, poll, 0x40050094u);
        return a.Build();
    }

    /// <summary>SysTick on the core clock every 1000 cycles (TICKINT): the handler counts into SRAM and toggles GP15.</summary>
    public static byte[] SysTickIsr()
    {
        var a = new Thumb();
        int toMain = a.Placeholder();
        int handler = a.Here;
        a.LdrLit(0, 0x20001000u);
        a.Emit(Ldr(1, 0, 0));
        a.Emit(0x3101);                  // adds r1, #1
        a.Emit(Str(1, 0, 0));
        ToggleAndReturn(a);
        a.PatchB(toMain, a.Here);
        Prologue(a);
        Vector(a, 15, HandlerLit(a, handler));
        a.LdrLit(0, 0xE000E014u);        // SYST_RVR
        a.LdrLit(1, 999);
        a.Emit(Str(1, 0, 0));
        a.LdrLit(0, 0xE000E010u);        // SYST_CSR = ENABLE | TICKINT | CLKSOURCE
        a.Emit(0x2107);
        a.Emit(Str(1, 0, 0));
        EndLoop(a, false, 0);
        return a.Build();
    }

    /// <summary>WFI forever, or copy the word at <paramref name="pollAddr"/> into SRAM 0x20001004 forever.</summary>
    private static void EndLoop(Thumb a, bool poll, uint pollAddr)
    {
        if (!poll)
        {
            int top = a.Here;
            a.Emit(0xBF30);              // wfi
            a.B(top);
            return;
        }
        a.LdrLit(2, pollAddr);
        a.LdrLit(3, 0x20001004u);
        int loop = a.Here;
        a.Emit(Ldr(1, 2, 0));
        a.Emit(Str(1, 3, 0));
        a.B(loop);
    }

    private static ushort Str(int rt, int rn, int imm) => (ushort)(0x6000 | (imm >> 2) << 6 | rn << 3 | rt);
    private static ushort Ldr(int rt, int rn, int imm) => (ushort)(0x6800 | (imm >> 2) << 6 | rn << 3 | rt);

    /// <summary>A tiny Thumb-1 assembler: backward branches and a literal pool after the code.</summary>
    private sealed class Thumb
    {
        private readonly List<ushort> _code = [];
        private readonly List<(int At, int Rt, uint Value)> _lits = [];

        public int Here => _code.Count;
        public void Emit(ushort op) => _code.Add(op);

        public void LdrLit(int rt, uint value) { _lits.Add((_code.Count, rt, value)); _code.Add(0); }

        // Literals whose value is known only later (a handler's address).
        private readonly List<uint> _shared = [];
        private readonly List<(int At, int Rt, int Lit)> _sharedRefs = [];
        public int Lit(uint value) { _shared.Add(value); return _shared.Count - 1; }
        public void SetLit(int lit, uint value) => _shared[lit] = value;
        public void LdrLitRef(int rt, int lit) { _sharedRefs.Add((_code.Count, rt, lit)); _code.Add(0); }

        /// <summary>A forward branch, patched with <see cref="PatchB"/>.</summary>
        public int Placeholder() { _code.Add(0xE000); return _code.Count - 1; }

        public void PatchB(int at, int target)
        {
            int off = target * 2 - (at * 2 + 4);
            _code[at] = (ushort)(0xE000 | ((off >> 1) & 0x7FF));
        }

        public void Bcc(int cond, int target)
        {
            int off = target * 2 - (_code.Count * 2 + 4);
            _code.Add((ushort)(0xD000 | cond << 8 | ((off >> 1) & 0xFF)));
        }

        public void B(int target)
        {
            int off = target * 2 - (_code.Count * 2 + 4);
            _code.Add((ushort)(0xE000 | ((off >> 1) & 0x7FF)));
        }

        public byte[] Build()
        {
            if (_code.Count % 2 != 0) _code.Add(0xBF00); // nop: word-align the pool
            foreach (var (at, rt, lit) in _sharedRefs) _lits.Add((at, rt, _shared[lit]));
            int pool = _code.Count * 2;
            var bytes = new byte[pool + _lits.Count * 4];
            for (int i = 0; i < _lits.Count; i++)
            {
                var (at, rt, value) = _lits[i];
                int lit = pool + i * 4;
                int pc = (at * 2 + 4) & ~3;
                _code[at] = (ushort)(0x4800 | rt << 8 | ((lit - pc) >> 2));
                BitConverter.TryWriteBytes(bytes.AsSpan(lit), value);
            }
            for (int i = 0; i < _code.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), _code[i]);
            return bytes;
        }
    }

    // ---- MicroPython images --------------------------------------------------------------------

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "RP2040Sharp.sln"))) return d.FullName;
        return null;
    }

    /// <summary>MicroPython v1.28.0 for the Pico (RPI_PICO): RP2040_MICROPYTHON_UF2, else the copy the repository tracks.</summary>
    public static string? MicroPythonPico()
    {
        var env = Environment.GetEnvironmentVariable("RP2040_MICROPYTHON_UF2");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        var root = RepoRoot();
        var p = root is null ? null : Path.Combine(root, "tests", "RP2040Sharp.IntegrationTests", "Firmware", "python", "micropython-v1.28.0.uf2");
        return p is not null && File.Exists(p) ? p : null;
    }

    /// <summary>MicroPython v1.28.0 for the Pico W (RPI_PICO_W): RP2040_MICROPYTHON_PICOW_UF2, else the integration
    /// tests' firmware cache. Never downloaded here.</summary>
    public static string? MicroPythonPicoW()
    {
        var env = Environment.GetEnvironmentVariable("RP2040_MICROPYTHON_PICOW_UF2");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        var p = Path.Combine(Path.GetTempPath(), "rp2040sharp-firmware-cache", "micropython-picow-v1.28.0.uf2");
        return File.Exists(p) ? p : null;
    }
}

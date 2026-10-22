namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Hand-assembled Thumb programs that drive the hardware SPI, UART and I2C blocks with their functions routed to pads
/// (pin-level buses), for the differential, lockstep and event-guard tests. They poll (or sleep in WFI for an
/// interrupt, or let the DMA move the data), store what they receive in SRAM and count completed bursts in R7.
/// No timer, no SysTick: what the CPU sees depends only on the pads and the bus engines.
/// </summary>
internal static class PinLevelFirmware
{
    public const uint RxBuffer = 0x20010000u;    // words received, in order
    public const uint TxBuffer = 0x20008000u;    // DMA source words / bytes
    public const uint Counter = 0x20000F00u;     // interrupt-mode word count
    public const uint IsrPtr = 0x20000F04u;      // interrupt-mode store pointer

    private const uint IoBank0 = 0x40014000u, Spi0 = 0x4003C000u, Uart0 = 0x40034000u, I2c0 = 0x40044000u;
    private const uint Dma = 0x50000000u, Nvic = 0xE000E100u, Vtor = 0xE000ED08u;

    /// <summary>The words an SPI burst program sends: 0x5A, then +0x13 per word, masked to <paramref name="dss"/> bits.</summary>
    public static uint SpiWord(int index, int dss) => (uint)(0x5A + 0x13 * index) & ((1u << dss) - 1);

    public enum SpiVariant { Poll, Dma, Irq }

    /// <summary>
    /// SPI0 on GP16 (RX / MISO), GP17 (CSn), GP18 (SCK), GP19 (TX / MOSI), FUNCSEL 1, master, the given SPO/SPH mode,
    /// DSS and CPSDVSR (SCR 0). Bursts of 8 words (4 in IRQ mode), then a delay loop of <paramref name="gap"/>
    /// iterations, <paramref name="bursts"/> times; then it spins. Poll: fill the FIFO, wait for BSY to clear, drain RX.
    /// Dma: channel 0 TX (DREQ 16), channel 1 RX (DREQ 17), wait for channel 1 to finish. Irq: an SPI0_IRQ (RXIM, the RX
    /// FIFO at least half full) handler drains RX while core 0 sleeps in WFI.
    /// </summary>
    public static byte[] SpiBursts(int mode, int dss, int cpsdvsr, SpiVariant variant, int gap = 40, int bursts = 3)
    {
        int words = variant == SpiVariant.Irq ? 4 : 8;
        var a = new ThumbAsm();
        if (variant == SpiVariant.Irq) a.Ldr(4, 0x20042000u).MovSp(4);   // a stack for the handler
        foreach (var pin in new[] { 16, 17, 18, 19 }) a.Ldr(0, IoBank0 + (uint)pin * 8 + 4).Movs(1, 1).Str(1, 0, 0);
        a.Ldr(0, Spi0)
         .Ldr(1, (uint)(dss - 1) | (uint)(mode >> 1) << 6 | (uint)(mode & 1) << 7).Str(1, 0, 0x00)
         .Ldr(1, (uint)cpsdvsr).Str(1, 0, 0x10);
        if (variant == SpiVariant.Dma) a.Movs(1, 3).Str(1, 0, 0x24);      // SSPDMACR: TXDMAE | RXDMAE
        if (variant == SpiVariant.Irq)
        {
            a.Ldr(1, 0x20000000u).Ldr(2, Vtor).Str(1, 2, 0)                 // VTOR = SRAM table
             .Ldr(2, 0x20000000u + (16 + 18) * 4).AdrLabel(3, "isr").Str(3, 2, 0)
             .Ldr(2, Counter).Movs(3, 0).Str(3, 2, 0)
             .Ldr(2, IsrPtr).Ldr(3, RxBuffer).Str(3, 2, 0)
             .Movs(1, 4).Str(1, 0, 0x14)                                      // SSPIMSC: RXIM
             .Ldr(2, Nvic).Ldr(1, 1u << 18).Str(1, 2, 0)                      // ISER: SPI0_IRQ
             .Cpsie();
        }
        a.Movs(1, 2).Str(1, 0, 0x04);                                         // SSPCR1: SSE
        a.Ldr(6, RxBuffer).Movs(3, 0x5A).Movs(7, 0);
        if (variant == SpiVariant.Irq) a.Movs(6, 0);                         // R6 counts the words expected
        a.Label("burst");
        switch (variant)
        {
            case SpiVariant.Poll:
                a.Movs(2, (byte)words)
                 .Label("fill").Str(3, 0, 8).Adds(3, 0x13).Subs(2, 1).B(ThumbAsm.NE, "fill")
                 .Label("wait").Ldr(1, 0, 0x0C).Movs(4, 0x10).Tst(1, 4).B(ThumbAsm.NE, "wait")
                 .Movs(2, (byte)words)
                 .Label("drain").Ldr(1, 0, 8).Str(1, 6, 0).Adds(6, 4).Subs(2, 1).B(ThumbAsm.NE, "drain");
                break;
            case SpiVariant.Dma:
            {
                // Stage the burst in TxBuffer (bytes for DSS <= 8, halfwords above), then start RX, then TX.
                bool half = dss > 8;
                a.Ldr(5, TxBuffer).Movs(2, (byte)words)
                 .Label("stage");
                if (half) a.Strh(3, 5, 0).Adds(5, 2); else a.Strb(3, 5, 0).Adds(5, 1);
                a.Adds(3, 0x13).Subs(2, 1).B(ThumbAsm.NE, "stage");
                uint size = half ? 1u << 2 : 0;
                a.Ldr(5, Dma + 0x40)                                                  // channel 1: SSPDR -> RxBuffer
                 .Ldr(1, Spi0 + 8).Str(1, 5, 0x0)
                 .Str(6, 5, 0x4)
                 .Movs(1, (byte)words).Str(1, 5, 0x8)
                 .Ldr(1, 1u | size | 1u << 5 | 1u << 11 | 17u << 15).Str(1, 5, 0xC)   // EN, INCR_WRITE, CHAIN_TO 1, TREQ 17
                 .Ldr(5, Dma)                                                         // channel 0: TxBuffer -> SSPDR
                 .Ldr(1, TxBuffer).Str(1, 5, 0x0)
                 .Ldr(1, Spi0 + 8).Str(1, 5, 0x4)
                 .Movs(1, (byte)words).Str(1, 5, 0x8)
                 .Ldr(1, 1u | size | 1u << 4 | 0u << 11 | 16u << 15).Str(1, 5, 0xC)   // EN, INCR_READ, CHAIN_TO 0, TREQ 16
                 .Ldr(5, Dma + 0x40)
                 .Label("dmawait").Ldr(1, 5, 0xC).Lsrs(1, 1, 25).B(ThumbAsm.CS, "dmawait")   // BUSY = bit 24
                 .Adds(6, (byte)(words * (half ? 2 : 1)));
                break;
            }
            case SpiVariant.Irq:
                a.Movs(2, (byte)words)
                 .Label("fill").Str(3, 0, 8).Adds(3, 0x13).Subs(2, 1).B(ThumbAsm.NE, "fill")
                 .Adds(6, (byte)words)                                     // R6 = words expected so far
                 .Label("sleep").Wfi().Ldr(2, Counter).Ldr(1, 2, 0).CmpReg(1, 6).B(ThumbAsm.LT, "sleep");
                break;
        }
        a.Ldr(5, (uint)gap).Label("delay").Subs(5, 1).B(ThumbAsm.NE, "delay")
         .Adds(7, 1).Cmp(7, (byte)bursts).B(ThumbAsm.LT, "burst")
         .Label("done").B("done");
        if (variant == SpiVariant.Irq)
        {
            // R0-R3 are stacked by the exception entry: drain while RNE, count and store through IsrPtr.
            a.Label("isr")
             .Ldr(0, Spi0)
             .Label("isrloop").Ldr(1, 0, 0x0C).Lsrs(1, 1, 3).B(ThumbAsm.CC, "isrdone")   // RNE = bit 2
             .Ldr(1, 0, 8)
             .Ldr(2, IsrPtr).Ldr(3, 2, 0).Str(1, 3, 0).Adds(3, 4).Str(3, 2, 0)
             .Ldr(2, Counter).Ldr(3, 2, 0).Adds(3, 1).Str(3, 2, 0)
             .B("isrloop")
             .Label("isrdone").BxLr();
        }
        return a.Build();
    }

    /// <summary>
    /// UART0 on GP0 (TX) and GP1 (RX), FUNCSEL 2: IBRD/FBRD, UARTLCR_H (with FEN) and UARTCR = UARTEN | TXE | RXE. Bursts
    /// of <paramref name="bytes"/> bytes (0x5A, +0x25 each), then it reads as many from the RX FIFO, storing each
    /// UARTDR (data and error flags) as a word, then a delay loop, <paramref name="bursts"/> times.
    /// </summary>
    public static byte[] UartBursts(uint ibrd, uint fbrd, uint lcrH, int bytes = 4, int gap = 40, int bursts = 3)
    {
        var a = new ThumbAsm();
        a.Ldr(0, IoBank0 + 0 * 8 + 4).Movs(1, 2).Str(1, 0, 0)
         .Ldr(0, IoBank0 + 1 * 8 + 4).Str(1, 0, 0)
         .Ldr(0, Uart0)
         .Ldr(1, ibrd).Str(1, 0, 0x24)
         .Ldr(1, fbrd).Str(1, 0, 0x28)
         .Ldr(1, lcrH | 1u << 4).Str(1, 0, 0x2C)
         .Ldr(1, 0x301u).Str(1, 0, 0x30)
         .Ldr(6, RxBuffer).Movs(3, 0x5A).Movs(7, 0)
         .Label("burst").Movs(2, (byte)bytes)
         .Label("send").Ldr(1, 0, 0x18).Lsrs(1, 1, 6).B(ThumbAsm.CS, "send")   // TXFF = bit 5
         .Str(3, 0, 0).Adds(3, 0x25).Subs(2, 1).B(ThumbAsm.NE, "send")
         .Movs(2, (byte)bytes)
         .Label("recv").Ldr(1, 0, 0x18).Lsrs(1, 1, 5).B(ThumbAsm.CS, "recv")   // RXFE = bit 4
         .Ldr(1, 0, 0).Str(1, 6, 0).Adds(6, 4).Subs(2, 1).B(ThumbAsm.NE, "recv")
         .Ldr(5, (uint)gap).Label("delay").Subs(5, 1).B(ThumbAsm.NE, "delay")
         .Adds(7, 1).Cmp(7, (byte)bursts).B(ThumbAsm.LT, "burst")
         .Label("done").B("done");
        return a.Build();
    }

    /// <summary>
    /// I2C0 on GP4 (SDA) and GP5 (SCL), FUNCSEL 3, fast mode with the given HCNT / LCNT and SDA hold (IC_CON master,
    /// RESTART_EN, SLAVE_DISABLE, TX_EMPTY_CTRL). Each round writes two bytes (round, round ^ 0xFF) to register 0x10 of
    /// the slave at <paramref name="address"/> with STOP, waits for STOP_DET, then writes the pointer 0x10 and reads
    /// two bytes back (RESTART, STOP), storing them as words, waits for STOP_DET again. With
    /// <paramref name="absentAddress"/> nonzero it then addresses that one with a STOP write and stores
    /// IC_TX_ABRT_SOURCE once TX_ABRT is up. <paramref name="rounds"/> rounds.
    /// </summary>
    public static byte[] I2cRounds(int address, uint hcnt, uint lcnt, uint sdaHold, int absentAddress = 0, int rounds = 2, int gap = 40)
    {
        var a = new ThumbAsm();
        a.Ldr(0, IoBank0 + 4 * 8 + 4).Movs(1, 3).Str(1, 0, 0)
         .Ldr(0, IoBank0 + 5 * 8 + 4).Str(1, 0, 0)
         .Ldr(0, I2c0)
         .Movs(1, 0).Str(1, 0, 0x6C)                                      // IC_ENABLE = 0
         .Ldr(1, 0x165u).Str(1, 0, 0x00)                                  // IC_CON
         .Ldr(1, (uint)address).Str(1, 0, 0x04)                           // IC_TAR
         .Ldr(1, hcnt).Str(1, 0, 0x1C).Ldr(1, lcnt).Str(1, 0, 0x20)       // IC_FS_SCL_HCNT / LCNT
         .Ldr(1, sdaHold).Str(1, 0, 0x7C)                                 // IC_SDA_HOLD
         .Movs(1, 1).Str(1, 0, 0x6C)                                      // IC_ENABLE = 1
         .Ldr(6, RxBuffer).Movs(7, 0)
         .Label("round")
         .Movs(1, 0x10).Str(1, 0, 0x10)                                   // pointer
         .Str(7, 0, 0x10)                                                 // round
         .Movs(1, 0xFF).Eors(1, 7).Ldr(2, 1u << 9).Orrs(1, 2).Str(1, 0, 0x10)   // round ^ 0xFF, STOP
         .Bl("stopdet")
         .Movs(1, 0x10).Str(1, 0, 0x10)                                   // pointer
         .Ldr(1, 0x500u).Str(1, 0, 0x10)                                  // read, RESTART
         .Ldr(1, 0x300u).Str(1, 0, 0x10)                                  // read, STOP
         .Label("rxwait").Ldr(1, 0, 0x78).Cmp(1, 2).B(ThumbAsm.LT, "rxwait")
         .Ldr(1, 0, 0x10).Str(1, 6, 0).Ldr(1, 0, 0x10).Str(1, 6, 4).Adds(6, 8)
         .Bl("stopdet");
        if (absentAddress != 0)
        {
            a.Movs(1, 0).Str(1, 0, 0x6C)
             .Ldr(1, (uint)absentAddress).Str(1, 0, 0x04)
             .Movs(1, 1).Str(1, 0, 0x6C)
             .Ldr(1, 0x200u).Str(1, 0, 0x10)                              // write 0, STOP
             .Label("abrt").Ldr(1, 0, 0x34).Lsrs(1, 1, 7).B(ThumbAsm.CC, "abrt")   // TX_ABRT = bit 6
             .Ldr(2, I2c0 + 0x80).Ldr(1, 2, 0).Str(1, 6, 0).Adds(6, 4)   // IC_TX_ABRT_SOURCE
             .Ldr(1, 0, 0x54)                                             // IC_CLR_TX_ABRT
             .Bl("stopdet")
             .Movs(1, 0).Str(1, 0, 0x6C)
             .Ldr(1, (uint)address).Str(1, 0, 0x04)
             .Movs(1, 1).Str(1, 0, 0x6C);
        }
        a.Ldr(5, (uint)gap).Label("delay").Subs(5, 1).B(ThumbAsm.NE, "delay")
         .Adds(7, 1).Cmp(7, (byte)rounds).B(ThumbAsm.LT, "round")
         .Label("done").B("done")
         // Wait for STOP_DET (IC_RAW_INTR_STAT bit 9) and clear it (IC_CLR_STOP_DET). Uses R1 only.
         .Label("stopdet").Ldr(1, 0, 0x34).Lsrs(1, 1, 10).B(ThumbAsm.CC, "stopdet")
         .Ldr(1, 0, 0x60).BxLr();
        return a.Build();
    }
}

/// <summary>A small Thumb-1 assembler: labels (forward and backward), a literal pool after the code, run from flash.</summary>
internal sealed class ThumbAsm
{
    public const int EQ = 0, NE = 1, CS = 2, CC = 3, MI = 4, PL = 5, HI = 8, LS = 9, GE = 10, LT = 11, GT = 12, LE = 13;
    public const uint Base = 0x10000000u;

    private readonly List<ushort> _code = [];
    private readonly Dictionary<string, int> _labels = [];
    private readonly List<(int At, Func<int, ushort> Make, string Label)> _fixups = [];
    private readonly List<(int At, string Label)> _bls = [];
    private readonly List<(int At, int Rt, uint Value, string? Label)> _lits = [];

    private ThumbAsm Op(int op) { _code.Add((ushort)op); return this; }

    public ThumbAsm Label(string name) { _labels.Add(name, _code.Count); return this; }
    public ThumbAsm Movs(int rd, byte imm) => Op(0x2000 | rd << 8 | imm);
    public ThumbAsm Adds(int rd, byte imm) => Op(0x3000 | rd << 8 | imm);
    public ThumbAsm Subs(int rd, byte imm) => Op(0x3800 | rd << 8 | imm);
    public ThumbAsm Cmp(int rn, byte imm) => Op(0x2800 | rn << 8 | imm);
    public ThumbAsm Tst(int rn, int rm) => Op(0x4200 | rm << 3 | rn);
    public ThumbAsm CmpReg(int rn, int rm) => Op(0x4280 | rm << 3 | rn);
    public ThumbAsm Eors(int rdn, int rm) => Op(0x4040 | rm << 3 | rdn);
    public ThumbAsm Orrs(int rdn, int rm) => Op(0x4300 | rm << 3 | rdn);
    public ThumbAsm Lsrs(int rd, int rm, int imm) => Op(0x0800 | imm << 6 | rm << 3 | rd);
    public ThumbAsm Lsls(int rd, int rm, int imm) => Op(0x0000 | imm << 6 | rm << 3 | rd);
    public ThumbAsm Ldr(int rt, int rn, int imm) => Op(0x6800 | Imm5(imm, 4) << 6 | rn << 3 | rt);
    public ThumbAsm Str(int rt, int rn, int imm) => Op(0x6000 | Imm5(imm, 4) << 6 | rn << 3 | rt);

    private static int Imm5(int imm, int scale) =>
        imm % scale == 0 && imm / scale is >= 0 and < 32 ? imm / scale : throw new ArgumentOutOfRangeException(nameof(imm));
    public ThumbAsm Strb(int rt, int rn, int imm) => Op(0x7000 | imm << 6 | rn << 3 | rt);
    public ThumbAsm Strh(int rt, int rn, int imm) => Op(0x8000 | (imm >> 1) << 6 | rn << 3 | rt);
    /// <summary>MOV SP, Rm.</summary>
    public ThumbAsm MovSp(int rm) => Op(0x4600 | 1 << 7 | rm << 3 | 5);
    public ThumbAsm Wfi() => Op(0xBF30);
    public ThumbAsm Cpsie() => Op(0xB662);
    public ThumbAsm BxLr() => Op(0x4770);

    /// <summary>LDR Rt, =value (literal pool).</summary>
    public ThumbAsm Ldr(int rt, uint value) { _lits.Add((_code.Count, rt, value, null)); return Op(0); }

    /// <summary>LDR Rt, =label | 1 (a Thumb code address, for a vector).</summary>
    public ThumbAsm AdrLabel(int rt, string label) { _lits.Add((_code.Count, rt, 0, label)); return Op(0); }

    public ThumbAsm B(string label)
    {
        _fixups.Add((_code.Count, off => (ushort)(0xE000 | ((off >> 1) & 0x7FF)), label));
        return Op(0);
    }

    public ThumbAsm B(int cond, string label)
    {
        _fixups.Add((_code.Count, off => (ushort)(0xD000 | cond << 8 | ((off >> 1) & 0xFF)), label));
        return Op(0);
    }

    /// <summary>BL label (two halfwords).</summary>
    public ThumbAsm Bl(string label)
    {
        _bls.Add((_code.Count, label));
        Op(0); return Op(0);
    }

    public byte[] Build()
    {
        foreach (var (at, make, label) in _fixups)
            _code[at] = make(_labels[label] * 2 - (at * 2 + 4));
        foreach (var (at, label) in _bls)
        {
            // BL T1 for offsets within +-4 MiB with J1 = J2 = 1: S:imm10 then 0b11111:imm11.
            int off = _labels[label] * 2 - (at * 2 + 4);
            _code[at] = (ushort)(0xF000 | ((off >> 12) & 0x7FF));
            _code[at + 1] = (ushort)(0xF800 | ((off >> 1) & 0x7FF));
        }
        if (_code.Count % 2 != 0) _code.Add(0xBF00);   // nop: word-align the pool
        int pool = _code.Count * 2;
        var bytes = new byte[pool + _lits.Count * 4];
        for (int i = 0; i < _lits.Count; i++)
        {
            var (at, rt, value, label) = _lits[i];
            if (label is not null) value = Base + (uint)_labels[label] * 2 | 1;
            int lit = pool + i * 4;
            int pc = (at * 2 + 4) & ~3;
            if (lit - pc > 1020) throw new InvalidOperationException("literal out of range");
            _code[at] = (ushort)(0x4800 | rt << 8 | ((lit - pc) >> 2));
            BitConverter.TryWriteBytes(bytes.AsSpan(lit), value);
        }
        for (int i = 0; i < _code.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), _code[i]);
        return bytes;
    }
}

using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Ahb;
using RP2040.Peripherals.Apb;
using RP2040.Peripherals.Busctrl;
using RP2040.Peripherals.Clocks;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Gpio;
using RP2040.Peripherals.I2c;
using RP2040.Peripherals.IoQspi;
using RP2040.Peripherals.Pads;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Pll;
using RP2040.Peripherals.Ppb;
using RP2040.Peripherals.Psm;
using RP2040.Peripherals.Pwm;
using RP2040.Peripherals.Resets;
using RP2040.Peripherals.Rosc;
using RP2040.Peripherals.Rtc;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Spi;
using RP2040.Peripherals.Ssi;
using RP2040.Peripherals.SysCfg;
using RP2040.Peripherals.SysInfo;
using RP2040.Peripherals.Tbman;
using RP2040.Peripherals.Timer;
using RP2040.Peripherals.Uart;
using RP2040.Peripherals.Usb;
using RP2040.Peripherals.Vreg;
using RP2040.Peripherals.Watchdog;
using RP2040.Peripherals.Xosc;

namespace RP2040.Peripherals;

/// <summary>
/// Root class that wires all RP2040 peripherals together.
/// Typical usage:
/// <code>
/// var machine = new RP2040Machine();
/// machine.LoadFlash(bytes);
/// machine.Run(1_000_000);
/// </code>
/// </summary>
public enum RP2040BootromRevision
{
    /// <summary>Bootrom version 2 (RP2040 B0/B1 silicon). Default.</summary>
    B1,
    /// <summary>Bootrom version 3 (RP2040 B2 silicon). Verified by HIL against a real B2 Pico.</summary>
    B2,
}

public sealed class RP2040Machine : IDisposable
{
    public const uint CLK_HZ = 125_000_000;

    // ── Core ────────────────────────────────────────────────────────────
    public BusInterconnect Bus { get; }
    /// <summary>Core 0 (the primary CPU).</summary>
    public CortexM0Plus    Cpu { get; }
    /// <summary>Core 1 (launched by multicore handshake via SIO FIFO).</summary>
    public CortexM0Plus    Cpu1 { get; }

    // ── System peripherals ──────────────────────────────────────────────
    /// <summary>Private Peripheral Bus for Core 0.</summary>
    public PpbPeripheral      Ppb      { get; }
    /// <summary>Private Peripheral Bus for Core 1.</summary>
    public PpbPeripheral      Ppb1     { get; }
    public SioPeripheral      Sio      { get; }
    public SysInfoPeripheral  SysInfo  { get; }
    public SysCfgPeripheral   SysCfg   { get; }
    public PsmPeripheral      Psm      { get; }
    public ResetsPeripheral   Resets   { get; }
    public ClocksPeripheral   Clocks   { get; }
    public XoscPeripheral     Xosc     { get; }
    public WatchdogPeripheral Watchdog { get; }
    public BusctrlPeripheral  Busctrl  { get; }
    public TbmanPeripheral    Tbman    { get; }
    public PllPeripheral      PllSys   { get; }
    public PllPeripheral      PllUsb   { get; }
    public RoscPeripheral     Rosc     { get; }
    public VregPeripheral     Vreg     { get; }
    public SsiPeripheral      Ssi      { get; }
    public IoQspiPeripheral   IoQspi   { get; }

    // ── I/O peripherals ─────────────────────────────────────────────────
    public IoBank0Peripheral IoBank0   { get; }
    public PadsPeripheral    PadsBank0 { get; }
    public PadsPeripheral    PadsQspi  { get; }
    public TimerPeripheral   Timer     { get; }
    public UartPeripheral    Uart0     { get; }
    public UartPeripheral    Uart1     { get; }
    public SpiPeripheral     Spi0      { get; }
    public SpiPeripheral     Spi1      { get; }
    public I2cPeripheral     I2c0      { get; }
    public I2cPeripheral     I2c1      { get; }
    public AdcPeripheral     Adc       { get; }
    public PwmPeripheral     Pwm       { get; }
    public RtcPeripheral     Rtc       { get; }
    public DmaPeripheral     Dma       { get; }
    public PioPeripheral     Pio0      { get; }
    public PioPeripheral     Pio1      { get; }
    public UsbPeripheral     Usb       { get; }
    public IReadOnlyList<GpioPin> Gpio { get; }

    private readonly ITickable[] _tickables;
    private bool _core1Launched;
    private int  _activeCoreId;   // 0 = Core0, 1 = Core1 (set before each Run slice)

    private readonly RP2040BootromRevision _bootromRevision;

    public RP2040Machine(uint flashSize = 2 * 1024 * 1024,
                         RP2040BootromRevision bootrom = RP2040BootromRevision.B1)
    {
        _bootromRevision = bootrom;
        Bus = new BusInterconnect(flashSize);
        Cpu  = new CortexM0Plus(Bus) { CoreId = 0 };
        Cpu1 = new CortexM0Plus(Bus) { CoreId = 1 };

        // Every shared peripheral IRQ line reaches both interrupt controllers on silicon, and SEV
        // is an event for the whole cluster — pair the cores so neither is invisible to the other.
        Cpu.SiblingCore  = Cpu1;
        Cpu1.SiblingCore = Cpu;

        // ── PPB (0xE) ────────────────────────────────────────────────────
        Ppb  = new PpbPeripheral(Cpu);
        Ppb1 = new PpbPeripheral(Cpu1);
        // Route PPB accesses to the correct per-core PPB based on the active core.
        var ppbRouter = new PerCorePpbRouter(Ppb, Ppb1, () => _activeCoreId);
        Bus.MapDevice(0xE, ppbRouter);

        // ── SIO (0xD) ────────────────────────────────────────────────────
        Sio = new SioPeripheral(Cpu);
        Sio.GetActiveCoreId = () => _activeCoreId;
        Sio.SetCpu1(Cpu1);
        Sio.OnLaunchCore1 = LaunchCore1;
        Bus.MapDevice(0xD, Sio);

        // ── APB bridge (0x4) ─────────────────────────────────────────────
        var apb = new ApbBridge();
        Bus.MapDevice(4, apb);

        // System info / config (slots 0–1)
        SysInfo = new SysInfoPeripheral();
        apb.Register(0x40000000, SysInfo);

        SysCfg = new SysCfgPeripheral();
        apb.Register(0x40004000, SysCfg);

        // Clocks @ 0x40008000 (slot 2)
        Clocks = new ClocksPeripheral();
        apb.Register(0x40008000, Clocks);

        // RESETS @ 0x4000C000 (slot 3)
        Resets = new ResetsPeripheral();
        apb.Register(0x4000C000, Resets);

        // PSM @ 0x40010000 (slot 4)
        Psm = new PsmPeripheral();
        // Firmware releasing PROC1 from FRCE_OFF is multicore_reset_core1(): reset Core 1
        // and signal its bootrom is ready so Core 0's blocking FIFO pop returns.
        Psm.OnProc1Released = ResetCore1FromPsm;
        apb.Register(0x40010000, Psm);

        // IO_BANK0 @ 0x40014000 (slot 5)
        IoBank0 = new IoBank0Peripheral(Sio, Cpu);
        apb.Register(0x40014000, IoBank0);

        // PADS_BANK0 @ 0x4001C000 (slot 7), PADS_QSPI @ 0x40020000 (slot 8)
        PadsBank0 = new PadsPeripheral();
        apb.Register(0x4001C000, PadsBank0);
        Sio.PadsBank0 = PadsBank0;   // floating inputs settle on their pull resistor

        PadsQspi = new PadsPeripheral();
        apb.Register(0x40020000, PadsQspi);

        // XOSC @ 0x40024000 (slot 9)
        Xosc = new XoscPeripheral();
        apb.Register(0x40024000, Xosc);

        // PLL_SYS @ 0x40028000 (slot 10), PLL_USB @ 0x4002C000 (slot 11)
        PllSys = new PllPeripheral();
        apb.Register(0x40028000, PllSys);

        PllUsb = new PllPeripheral();
        apb.Register(0x4002C000, PllUsb);

        // IO_QSPI @ 0x40018000 (slot 6)
        IoQspi = new IoQspiPeripheral();
        apb.Register(0x40018000, IoQspi);

        // BUSCTRL @ 0x40030000 (slot 12)
        Busctrl = new BusctrlPeripheral();
        apb.Register(0x40030000, Busctrl);

        // UART0 @ 0x40034000 (slot 13), UART1 @ 0x40038000 (slot 14)
        Uart0 = new UartPeripheral(Cpu, irq: 20);
        Uart1 = new UartPeripheral(Cpu, irq: 21);
        apb.Register(0x40034000, Uart0);
        apb.Register(0x40038000, Uart1);

        // SPI0 @ 0x4003C000 (slot 15), SPI1 @ 0x40040000 (slot 16)
        Spi0 = new SpiPeripheral(Cpu, irq: 18);
        Spi1 = new SpiPeripheral(Cpu, irq: 19);
        apb.Register(0x4003C000, Spi0);
        apb.Register(0x40040000, Spi1);

        // I2C0 @ 0x40044000 (slot 17), I2C1 @ 0x40048000 (slot 18)
        I2c0 = new I2cPeripheral(Cpu, irq: 23);
        I2c1 = new I2cPeripheral(Cpu, irq: 24);
        apb.Register(0x40044000, I2c0);
        apb.Register(0x40048000, I2c1);

        // ADC @ 0x4004C000 (slot 19)
        Adc = new AdcPeripheral(Cpu);
        apb.Register(0x4004C000, Adc);

        // PWM @ 0x40050000 (slot 20)
        Pwm = new PwmPeripheral(Cpu);
        apb.Register(0x40050000, Pwm);
        IoBank0.Pwm = Pwm;   // let a pad muxed to PWM report the channel level it is driving

        // B-pin inputs for the PWM gated/edge-count DIVMODEs: odd GPIOs muxed to PWM (FUNCSEL 4)
        // feed slice (pin >> 1) & 7 — what CircuitPython's countio counts.
        IoBank0.OnInputChanged = (pin, level) =>
        {
            if ((pin & 1) != 0 && IoBank0.GetFuncSel(pin) == 4)
                Pwm.SetBInput((pin >> 1) & 7, level);
        };

        // Timer @ 0x40054000 (slot 21)
        Timer = new TimerPeripheral(Cpu, CLK_HZ);
        apb.Register(0x40054000, Timer);

        // Watchdog @ 0x40058000 (slot 22)
        Watchdog = new WatchdogPeripheral();
        apb.Register(0x40058000, Watchdog);

        // RTC @ 0x4005C000 (slot 23)
        Rtc = new RtcPeripheral(Cpu);
        apb.Register(0x4005C000, Rtc);

        // TBMAN @ 0x4006C000 (slot 27)
        Tbman = new TbmanPeripheral();
        apb.Register(0x4006C000, Tbman);

        // ROSC @ 0x40060000 (slot 24), VREG @ 0x40064000 (slot 25)
        Rosc = new RoscPeripheral();
        apb.Register(0x40060000, Rosc);

        Vreg = new VregPeripheral();
        apb.Register(0x40064000, Vreg);

        // SSI at 0x18000000 is within XIP Flash region — registered as sub-device so
        // all accesses to [0x18000000, 0x18FFFFFF] route to SSI registers while
        // [0x10000000, 0x17FFFFFF] continues to use the flash pointer fast path.
        Ssi = new SsiPeripheral();
        Bus.RegisterSsi(Ssi);

        // Wire the SSI flash command engine to the flash memory and to the IO_QSPI
        // SS pin so CS assert/deassert signals from flash_cs_force() reach the SSI.
        unsafe { Ssi.AttachFlash(Bus.PtrFlash, Bus.FlashSize); }
        IoQspi.AttachSsi(Ssi);

        // ── AHB bridge (0x5): DMA + PIO ──────────────────────────────────
        var ahb = new AhbBridge();
        Bus.MapDevice(5, ahb);

        // DMA @ 0x50000000 (slot 0)
        Dma = new DmaPeripheral(Bus, Cpu);
        ahb.Register(0x50000000, Dma);

        // USB @ 0x50100000 (slot 1, covers DPRAM + REGS at 0x50110000)
        Usb = new UsbPeripheral(Cpu);
        ahb.Register(0x50100000, Usb);

        // Wire PPB's OnInterruptEnable to USB.RecheckInterrupts so that when
        // pico-sdk's irq_set_enabled does ICPR then ISER, the USB level-triggered
        // IRQ is re-asserted correctly (see: NVIC_ICPR clears pending bit, but
        // hardware IRQ line stays asserted — we simulate this via RecheckInterrupts).
        // Core 1's NVIC gets the same treatment: a driver that enables USBCTRL_IRQ there must
        // see the still-asserted line re-latched, exactly as Core 0 does.
        Ppb.OnInterruptEnable  += Usb.RecheckInterrupts;
        Ppb1.OnInterruptEnable += Usb.RecheckInterrupts;

        // When firmware resets the USBCTRL block (rp2040_usb_init → reset_block/unreset_block),
        // reset the USB peripheral emulator state so the next CONTROLLER_EN write re-triggers
        // enumeration (OnUsbEnabled). Bit 24 = USBCTRL in RESETS.RESET.
        const uint USBCTRL_BIT = 1u << 24;
        Resets.OnUnreset += released => { if ((released & USBCTRL_BIT) != 0) Usb.Reset(); };

        // PIO0 @ 0x50200000 (slot 2), PIO1 @ 0x50300000 (slot 3)
        Pio0 = new PioPeripheral(Cpu, 0);
        Pio1 = new PioPeripheral(Cpu, 1);
        ahb.Register(0x50200000, Pio0);
        ahb.Register(0x50300000, Pio1);

        // PIO blocks power up held in reset (RESETS bits 10/11). Start them gated and release on the
        // RESETS transition, so firmware that drives PIO registers without first clearing the reset bit
        // gets silent no-ops exactly like silicon (this is what masked the real Pico bug).
        Pio0.InReset = Pio1.InReset = true;
        Resets.OnUnreset += released =>
        {
            if ((released & (1u << 10)) != 0) Pio0.InReset = false;
            if ((released & (1u << 11)) != 0) Pio1.InReset = false;
        };
        Resets.OnReset += asserted =>
        {
            if ((asserted & (1u << 10)) != 0) Pio0.InReset = true;
            if ((asserted & (1u << 11)) != 0) Pio1.InReset = true;
        };

        // ── GPIO pins ─────────────────────────────────────────────────────
        var pins = new GpioPin[30];
        for (var i = 0; i < 30; i++)
            pins[i] = new GpioPin(i, Sio, IoBank0);
        Gpio = pins;

        // ── Tickable list ─────────────────────────────────────────────────
        // ppbRouter.Tick() internally ticks both Ppb (Core0) and Ppb1 (Core1),
        // so Ppb1 does not need a separate entry here.
        _tickables = [ppbRouter, Timer, Pwm, Pio0, Pio1, Rtc, Watchdog, Usb];

        // ── DMA DREQ sources ──────────────────────────────────────────────
        // PIO0 TX/RX SM0-3: DREQ 0-3 (TX), 4-7 (RX)
        // PIO1 TX/RX SM0-3: DREQ 8-11 (TX), 12-15 (RX)
        for (var i = 0; i < 4; i++)
        {
            var sm = i;
            Dma.RegisterDreq( 0 + sm, () => Pio0.TxFifoNotFull(sm));
            Dma.RegisterDreq( 4 + sm, () => !Pio0.RxFifoEmpty(sm));
            Dma.RegisterDreq( 8 + sm, () => Pio1.TxFifoNotFull(sm));
            Dma.RegisterDreq(12 + sm, () => !Pio1.RxFifoEmpty(sm));
        }
        // Re-arm a DREQ-paced DMA the instant the SM frees a TX slot or produces an RX word — otherwise
        // a paced transfer (e.g. a CYW43 firmware download over the PIO gSPI) stalls BUSY forever.
        Pio0.OnTxConsumed += sm => Dma.ResumeDreq(0 + sm);
        Pio0.OnRxPush     += (sm, _) => Dma.ResumeDreq(4 + sm);
        Pio1.OnTxConsumed += sm => Dma.ResumeDreq(8 + sm);
        Pio1.OnRxPush     += (sm, _) => Dma.ResumeDreq(12 + sm);
        // Gate the PIO's per-tick NVIC recompute to the window where core 0 sleeps (a busy core
        // re-checks interrupts itself), removing the churn that dominated fine-grained stepping.
        Pio0.CoreWaiting = Pio1.CoreWaiting = () => Core0Waiting;
        // SPI0 TX(16), RX(17), SPI1 TX(18), RX(19)
        Dma.RegisterDreq(16, () => true);              // SPI0 TX always ready
        Dma.RegisterDreq(17, () => Spi0.RxDataAvailable);
        Dma.RegisterDreq(18, () => true);              // SPI1 TX always ready
        Dma.RegisterDreq(19, () => Spi1.RxDataAvailable);
        // UART0 TX(20), RX(21), UART1 TX(22), RX(23)
        Dma.RegisterDreq(20, () => true);              // UART0 TX always ready
        Dma.RegisterDreq(21, () => Uart0.RxDataAvailable);
        Dma.RegisterDreq(22, () => true);              // UART1 TX always ready
        Dma.RegisterDreq(23, () => Uart1.RxDataAvailable);
        // ADC DREQ 36: RX FIFO has data
        Dma.RegisterDreq(36, () => Adc.HasFifoData);

        // ── PIO GPIO integration ───────────────────────────────────────────
        // Shared helpers: read physical GPIO levels; update SIO output and notify IoBank0
        // The PIO samples the PAD, not a guess: `in pins`, `wait gpio/pin` and `jmp pin` read what the
        // pin is actually at, which may be driven by another SM, by a peripheral, or by an off-chip
        // device (SetExternalInput). OR-ing SIO's input and output words instead reported a 1 whenever
        // either said so, so a pad driven low by one source read high because the other still held it.
        uint ReadGpio() => IoBank0.GetInputWord();

        void ApplyPins(int block, uint value, uint mask)
        {
            // PIO output: update SIO GpioIn so physical level is visible to CPU reads
            Sio.DriveGpioIn(value, mask);
            // Route to the GPIO function mux so a pad muxed to PIO reflects the SM output
            // (the level a circuit host reads via IoBank0.GetPadOutputLevel).
            IoBank0.SetPioOut(block, value, mask);
            // Notify IoBank0 for edge/level interrupt detection on each changed pin
            for (var pin = 0; pin < 30; pin++)
                if ((mask & (1u << pin)) != 0)
                    IoBank0.UpdatePinInput(pin, (value & (1u << pin)) != 0);
        }

        Pio0.ReadGpioIn    = ReadGpio;
        Pio0.WriteGpioPins = (value, mask) => ApplyPins(0, value, mask);
        Pio0.WriteGpioDirs = (value, mask) => IoBank0.SetPioDirs(0, value, mask);

        Pio1.ReadGpioIn    = ReadGpio;
        Pio1.WriteGpioPins = (value, mask) => ApplyPins(1, value, mask);
        Pio1.WriteGpioDirs = (value, mask) => IoBank0.SetPioDirs(1, value, mask);
    }

    /// <summary>
    /// Writes bytes directly into the existing Flash backing store at <paramref name="offset"/>
    /// (relative to 0x10000000), without re-loading the whole image. Lets a host edit a filesystem
    /// region in place — e.g. stage a new code.py — then call <see cref="Reset"/> to re-run, instead
    /// of allocating and re-loading a fresh multi-MB flash image on every change.
    /// </summary>
    public unsafe void WriteFlash(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > Bus.FlashSize)
            throw new ArgumentOutOfRangeException(nameof(offset), "Write would fall outside the flash region");
        data.CopyTo(new Span<byte>(Bus.PtrFlash + offset, data.Length));
    }

    /// <summary>Load a binary image into Flash starting at 0x10000000.</summary>
    public unsafe void LoadFlash(ReadOnlySpan<byte> image)
    {
        if (image.Length > Bus.FlashSize)
            throw new ArgumentException($"Flash image exceeds configured flash size ({Bus.FlashSize / 1024} KB)");

        image.CopyTo(new Span<byte>(Bus.PtrFlash, image.Length));

        // If no BootROM has been loaded, install the real RP2040 B1 BootROM binary.
        // The real bootrom implements rom_table_lookup, memcpy44, memset4 and all
        // bit-manipulation helpers correctly in native Thumb code.
        // Flash-hardware-accessing functions (connect_internal_flash, flash_exit_xip,
        // flash_flush_cache, flash_enter_cmd_xip) are patched to BX LR so they return
        // immediately without touching SSI registers.
        // flash_range_erase and flash_range_program are intercepted by C# native hooks.
        if (*(uint*)Bus.PtrBootRom == 0 && *(uint*)(Bus.PtrBootRom + 4) == 0)
        {
            LoadRealBootRom(Bus.PtrBootRom, _bootromRevision);

            // The ROM images ship without their float library (see BootromFloat and
            // NOTICE.txt): install native implementations behind the 'SF'/'SD' tables so
            // firmware that resolves them through rom_data_lookup keeps working.
            // Both cores, not just Core 0: the stripped window is filled with BKPT, so a Core 1
            // thread doing float work (MicroPython's _thread + time.sleep(float)) would execute
            // it and HardFault straight into lockup.
            BootromFloat.Install(Cpu, Bus.PtrBootRom);
            BootromFloat.Install(Cpu1, Bus.PtrBootRom);

            if (TryFindVectorTable(Bus.PtrFlash, (int)image.Length, out var sp, out var resetPc,
                    out var vectorTableOffset))
            {
                // Real BootROM sets VTOR to point at the firmware's own vector table
                // before branching to the Reset handler.  pico-sdk code checks VTOR
                // during spinlock initialisation, so this must be done before Reset().
                Cpu.Registers.VTOR = 0x10000000u + (uint)vectorTableOffset;
            }

            // Register C# hooks only for flash erase/program at their real bootrom
            // addresses so MicroPython's LittleFS formatter can modify emulated flash.
            Cpu.RegisterNativeHook(0x237C, FlashEraseHook);
            Cpu.RegisterNativeHook(0x23C4, FlashProgramHook);
            Cpu1.RegisterNativeHook(0x237C, FlashEraseHook);
            Cpu1.RegisterNativeHook(0x23C4, FlashProgramHook);

            // Where Core 1 lands when its entry function returns (see LaunchCore1).
            _core1WaitForVector = RomFuncLookup(Bus.PtrBootRom, 'W', 'V');
            if (_core1WaitForVector != 0)
                Cpu1.RegisterNativeHook(_core1WaitForVector & ~1u, Core1ReturnedToBootrom);
        }

        Cpu.Reset();

        // rp2040js-compatible boot: bypass the bootrom reset handler (which tries to
        // configure SSI/QSPI hardware that is not fully emulated) and start execution
        // directly at the flash start address 0x10000000, where boot2 lives.
        // The bootrom is still resident and handles ROM API calls (rom_table_lookup, etc.)
        // The firmware's own SP comes from the vector table entry we found above.
        if (TryFindVectorTable(Bus.PtrFlash, (int)image.Length, out var firmwareSp, out _,
                out _))
        {
            Cpu.Registers.SP = firmwareSp;
        }
        Cpu.Registers.PC = BusInterconnect.FLASH_START_ADDRESS;
    }

    // UF2 format constants (https://github.com/microsoft/uf2)
    private const uint Uf2MagicStart0 = 0x0A324655u; // "UF2\n"
    private const uint Uf2MagicStart1 = 0x9E5D5157u;
    private const uint Uf2MagicEnd    = 0x0AB16F30u;
    private const int  Uf2BlockSize   = 512;
    private const uint FlashBase      = BusInterconnect.FLASH_START_ADDRESS;

    /// <summary>
    /// Parses a UF2 firmware file and loads its payload into Flash via <see cref="LoadFlash"/>.
    /// Only blocks targeting the RP2040 flash region (≥ 0x10000000) are copied; blocks with
    /// the "not main flash" flag (bit 0) are skipped.
    /// </summary>
    /// <param name="uf2">Raw UF2 file bytes.</param>
    /// <exception cref="ArgumentException">Data is not a valid UF2 size.</exception>
    /// <exception cref="InvalidDataException">No valid data blocks or target address below flash base.</exception>
    public void LoadUf2(ReadOnlySpan<byte> uf2) => LoadFlash(Uf2ToFlash(uf2));

    /// <summary>
    /// Parses a UF2 file into a flat binary flash image starting at offset 0 (relative to 0x10000000).
    /// Erased bytes (not covered by any UF2 block) are set to 0xFF.
    /// Returns <c>null</c> if the data is not a valid UF2 file (wrong magic or invalid block structure).
    /// </summary>
    /// <param name="uf2">Raw UF2 file bytes.</param>
    public static byte[]? Uf2ToFlash(ReadOnlySpan<byte> uf2)
    {
        if (uf2.IsEmpty || uf2.Length < Uf2BlockSize || uf2.Length % Uf2BlockSize != 0)
            return null;

        int blockCount = uf2.Length / Uf2BlockSize;
        uint flashMin = uint.MaxValue;
        uint flashMax = 0;

        // First pass: validate blocks and find address range.
        for (int b = 0; b < blockCount; b++)
        {
            int off = b * Uf2BlockSize;
            uint magic0 = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[off..]);
            uint magic1 = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 4)..]);
            uint magicE  = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 508)..]);
            if (magic0 != Uf2MagicStart0 || magic1 != Uf2MagicStart1 || magicE != Uf2MagicEnd)
                return null;

            uint flags       = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 8)..]);
            if ((flags & 0x00000001u) != 0) continue; // not main flash — skip

            uint targetAddr  = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 12)..]);
            uint payloadSize = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 16)..]);
            if (payloadSize == 0 || payloadSize > 476)
                return null;

            if (targetAddr < flashMin) flashMin = targetAddr;
            uint end = targetAddr + payloadSize;
            if (end > flashMax) flashMax = end;
        }

        if (flashMin == uint.MaxValue || flashMax <= flashMin)
            return null;
        if (flashMin < FlashBase)
            return null;

        // Allocate a flash image from FlashBase, initialized to 0xFF (erased flash).
        var image = new byte[flashMax - FlashBase];
        image.AsSpan().Fill(0xFF);

        // Second pass: copy payloads.
        for (int b = 0; b < blockCount; b++)
        {
            int off = b * Uf2BlockSize;
            uint flags       = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 8)..]);
            if ((flags & 0x00000001u) != 0) continue;

            uint targetAddr  = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 12)..]);
            uint payloadSize = System.Runtime.InteropServices.MemoryMarshal.Read<uint>(uf2[(off + 16)..]);
            uf2.Slice(off + 32, (int)payloadSize).CopyTo(image.AsSpan((int)(targetAddr - FlashBase)));
        }

        return image;
    }

    /// <summary>
    /// Scans the flash image for an ARM Cortex-M vector table by looking for a word
    /// whose upper byte places it in SRAM (0x20xxxxxx) followed by a Thumb-mode pointer
    /// into Flash (0x1xxxxxxx with LSB set).
    /// </summary>
    private static unsafe bool TryFindVectorTable(byte* flash, int size,
        out uint sp, out uint resetPc, out int vectorTableOffset)
    {
        // RP2040 SDK firmware: main vector table at offset 0x100 (after 256-byte boot2).
        // Bare Cortex-M firmware (no boot2): vector table at offset 0.
        // Also try 0x200 for exotic layouts.
        ReadOnlySpan<int> offsets = [0x100, 0, 0x200];

        foreach (var off in offsets)
        {
            if (off + 8 > size) continue;

            var candidateSp = *(uint*)(flash + off);
            var candidatePc = *(uint*)(flash + off + 4);

            // SP must be within RP2040 SRAM (0x20000000 – 0x2007FFFF), 4-byte aligned.
            if ((candidateSp >> 19) != (0x20000000u >> 19)) continue;
            if ((candidateSp & 3) != 0) continue;

            // Reset PC must be a Thumb pointer (LSB = 1) into Flash (0x10xxxxxx).
            if ((candidatePc & 1) == 0) continue;
            if ((candidatePc >> 24) != 0x10) continue;

            sp = candidateSp;
            resetPc = candidatePc;
            vectorTableOffset = off;
            return true;
        }

        sp = 0;
        resetPc = 0;
        vectorTableOffset = 0;
        return false;
    }

    // ── Native hook: ROM function lookup ─────────────────────────────────────

    /// <summary>
    /// Function codes for the ROM function lookup table, indexed for fast access.
    /// Key = 16-bit ROM code, Value = BootROM address (even, Thumb bit NOT included).
    /// </summary>
    private static readonly Dictionary<uint, uint> RomFuncTable = new()
    {
        [0x434D] = 0x0100,  // 'MC' = memcpy44
        [0x534D] = 0x0120,  // 'MS' = memset4
        [0x3443] = 0x0100,  // 'C4' = memcpy4 (alias)
        [0x3453] = 0x0120,  // 'S4' = memset4 (alias)
        [0x3350] = 0x01C0,  // 'P3' = popcount32       (native hook at 0x01C0)
        [0x3352] = 0x01D0,  // 'R3' = reverse32        (native hook at 0x01D0)
        [0x334C] = 0x01E0,  // 'L3' = clz32            (native hook at 0x01E0)
        [0x3354] = 0x01F0,  // 'T3' = ctz32            (native hook at 0x01F0)
        [0x4649] = 0x0180,  // 'IF' = connect_internal_flash (no-op)
        [0x5845] = 0x0180,  // 'EX' = flash_exit_xip (no-op)
        [0x4552] = 0x0190,  // 'RE' = flash_range_erase (native hook)
        [0x5052] = 0x01A0,  // 'RP' = flash_range_program (native hook)
        [0x4346] = 0x0180,  // 'FC' = flash_flush_cache (no-op)
        [0x5843] = 0x0180,  // 'CX' = flash_enter_cmd_xip (no-op)
        // Soft-float data table: 'SF' returns pointer to an empty table (terminator only at 0x0250)
        [0x4653] = 0x0250,  // 'SF' = soft_float_table stub
    };

    private static void RomTableLookupHook(Core.Cpu.CortexM0Plus cpu)
    {
        // r0 = table ptr (uint16_t*), r1 = code  →  r0 = func addr with Thumb bit, or 0
        var code = cpu.Registers.R1 & 0xFFFF;
        if (RomFuncTable.TryGetValue(code, out var addr))
        {
            cpu.Registers.R0 = addr | 1u;
        }
        else
        {
            System.Console.Error.WriteLine($"Unknown ROM function code=0x{code:X4} ('{(char)(code & 0xFF)}{(char)((code >> 8) & 0xFF)}') at LR=0x{cpu.Registers.LR:X8}");
            cpu.Registers.R0 = 0x0181u;  // BX LR (safe no-op instead of NULL)
        }
    }

    /// <summary>
    /// Native hook for <c>flash_range_erase(uint32_t flash_offs, size_t count, ...)</c>.
    /// Fills the specified flash region with 0xFF (erased state).
    /// Called by the CPU when PC = 0x0190 (registered in <see cref="LoadFlash"/>).
    /// </summary>
    private unsafe void FlashEraseHook(Core.Cpu.CortexM0Plus cpu)
    {
        var offset = (int)(cpu.Registers.R0 & (Bus.FlashSize - 1));
        var count  = (int)cpu.Registers.R1;
        if (count < 0 || offset + count > (int)Bus.FlashSize) count = (int)Bus.FlashSize - offset;
        if (count > 0)
            new Span<byte>(Bus.PtrFlash + offset, count).Fill(0xFF);
    }

    /// <summary>
    /// Native hook for <c>flash_range_program(uint32_t flash_offs, const uint8_t* data, size_t count)</c>.
    /// Copies bytes from SRAM (or anywhere in the address space) into the emulated flash.
    /// Called by the CPU when PC = 0x01A0 (registered in <see cref="LoadFlash"/>).
    /// </summary>
    private unsafe void FlashProgramHook(Core.Cpu.CortexM0Plus cpu)
    {
        var flashOffset = (int)(cpu.Registers.R0 & (Bus.FlashSize - 1));
        var srcAddr     = cpu.Registers.R1;
        var count       = (int)cpu.Registers.R2;
        if (count < 0 || flashOffset + count > (int)Bus.FlashSize)
            count = (int)Bus.FlashSize - flashOffset;
        for (var i = 0; i < count; i++)
            Bus.PtrFlash[flashOffset + i] = Bus.ReadByte(srcAddr + (uint)i);
    }

    /// <summary>
    /// Native hook for bootrom memcpy44: copies n bytes (arbitrary count) from src to dst.
    /// Signature: void* memcpy44(void* dst, const void* src, size_t n) → R0=dst
    /// </summary>
    private unsafe void Memcpy44Hook(Core.Cpu.CortexM0Plus cpu)
    {
        var dst = cpu.Registers.R0;
        var src = cpu.Registers.R1;
        var n   = (int)cpu.Registers.R2;
        for (var i = 0; i < n; i++)
            Bus.WriteByte(dst + (uint)i, Bus.ReadByte(src + (uint)i));
        // R0 = original dst (already set, unchanged)
    }

    /// <summary>
    /// Native hook for bootrom memset4: fills n bytes with value c.
    /// Signature: void* memset4(void* dst, uint8_t c, size_t n) → R0=dst
    /// The real RP2040 bootrom 'MS' function handles arbitrary n.
    /// </summary>
    private unsafe void Memset4Hook(Core.Cpu.CortexM0Plus cpu)
    {
        var dst = cpu.Registers.R0;
        var val = (byte)(cpu.Registers.R1 & 0xFF);
        var n   = (int)cpu.Registers.R2;
        for (var i = 0; i < n; i++)
            Bus.WriteByte(dst + (uint)i, val);
        // R0 = original dst (already set, unchanged)
    }

    private static void Popcount32Hook(Core.Cpu.CortexM0Plus cpu)
        => cpu.Registers.R0 = (uint)System.Numerics.BitOperations.PopCount(cpu.Registers.R0);

    private static void Reverse32Hook(Core.Cpu.CortexM0Plus cpu)
    {
        var v = cpu.Registers.R0;
        v = ((v & 0xFFFF0000u) >> 16) | ((v & 0x0000FFFFu) << 16);
        v = ((v & 0xFF00FF00u) >>  8) | ((v & 0x00FF00FFu) <<  8);
        v = ((v & 0xF0F0F0F0u) >>  4) | ((v & 0x0F0F0F0Fu) <<  4);
        v = ((v & 0xCCCCCCCCu) >>  2) | ((v & 0x33333333u) <<  2);
        v = ((v & 0xAAAAAAAAu) >>  1) | ((v & 0x55555555u) <<  1);
        cpu.Registers.R0 = v;
    }

    private static void Clz32Hook(Core.Cpu.CortexM0Plus cpu)
        => cpu.Registers.R0 = (uint)System.Numerics.BitOperations.LeadingZeroCount(cpu.Registers.R0);

    private static void Ctz32Hook(Core.Cpu.CortexM0Plus cpu)
        => cpu.Registers.R0 = (uint)System.Numerics.BitOperations.TrailingZeroCount(cpu.Registers.R0);

    /// <summary>
    /// Loads the real RP2040 B1 bootrom binary (embedded as a resource) into bootrom
    /// memory, then patches flash hardware-accessing functions to BX LR so they return
    /// without touching SSI/QSPI registers that are not fully emulated.
    /// </summary>
    private static unsafe void LoadRealBootRom(byte* rom, RP2040BootromRevision revision)
    {
        // Load the BootROM for the requested silicon revision. B1 (version 2) is the default; B2
        // (version 3) is the bootrom on later RP2040 silicon (verified by HIL against a real B2 chip).
        var resource = revision switch
        {
            RP2040BootromRevision.B2 => "RP2040Sharp.bootrom_b2.bin",
            _                        => "RP2040Sharp.bootrom_b1.bin",
        };
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resource}' not found. Ensure the matching bootrom .bin is included " +
                "as an EmbeddedResource in the project.");
        stream.ReadExactly(new Span<byte>(rom, 16384));

        // Patch the flash hardware-accessing bootrom functions to 'BX LR' (0x4770): they talk directly to
        // the SSI/QSPI peripheral (not fully emulated) and are called by MicroPython's LittleFS flash
        // trampoline to set up/tear down XIP around erase/program. Making them no-ops is safe — our C#
        // hooks handle the actual flash data. The function ADDRESSES differ between bootrom revisions, so
        // resolve them from the ROM function table instead of hardcoding (revision-agnostic; reproduces
        // the historical B1 addresses 0x24A0/0x23F4/0x2360/0x2330 exactly).
        static void PatchBxLr(byte* p, int addr) { p[addr] = 0x70; p[addr + 1] = 0x47; }
        static ushort U16(byte* p, int o) => (ushort)(p[o] | (p[o + 1] << 8));

        // rom_func_table pointer lives at offset 0x14; the table is a list of (code:u16, addr:u16) pairs
        // terminated by code 0. Codes are two ASCII chars: 'IF','EX','FC','CX' (see RP2040 datasheet §2.8.3).
        foreach (var code in new ushort[] { 0x4649 /*IF*/, 0x5845 /*EX*/, 0x4346 /*FC*/, 0x5843 /*CX*/ })
        {
            for (int p = U16(rom, 0x14); p < 0x4000 && U16(rom, p) != 0; p += 4)
            {
                if (U16(rom, p) != code) continue;
                PatchBxLr(rom, U16(rom, p + 2) & ~1); // table stores the Thumb (odd) address; patch the instruction
                break;
            }
        }
    }

    /// 
    /// The stub implements the ROM API (rom_table_lookup, memcpy44, memset4) using
    /// hand-assembled ARM Thumb opcodes.  Entry [0] (initial SP) and entry [1]
    /// (reset PC) are left at zero and must be patched by the caller after
    /// locating the firmware's own vector table.
    /// </summary>
    private static unsafe void WriteBootRomStub(byte* rom)
    {
        // ── helpers ─────────────────────────────────────────────────────────
        static void W16(byte* p, int off, ushort v)
        {
            p[off]     = (byte)(v & 0xFF);
            p[off + 1] = (byte)(v >> 8);
        }
        static void W32(byte* p, int off, uint v)
        {
            p[off]     = (byte)( v        & 0xFF);
            p[off + 1] = (byte)((v >>  8) & 0xFF);
            p[off + 2] = (byte)((v >> 16) & 0xFF);
            p[off + 3] = (byte)( v >> 24);
        }

        // ── Exception vector table (0x0000 – 0x003F + IRQs) ─────────────────
        //   Entry [0] = Initial SP    ← patched later by LoadFlash
        //   Entry [1] = Reset PC      ← patched later by LoadFlash
        //   All others → default_handler (BX LR at 0x0180) with Thumb bit
        const uint defaultHandler = 0x0181u;
        W32(rom, 0x0000, 0x20041000);       // BootROM initial SP (overwritten later)
        for (int i = 1; i < 16; i++)
            W32(rom, i * 4, defaultHandler);
        for (int i = 0; i < 26; i++)        // RP2040 has 26 external IRQs
            W32(rom, 0x0040 + i * 4, defaultHandler);

        // ── ROM API infrastructure (in reserved Cortex-M0+ vector slots) ─────
        //   0x0010 – ROM code magic, 0x0012 – version, 0x0014 – func_table_ptr,
        //   0x0016 – data_table_ptr, 0x0018 – rom_table_lookup fn ptr
        W16(rom, 0x0010, 0x0210);   // ROM code magic (matches real RP2040 BootROM)
        W16(rom, 0x0012, 0x02);     // ROM version 2
        W16(rom, 0x0014, 0x0200);   // function table at 0x0200
        W16(rom, 0x0016, 0x0250);   // data table at 0x0250 (just a terminator)
        W16(rom, 0x0018, 0x0061);   // rom_table_lookup at 0x0060 (Thumb bit = 0x0061)

        // ── default_handler at 0x0180: BX LR ─────────────────────────────────
        W16(rom, 0x0180, 0x4770);   // BX LR

        // ── rom_table_lookup at 0x0060 ────────────────────────────────────────
        //   r0 = table (uint16_t*), r1 = code → r0 = func addr (with Thumb bit) or 0
        //   Branch offsets: ARMv6-M PC = instruction_address + 4 when computing branch target.
        //   loop(0x60): ldrh r2,[r0]; cbz r2,not_found(0x74); uxth r3,r1; cmp r2,r3
        //               beq found(0x6E); adds r0,#4; b loop(0x60)
        //   found(0x6E): ldrh r0,[r0,#2]; bx lr
        //   not_found(0x74): movs r0,#0; bx lr
        ReadOnlySpan<ushort> lookup =
        [
            0x8802,  // 0x0060  LDRH r2, [r0, #0]             ; loop:
            0xB13A,  // 0x0062  CBZ  r2, not_found  ; PC=0x0066, +14 → 0x0074
            0xB28B,  // 0x0064  UXTH r3, r1
            0x429A,  // 0x0066  CMP  r2, r3
            0xD001,  // 0x0068  BEQ  found          ; PC=0x006C, +1×2=2 → 0x006E
            0x3004,  // 0x006A  ADDS r0, r0, #4
            0xE7F8,  // 0x006C  B    loop            ; PC=0x0070, -8×2=-16 → 0x0060
            0x8840,  // 0x006E  LDRH r0, [r0, #2]   ; found:
            0x4770,  // 0x0070  BX   LR
            0x2000,  // 0x0072  MOVS r0, #0          ; not_found:
            0x4770,  // 0x0074  BX   LR
        ];
        for (int i = 0; i < lookup.Length; i++) W16(rom, 0x0060 + i * 2, lookup[i]);

        // ── memcpy44 at 0x0100 ────────────────────────────────────────────────
        //   void *memcpy44(void *dst, const void *src, uint n)  -- n bytes (multiple of 4)
        //   Uses CBZ up-front guard so n=0 returns immediately without corrupting memory.
        //   Layout: 0x0100 – 0x0110 (9 halfwords = 18 bytes)
        ReadOnlySpan<ushort> memcpy44 =
        [
            0xB510,  // 0x0100  PUSH {r4, lr}
            0x4604,  // 0x0102  MOV  r4, r0               ; save original dst
            0xB11A,  // 0x0104  CBZ  r2, done (+6)         ; PC=0x0108, +6 → 0x010E
            0xC908,  // 0x0106  LDMIA r1!, {r3}            ; loop: r3 = *src++
            0xC008,  // 0x0108  STMIA r0!, {r3}            ; *dst++ = r3
            0x3A04,  // 0x010A  SUBS r2, r2, #4
            0xD1FB,  // 0x010C  BNE  loop          (-10)   ; PC=0x0110, -10 → 0x0106
            0x4620,  // 0x010E  MOV  r0, r4                ; done: return original dst
            0xBD10,  // 0x0110  POP  {r4, pc}
        ];
        for (int i = 0; i < memcpy44.Length; i++) W16(rom, 0x0100 + i * 2, memcpy44[i]);

        // ── memset4  at 0x0120 ────────────────────────────────────────────────
        //   void *memset4(void *dst, uint8_t c, uint n)
        //   Fills n bytes (multiple of 4) with word pattern (c,c,c,c); returns dst.
        //   Uses CBZ up-front guard: decrements n AFTER each store (no off-by-one).
        //   Layout: 0x0120 – 0x0138 (13 halfwords = 26 bytes)
        ReadOnlySpan<ushort> memset4 =
        [
            0xB510,  // 0x0120  PUSH {r4, lr}
            0x4604,  // 0x0122  MOV  r4, r0              ; save original dst
            0xB2C9,  // 0x0124  UXTB r1, r1              ; r1 = c & 0xFF (zero-extend)
            0x020B,  // 0x0126  LSLS r3, r1, #8
            0x4319,  // 0x0128  ORRS r1, r3              ; r1 = c | (c<<8)
            0x040B,  // 0x012A  LSLS r3, r1, #16
            0x4319,  // 0x012C  ORRS r1, r3              ; r1 = 4-byte word pattern
            0xB112,  // 0x012E  CBZ  r2, done (+4)        ; PC=0x0132, +4 → 0x0136
            0xC002,  // 0x0130  STMIA r0!, {r1}          ; loop: *dst++ = word
            0x3A04,  // 0x0132  SUBS r2, r2, #4
            0xD1FC,  // 0x0134  BNE  loop          (-8)   ; PC=0x0138, -8 → 0x0130
            0x4620,  // 0x0136  MOV  r0, r4              ; done: return original dst
            0xBD10,  // 0x0138  POP  {r4, pc}
        ];
        for (int i = 0; i < memset4.Length; i++) W16(rom, 0x0120 + i * 2, memset4[i]);

        // ── Native-hook stubs ─────────────────────────────────────────────────
        //   0x0190: flash_range_erase  hook  — BX LR fallback (hook fires first)
        //   0x01A0: flash_range_program hook — BX LR fallback
        //   0x01C0: popcount32, 0x01D0: reverse32, 0x01E0: clz32, 0x01F0: ctz32
        W16(rom, 0x0190, 0x4770);  // BX LR
        W16(rom, 0x01A0, 0x4770);  // BX LR
        W16(rom, 0x01C0, 0x4770);  // BX LR (popcount32 — native hook)
        W16(rom, 0x01D0, 0x4770);  // BX LR (reverse32 — native hook)
        W16(rom, 0x01E0, 0x4770);  // BX LR (clz32 — native hook)
        W16(rom, 0x01F0, 0x4770);  // BX LR (ctz32 — native hook)

        // ── Function lookup table at 0x0200 ───────────────────────────────────
        //   Format: pairs of uint16_t {code, func_ptr}, terminated by {0, 0}.
        //   'RE' and 'RP' point to native-hook stubs so C# code can modify flash.
        ReadOnlySpan<ushort> funcTable =
        [
            0x434D, 0x0101,  // 'MC' = MEMCPY / MEMCPY44     (Thumb bit: 0x0100|1)
            0x534D, 0x0121,  // 'MS' = MEMSET / MEMSET4      (Thumb bit: 0x0120|1)
            0x4649, 0x0181,  // 'IF' = connect_internal_flash (no-op BX LR)
            0x5845, 0x0181,  // 'EX' = flash_exit_xip         (no-op BX LR)
            0x4552, 0x0191,  // 'RE' = flash_range_erase  →  native hook at 0x0190
            0x5052, 0x01A1,  // 'RP' = flash_range_program → native hook at 0x01A0
            0x4346, 0x0181,  // 'FC' = flash_flush_cache       (no-op BX LR)
            0x5843, 0x0181,  // 'CX' = flash_enter_cmd_xip    (no-op BX LR)
            0x0000, 0x0000,  // terminator
        ];
        for (int i = 0; i < funcTable.Length; i++) W16(rom, 0x0200 + i * 2, funcTable[i]);

        // Data table at 0x0250: just a terminator
        W16(rom, 0x0250, 0x0000);
    }

    /// <summary>Load a binary image into BootROM at 0x00000000 (max 16 KB).</summary>
    public unsafe void LoadBootRom(ReadOnlySpan<byte> image)
    {
        if (image.Length > 0x4000)
            throw new ArgumentException("BootROM image exceeds 16 KB");

        image.CopyTo(new Span<byte>(Bus.PtrBootRom, image.Length));
    }

    /// <summary>Total instructions executed by Core 0 since reset.</summary>
    public long InstructionCount => Cpu.Cycles;

    /// <summary>True while core 0 is parked in WFI/WFE waiting for an event. Lets a test harness coarsen
    /// the run quantum while idle, and gates the PIO's per-tick NVIC recompute to the sleeping window.</summary>
    public bool Core0Waiting => Cpu.Registers.Waiting;

    /// <summary>True once Core 1 has been launched via the SIO FIFO multicore handshake.</summary>
    public bool Core1Launched => _core1Launched;

    /// <summary>
    /// Wall-clock cycles elapsed during the most recent <see cref="Run"/> call.
    /// When Core 1 is launched this is <c>max(core0, core1)</c> — both cores run in
    /// parallel on real hardware — never the sum.
    /// </summary>
    public long LastElapsedCycles { get; private set; }

    /// <summary>
    /// Run both cores for approximately <paramref name="instructions"/> instructions each,
    /// then tick all time-aware peripherals.
    /// Core 0 always runs; Core 1 only runs after it has been launched by the firmware
    /// via the SIO FIFO multicore handshake (RP2040 datasheet §2.8.3).
    /// </summary>
    public void Run(int instructions)
    {
        // ── Core 0 ────────────────────────────────────────────────────
        _activeCoreId = 0;
        var before0 = Cpu.Cycles;
        Cpu.Run(instructions);
        var delta = Cpu.Cycles - before0;

        // ── Core 1 (if launched) ───────────────────────────────────────
        if (_core1Launched)
        {
            _activeCoreId = 1;
            var before1 = Cpu1.Cycles;
            Cpu1.Run(instructions);
            _activeCoreId = 0;
            // Both cores run in parallel on real hardware; wall-clock elapsed
            // is the maximum of the two cycle counts.
            delta = Math.Max(delta, (int)(Cpu1.Cycles - before1));
        }

        LastElapsedCycles = delta;

        foreach (var t in _tickables)
            t.Tick(delta);
    }

    /// <summary>Diagnostic sibling of <see cref="Run"/> that drives core 0 through the per-instruction
    /// profiling path (<see cref="CortexM0Plus.RunProfiled"/>), then ticks peripherals. Core 1, if
    /// launched, runs unobserved. For boot/fault tracing only — not throughput-sensitive.</summary>
    public void RunProfiled(int instructions, Core.Cpu.IProfilingObserver observer)
    {
        _activeCoreId = 0;
        var before0 = Cpu.Cycles;
        Cpu.RunProfiled(instructions, observer);
        var delta = Cpu.Cycles - before0;
        if (_core1Launched)
        {
            _activeCoreId = 1;
            var before1 = Cpu1.Cycles;
            Cpu1.Run(instructions);
            _activeCoreId = 0;
            delta = Math.Max(delta, (int)(Cpu1.Cycles - before1));
        }
        LastElapsedCycles = delta;
        foreach (var t in _tickables)
            t.Tick(delta);
    }

    /// <summary>Reset Core 0. Core 1 is also reset and its launched state is cleared.</summary>
    public void Reset()
    {
        _core1Launched = false;
        _activeCoreId  = 0;
        Sio.ResetMulticoreLaunch();
        Cpu.Reset();
        Cpu1.Reset();
    }

    public void Dispose() => Bus.Dispose();

    // ── Multicore launch ──────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="SioPeripheral"/> when Core 0 completes the RP2040 §2.8.3
    /// multicore launch handshake.  Configures Core 1's registers (VTOR, SP, PC) and
    /// marks it as runnable so subsequent <see cref="Run"/> calls execute it.
    /// </summary>
    private void LaunchCore1(uint vtor, uint sp, uint entry)
    {
        // Reset clears the lockup flag (handles re-launch of a previously faulted core).
        Cpu1.Reset();
        Cpu1.Registers.VTOR = vtor;
        Cpu1.Registers.SP   = sp;
        Cpu1.Registers.PC   = entry & 0xFFFFFFFEu; // strip Thumb bit
        // The bootrom branches to the received entry point with LR still pointing at its own
        // wait_for_vector loop, so a Core 1 entry function that RETURNS lands back in the bootrom
        // and waits for the next launch. pico-sdk relies on that: core1_wrapper() is a plain
        // `push {r4, lr}` … `pop {r4, pc}` function, so MicroPython's _thread ends by returning
        // through it. Leaving LR at its reset value pops PC = 0 instead, and since MicroPython's
        // HardFault handler is a bkpt, every finished thread ended in lockup.
        if (_core1WaitForVector != 0)
            Cpu1.Registers.LR = _core1WaitForVector;
        // The Run() loop will auto-update the fetch cache on the first instruction.
        _core1Launched = true;
    }

    /// <summary>Bootrom <c>wait_for_vector</c> ('W','V'), Thumb-encoded; 0 when the ROM lacks it.</summary>
    private uint _core1WaitForVector;

    /// <summary>
    /// Core 1's entry function returned into the bootrom's <c>wait_for_vector</c> loop. That loop's
    /// side of the §2.8.3 handshake is what <see cref="SioPeripheral"/> already plays, so rather
    /// than execute it we park Core 1: it stops stepping until the next launch sequence, which is
    /// exactly what "sitting in the bootrom waiting for a vector" looks like from Core 0.
    /// </summary>
    private void Core1ReturnedToBootrom(CortexM0Plus cpu)
    {
        _core1Launched = false;
        Sio.ResetMulticoreLaunch();
        // wait_for_vector is a WFE loop, so park Core 1 in it rather than let the hook's automatic
        // `PC = LR` carry it back into the finished thread. LR points at the loop too, which keeps
        // the core in place for the rest of the batch already in flight — Run() bails out of a
        // waiting core on its next step, and Machine.Run stops stepping it once launched is clear.
        cpu.Registers.LR = _core1WaitForVector;
        cpu.Registers.EventRegistered = false;
        cpu.Registers.Waiting = true;
    }

    /// <summary>
    /// Resolves a bootrom function address through the ROM's own function table (pointer at offset
    /// 0x14), mirroring <c>rom_func_lookup</c>. Returns the Thumb-encoded address, or 0 if absent.
    /// </summary>
    private static unsafe uint RomFuncLookup(byte* rom, char a, char b)
    {
        var code = (ushort)(a | (b << 8));
        for (int p = rom[0x14] | (rom[0x15] << 8); p < 16 * 1024 - 4; p += 4)
        {
            var entry = (ushort)(rom[p] | (rom[p + 1] << 8));
            if (entry == 0) break;
            if (entry == code) return (uint)(rom[p + 2] | (rom[p + 3] << 8));
        }
        return 0;
    }

    /// <summary>
    /// Invoked when firmware releases PROC1 from <see cref="PsmPeripheral"/>.FRCE_OFF, i.e.
    /// calls pico-sdk's <c>multicore_reset_core1()</c> (MicroPython does this before every
    /// <c>_thread.start_new_thread</c>).  On real silicon Core 1 leaves reset, re-runs its
    /// bootrom and pushes a "ready" word to Core 0, which is blocked in a FIFO pop.  We
    /// reproduce that here: stop and reset Core 1, clear the SIO launch handshake so the
    /// following §2.8.3 sequence re-launches it, and inject the ready word to unblock Core 0.
    /// Without this the register write is inert, Core 1 never signals, and Core 0 sleeps in
    /// WFE forever.
    /// </summary>
    private void ResetCore1FromPsm()
    {
        _core1Launched = false;
        Sio.ResetMulticoreLaunch();
        Cpu1.Reset();
        Sio.SignalCore1BootromReady();
    }

    // ── Per-core PPB router ───────────────────────────────────────────

    /// <summary>
    /// Routes PPB (0xE000xxxx) bus accesses to Core 0's or Core 1's
    /// <see cref="PpbPeripheral"/> based on the currently-active core ID.
    /// Each core has its own private NVIC, SysTick and SCB in the real RP2040.
    /// </summary>
    private sealed class PerCorePpbRouter : IMemoryMappedDevice, ITickable
    {
        private readonly PpbPeripheral _ppb0;
        private readonly PpbPeripheral _ppb1;
        private readonly Func<int>     _getActiveCoreId;

        public PerCorePpbRouter(PpbPeripheral ppb0, PpbPeripheral ppb1,
                                Func<int> getActiveCoreId)
        {
            _ppb0 = ppb0;
            _ppb1 = ppb1;
            _getActiveCoreId = getActiveCoreId;
        }

        private PpbPeripheral Active =>
            _getActiveCoreId() == 1 ? _ppb1 : _ppb0;

        public uint Size => 0x10000000;  // covers the full 0xE region

        public uint   ReadWord(uint address)              => Active.ReadWord(address);
        public ushort ReadHalfWord(uint address)          => Active.ReadHalfWord(address);
        public byte   ReadByte(uint address)              => Active.ReadByte(address);
        public void   WriteWord(uint address, uint value) => Active.WriteWord(address, value);
        public void   WriteHalfWord(uint address, ushort value) =>
            Active.WriteHalfWord(address, value);
        public void   WriteByte(uint address, byte value) => Active.WriteByte(address, value);

        /// <summary>Tick both PPBs (SysTick, etc.) by the same delta cycles.</summary>
        public void Tick(long deltaCycles)
        {
            _ppb0.Tick(deltaCycles);
            _ppb1.Tick(deltaCycles);
        }
    }
}

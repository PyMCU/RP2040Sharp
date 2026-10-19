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
using RP2040.Peripherals.PinLevel;
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

    /// <summary>The serial blocks that drive and sample the GPIO pads themselves (docs/pin-level-buses.md).</summary>
    public PinLevelBus PinLevelBuses { get; }

    /// <param name="flashSize">Flash size in bytes.</param>
    /// <param name="bootrom">Bootrom revision.</param>
    /// <param name="pinLevelBuses">Serial blocks that drive and sample the pads once firmware routes their function to
    /// them (I2C / SPI masters, UARTs); a block whose pads are never routed keeps its transaction-level behaviour.
    /// None (the default) leaves every block transaction-level and the machine exactly as before.</param>
    public RP2040Machine(uint flashSize = 2 * 1024 * 1024,
                         RP2040BootromRevision bootrom = RP2040BootromRevision.B1,
                         PinLevelBus pinLevelBuses = PinLevelBus.None)
    {
        _bootromRevision = bootrom;
        PinLevelBuses = pinLevelBuses & PinLevelBus.All;
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
        // AIRCR.SYSRESETREQ resets the core that wrote it (datasheet 2.4.2.9), nothing else.
        Ppb.OnSystemResetRequest  += ResetCore0FromAircr;
        Ppb1.OnSystemResetRequest += ResetCore1FromAircr;

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
        SysInfo = new SysInfoPeripheral(bootrom);
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
        IoBank0 = new IoBank0Peripheral(Sio, Cpu) { Cpu1 = Cpu1 };
        apb.Register(0x40014000, IoBank0);

        // PADS_BANK0 @ 0x4001C000 (slot 7), PADS_QSPI @ 0x40020000 (slot 8)
        PadsBank0 = new PadsPeripheral();
        apb.Register(0x4001C000, PadsBank0);
        Sio.PadsBank0 = PadsBank0;   // floating inputs settle on their pull resistor
        IoBank0.Pads = PadsBank0;    // IE gates the input buffer, OD the driver; changes re-evaluate the pads

        PadsQspi = new PadsPeripheral(qspi: true);
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
                Pwm.SetBInput((pin >> 1) & 7, level);   // syncs the PWM first: the gate changes from here on
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
        // Spi0/Spi1 tick so their TX FIFO drains over time: the TX DREQ follows real occupancy
        // (see WireSpiDreq), and a FIFO that never drains never frees a beat.
        _ppbRouter = ppbRouter;
        // Pin-level UARTs and I2C masters count with time (their engines); otherwise they are not ticked at all.
        List<ITickable> tickables = [ppbRouter, Timer, Pwm, Pio0, Pio1, Rtc, Watchdog, Adc, Usb, Dma, Spi0, Spi1];
        if ((PinLevelBuses & PinLevelBus.Uart) != 0) tickables.AddRange([Uart0, Uart1]);
        if ((PinLevelBuses & PinLevelBus.I2c) != 0) tickables.AddRange([I2c0, I2c1]);
        _tickables = [.. tickables];
        _schedule = new DeferredTick[_tickables.Length];
        for (var i = 0; i < _tickables.Length; i++)
            _schedule[i] = new DeferredTick(this, _tickables[i], i);
        Timer.Schedule = _schedule[1];
        Timer.SweepClock = () => _lastSweepCpu0;
        Pwm.Schedule  = _schedule[2];
        Pio0.Schedule = _schedule[3];
        Pio1.Schedule = _schedule[4];
        Rtc.Schedule  = _schedule[5];
        Usb.Schedule  = _schedule[8];
        // Bus accesses (CPU or DMA) bring a deferred peripheral up to date first and make it plan again after.
        apb.RegisterSchedule(0x40054000, _schedule[1]);
        apb.RegisterSchedule(0x40050000, _schedule[2]);
        ahb.RegisterSchedule(0x50200000, _schedule[3], readsMutate: true);
        ahb.RegisterSchedule(0x50300000, _schedule[4], readsMutate: true);
        apb.RegisterSchedule(0x4005C000, _schedule[5]);
        apb.RegisterSchedule(0x40058000, _schedule[6]);
        apb.RegisterSchedule(0x4004C000, _schedule[7], readsMutate: true);
        ahb.RegisterSchedule(0x50100000, _schedule[8]);
        ahb.RegisterSchedule(0x50000000, _schedule[9]);
        apb.RegisterSchedule(0x4003C000, _schedule[10], readsMutate: true);
        apb.RegisterSchedule(0x40040000, _schedule[11], readsMutate: true);
        _schedule[9].AlwaysAsk = _schedule[10].AlwaysAsk = _schedule[11].AlwaysAsk = true;
        for (var i = 12; i < _schedule.Length; i++)
        {
            // Pin-level UART / I2C: their next event moves with pad inputs as well as accesses; asking is a compare.
            _schedule[i].AlwaysAsk = true;
            var baseAddress = _tickables[i] == Uart0 ? 0x40034000u : _tickables[i] == Uart1 ? 0x40038000u
                            : _tickables[i] == I2c0 ? 0x40044000u : 0x40048000u;
            apb.RegisterSchedule(baseAddress, _schedule[i], readsMutate: true);
        }
        // SysTick counts from the cores' own cycle counters (nothing is owed); a write only changes when it fires.
        ppbRouter.OnWrite = () => ScheduleEpoch++;
        Watchdog.OnReset = SystemReset;

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
        // SPI0 TX(16), RX(17), SPI1 TX(18), RX(19). The TX line follows the real FIFO occupancy —
        // an always-ready TX let a channel drain its whole buffer at trigger time, overrunning the
        // 8-deep RX FIFO — and both lines re-arm their channel so a stall is never permanent.
        WireSpiDreq(Spi0, 16);
        WireSpiDreq(Spi1, 18);
        // UART0 TX(20), RX(21), UART1 TX(22), RX(23). TX is always ready in the transaction model; a pin-level UART has
        // a real TX FIFO, which re-arms its channel as the engine takes bytes off it.
        Dma.RegisterDreq(20, () => Uart0.TxFifoNotFull);
        Dma.RegisterDreq(21, () => Uart0.RxDataAvailable);
        Dma.RegisterDreq(22, () => Uart1.TxFifoNotFull);
        Dma.RegisterDreq(23, () => Uart1.RxDataAvailable);
        Uart0.OnTxSpace += () => Dma.ResumeDreq(20);
        Uart1.OnTxSpace += () => Dma.ResumeDreq(22);
        // I2C0 TX(32), RX(33), I2C1 TX(34), RX(35): the same for the TX FIFO of a pin-level master.
        Dma.RegisterDreq(32, () => I2c0.TxFifoNotFull);
        Dma.RegisterDreq(33, () => I2c0.RxDataAvailable);
        Dma.RegisterDreq(34, () => I2c1.TxFifoNotFull);
        Dma.RegisterDreq(35, () => I2c1.RxDataAvailable);
        I2c0.OnTxSpace += () => Dma.ResumeDreq(32);
        I2c1.OnTxSpace += () => Dma.ResumeDreq(34);
        // ADC DREQ 36: RX FIFO has data
        Dma.RegisterDreq(36, () => Adc.HasFifoData);
        Dma.RegisterDreq(38, () => true);
        Dma.RegisterDreq(39, () => true);
        // A channel that stalled on an empty RX FIFO must re-arm when the peripheral produces data (the
        // PIO already does this via OnRxPush); otherwise it stays BUSY forever. Spi0/Spi1 are not here:
        // WireSpiDreq above already hooks both their RX and their TX resume.
        Uart0.OnRxAvailable += () => Dma.ResumeDreq(21);
        Uart1.OnRxAvailable += () => Dma.ResumeDreq(23);
        I2c0.OnRxAvailable  += () => Dma.ResumeDreq(33);
        I2c1.OnRxAvailable  += () => Dma.ResumeDreq(35);
        Adc.OnFifoData      += () => Dma.ResumeDreq(36);

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

        if (PinLevelBuses != PinLevelBus.None) WirePinLevelBuses(PinLevelBuses);
    }

    // ── Pin-level buses (docs/pin-level-buses.md) ─────────────────────────────

    /// <summary>
    /// Attach a bit-level engine to each selected serial block and register it with IO_BANK0 as that block's pad
    /// function, so a GPIO muxed to SPI (FUNCSEL 1), UART (2) or I2C (3) is driven by, and feeds, the block. "Routed"
    /// is judged live from IO_BANK0, so a block whose pads firmware never configures keeps its transaction behaviour.
    /// </summary>
    private void WirePinLevelBuses(PinLevelBus buses)
    {
        var io = IoBank0;
        // The dividers count clk_peri (SPI, UART) or clk_sys (I2C) as firmware programmed them from the PLLs; the core
        // runs at a fixed 125 MHz, so the engines scale those counts to its cycles. Until firmware switches clk_sys to
        // its aux source (clocks_init), the core's own 125 MHz is the clock (it never ran from the ROSC).
        Clocks.PllSys ??= PllSys;
        Clocks.PllUsb ??= PllUsb;
        uint PeriHz() => Clocks.ClkSysFromAux ? Clocks.ClkPeriHz() : CLK_HZ;
        uint SysHz() => Clocks.ClkSysFromAux ? Clocks.ClkSysHz : CLK_HZ;
        for (var b = 0; b < 2; b++)
        {
            var block = b;
            if ((buses & PinLevelBus.Spi) != 0)
            {
                var spi = block == 0 ? Spi0 : Spi1;
                var engine = spi.EnablePinLevel(
                    () => io.IsFunctionRouted(PadFunctions.FuncSpi, block, PadFunctions.SpiSck), EngineClock, PeriHz);
                spi.Changed = EngineChanged;
                io.RegisterPadFunction(PadFunctions.FuncSpi, block, engine);
            }
            if ((buses & PinLevelBus.Uart) != 0)
            {
                var uart = block == 0 ? Uart0 : Uart1;
                var engine = uart.EnablePinLevel(
                    () => io.IsFunctionRouted(PadFunctions.FuncUart, block, PadFunctions.UartTx), EngineClock, PeriHz);
                uart.Changed = EngineChanged;
                io.RegisterPadFunction(PadFunctions.FuncUart, block, engine);
            }
            if ((buses & PinLevelBus.I2c) != 0)
            {
                var i2c = block == 0 ? I2c0 : I2c1;
                var engine = i2c.EnablePinLevel(() =>
                    io.IsFunctionRouted(PadFunctions.FuncI2c, block, PadFunctions.I2cSda) &&
                    io.IsFunctionRouted(PadFunctions.FuncI2c, block, PadFunctions.I2cScl), EngineClock, SysHz);
                i2c.Changed = EngineChanged;
                io.RegisterPadFunction(PadFunctions.FuncI2c, block, engine);
            }
        }
    }

    // Core 0 / core 1 cycle counts at the start of core 1's turn in the slice in flight (see EngineClock).
    private long _core1TurnBase0, _core1TurnBase1;

    /// <summary>
    /// The pin-level engines' clock: core 0's cycle counter at the access in flight. Core 0 runs its whole slice first,
    /// then core 1, then the peripherals sweep: an access by core 0 happens at its own count, one by core 1 at the
    /// slice start plus the cycles core 1 has run in it (never past core 0's count), anything else at core 0's count.
    /// An engine is brought up to this clock at every access, so what firmware reads and the edges it starts do not
    /// depend on how the run was sliced.
    /// </summary>
    public long EngineClock() => Math.Min(Cpu.Cycles, ExecCycle());

    /// <summary>
    /// The core-0 cycle of what the machine is doing right now, as <see cref="EngineClock"/> but not clamped to core
    /// 0's count: during core 1's turn the slice start plus the cycles core 1 has run in it (it may run more than core 0
    /// did); otherwise core 0's count, which in a sweep is the end of the slice.
    /// </summary>
    public long ExecCycle()
    {
        if (_activeCoreId == 1)
            return _core1TurnBase0 + (Cpu1.Cycles - _core1TurnBase1);
        return Cpu.Cycles;
    }

    /// <summary>
    /// Cycles from core 0's clock until the next step of a pin-level engine that has work, on a pad in
    /// <paramref name="watch"/>: an SPI master shifting or about to take a queued word, a UART transmitter, an I2C
    /// master on the bus. 0 when an engine will look at its host on the next sweep; long.MaxValue when none has one
    /// coming (idle engines, unwatched pads, edges only firmware or an input can start). Reads no register and syncs
    /// nothing.
    /// </summary>
    public long CyclesUntilPadEdge(uint watch)
    {
        if (PinLevelBuses == PinLevelBus.None || watch == 0) return long.MaxValue;
        long now = Cpu.Cycles, d = long.MaxValue;
        var io = IoBank0;
        if ((PinLevelBuses & PinLevelBus.Spi) != 0)
        {
            if ((io.FunctionPads(PadFunctions.FuncSpi, 0) & watch) != 0) d = Math.Min(d, Spi0.CyclesUntilPadEdge(now));
            if ((io.FunctionPads(PadFunctions.FuncSpi, 1) & watch) != 0) d = Math.Min(d, Spi1.CyclesUntilPadEdge(now));
        }
        if ((PinLevelBuses & PinLevelBus.Uart) != 0)
        {
            if ((io.FunctionPads(PadFunctions.FuncUart, 0) & watch) != 0) d = Math.Min(d, Uart0.CyclesUntilPadEdge(now));
            if ((io.FunctionPads(PadFunctions.FuncUart, 1) & watch) != 0) d = Math.Min(d, Uart1.CyclesUntilPadEdge(now));
        }
        if ((PinLevelBuses & PinLevelBus.I2c) != 0)
        {
            if ((io.FunctionPads(PadFunctions.FuncI2c, 0) & watch) != 0) d = Math.Min(d, I2c0.CyclesUntilPadEdge(now));
            if ((io.FunctionPads(PadFunctions.FuncI2c, 1) & watch) != 0) d = Math.Min(d, I2c1.CyclesUntilPadEdge(now));
        }
        return d == long.MaxValue ? d : Math.Max(0, d);
    }

    // The cut of the run of core 0 in flight (RunCut, RunAwakeBounded with a watch): armed only while core 0 runs.
    private bool _cutArmed;
    private uint _cutWatch;

    /// <summary>A pin-level engine's next event may have moved (an access, a pad input): plan again, and move the cut
    /// of a cut run of core 0 to the engines' next pad edge.</summary>
    private void EngineChanged()
    {
        ScheduleEpoch++;
        if (!_cutArmed || _activeCoreId != 0) return;
        var e = CyclesUntilPadEdge(_cutWatch);
        Cpu.CutCycle = Math.Min(CutLimit, e == long.MaxValue ? long.MaxValue : Cpu.Cycles + e);
    }

    /// <summary>
    /// A core-0 cycle no cut run of core 0 (<see cref="RunCut"/>, <see cref="RunAwakeBounded"/>) runs past: it stops
    /// before the first instruction that would start at or after it, and sleeps only up to it. long.MaxValue (the
    /// default) for none. The co-simulation loop sets it for a run with a target cycle (<see cref="RP2040CoSim"/>).
    /// </summary>
    public long CutLimit { get; set; } = long.MaxValue;

    /// <summary>Both cores stopped (locked up): <paramref name="cycles"/> of time still pass, core 0's clock and the
    /// peripherals' with it.</summary>
    public void ChargeStopped(long cycles)
    {
        if (cycles <= 0) return;
        Cpu.Cycles += cycles;
        LastElapsedCycles = cycles;
        Sweep(cycles, cycles);
    }

    /// <summary>Inside a cut run of core 0, stop it right after the instruction in flight (a host that watches a pad that
    /// instruction moved). Does nothing otherwise.</summary>
    public void RequestCut()
    {
        if (_cutArmed && _activeCoreId == 0) Cpu.CutRequest = true;
    }

    private void ArmCut(uint watch)
    {
        var e = CyclesUntilPadEdge(watch);
        Cpu.CutRequest = false;
        Cpu.CutCycle = Math.Min(CutLimit, e == long.MaxValue ? long.MaxValue : Cpu.Cycles + Math.Max(1, e));
        _cutWatch = watch;
        _cutArmed = true;
    }

    private void DisarmCut()
    {
        _cutArmed = false;
        Cpu.CutRequest = false;
        Cpu.CutCycle = long.MaxValue;
    }

    /// <summary>
    /// A <see cref="Run"/> slice that ends early, for a host that answers pad edges between calls: before the first
    /// instruction of core 0 that would start at or after the next pin-level edge on a pad in <paramref name="watch"/>
    /// (<see cref="CyclesUntilPadEdge"/>, followed as accesses move it), and right after an instruction a
    /// <see cref="RequestCut"/> came from. Core 1 then runs as many instructions as core 0 did, and the peripherals
    /// sweep. With nothing to cut it is exactly <see cref="Run"/>.
    /// </summary>
    /// <returns>The instructions core 0 ran (at least 1).</returns>
    public int RunCut(int instructions, uint watch)
    {
        _activeCoreId = 0;
        var before0 = Cpu.Cycles;
        ArmCut(watch);
        try { Cpu.RunCut(instructions); }
        finally { DisarmCut(); }
        int k = instructions - Cpu.CutLeft;
        FinishSlice(k, Cpu.Cycles - before0);
        return k;
    }

    /// <summary>The machine's clock went back (watchdog reboot resets the core cycle counter): restart the engines on it.</summary>
    private void RebaseEngines()
    {
        if (PinLevelBuses == PinLevelBus.None) return;
        var now = Cpu.Cycles;
        Spi0.Rebase(now); Spi1.Rebase(now);
        Uart0.Rebase(now); Uart1.Rebase(now);
        I2c0.Rebase(now); I2c1.Rebase(now);
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
            // Hook the real function ENTRY points resolved from the ROM table ('RE'/'RP'); the old fixed
            // 0x237C/0x23C4 are B1-only and, on B2, landed mid-function (after PUSH {r3-r7,lr}), leaving
            // the caller's SP 24 bytes low and R4-R7 clobbered.
            var eraseEntry = RomFuncLookup(Bus.PtrBootRom, 'R', 'E') & ~1u;
            var progEntry  = RomFuncLookup(Bus.PtrBootRom, 'R', 'P') & ~1u;
            if (eraseEntry != 0) { Cpu.RegisterNativeHook(eraseEntry, FlashEraseHook); Cpu1.RegisterNativeHook(eraseEntry, FlashEraseHook); }
            if (progEntry  != 0) { Cpu.RegisterNativeHook(progEntry,  FlashProgramHook); Cpu1.RegisterNativeHook(progEntry,  FlashProgramHook); }

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
        var offset = (long)(cpu.Registers.R0 & (Bus.FlashSize - 1));
        // long: guest-controlled offset + count must not wrap past the bounds check (host heap write).
        var count  = (long)(int)cpu.Registers.R1;
        if (count < 0 || offset + count > Bus.FlashSize) count = Bus.FlashSize - offset;
        if (count > 0)
            new Span<byte>(Bus.PtrFlash + offset, (int)count).Fill(0xFF);
    }

    /// <summary>
    /// Native hook for <c>flash_range_program(uint32_t flash_offs, const uint8_t* data, size_t count)</c>.
    /// Copies bytes from SRAM (or anywhere in the address space) into the emulated flash.
    /// Called by the CPU when PC = 0x01A0 (registered in <see cref="LoadFlash"/>).
    /// </summary>
    private unsafe void FlashProgramHook(Core.Cpu.CortexM0Plus cpu)
    {
        var flashOffset = (long)(cpu.Registers.R0 & (Bus.FlashSize - 1));
        var srcAddr     = cpu.Registers.R1;
        // long: guest-controlled offset + count must not wrap past the bounds check (host heap write).
        var count       = (long)(int)cpu.Registers.R2;
        if (count < 0 || flashOffset + count > Bus.FlashSize)
            count = Bus.FlashSize - flashOffset;
        for (var i = 0L; i < count; i++)
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

    /// <summary>Load a binary image into BootROM at 0x00000000 (max 16 KB).</summary>
    public unsafe void LoadBootRom(ReadOnlySpan<byte> image)
    {
        if (image.Length > 0x4000)
            throw new ArgumentException("BootROM image exceeds 16 KB");

        image.CopyTo(new Span<byte>(Bus.PtrBootRom, image.Length));
    }

    /// <summary>Register a SPI's TX (<paramref name="dreqTx"/>) and RX (<paramref name="dreqTx"/>+1)
    /// DMA DREQ lines and wire the resume hooks. Without the TX line tracking real FIFO occupancy a
    /// paced channel drains its whole buffer at trigger time; without the resume hooks a channel that
    /// stalls on an empty FIFO stays BUSY forever, which is what hung any <c>spi.write()</c> of 32
    /// bytes or more (MicroPython's DMA threshold).</summary>
    private void WireSpiDreq(SpiPeripheral spi, int dreqTx)
    {
        Dma.RegisterDreq(dreqTx,     () => spi.TxFifoNotFull);
        Dma.RegisterDreq(dreqTx + 1, () => spi.RxDataAvailable);
        spi.OnTxSpace     += () => Dma.ResumeDreq(dreqTx);
        spi.OnRxAvailable += () => Dma.ResumeDreq(dreqTx + 1);
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
        FinishSlice(instructions, Cpu.Cycles - before0);
    }

    /// <summary>
    /// The rest of a <see cref="Run"/> slice once core 0 has run its part (<paramref name="core0Delta"/> cycles):
    /// core 1's turn, if launched, then the peripheral sweep with the longer of the two.
    /// </summary>
    public void FinishSlice(int instructions, long core0Delta)
    {
        var delta = core0Delta;

        // ── Core 1 (if launched) ───────────────────────────────────────
        if (_core1Launched)
        {
            var before1 = Cpu1.Cycles;
            _core1TurnBase0 = Cpu.Cycles - core0Delta;
            _core1TurnBase1 = before1;
            _activeCoreId = 1;
            Cpu1.Run(instructions);
            _activeCoreId = 0;
            // Both cores run in parallel on real hardware; wall-clock elapsed
            // is the maximum of the two cycle counts.
            delta = Math.Max(delta, (int)(Cpu1.Cycles - before1));
        }

        LastElapsedCycles = delta;
        Sweep(delta, core0Delta);
    }

    // ── Peripheral sweeps and deferred ticks (docs/peripheral-deadlines.md) ──

    private readonly PerCorePpbRouter _ppbRouter;
    private readonly DeferredTick[] _schedule;
    private bool _deferTicks;
    private long _swept;
    private long _lastSweepCpu0;
    private long _sweepDelta;
    private int _sweepCursor = int.MaxValue;

    /// <summary>Cycles of every peripheral sweep so far, counted or still owed: the peripherals' time base.</summary>
    public long SweptCycles => _swept;

    /// <summary>Bumped whenever a deferred peripheral's next event may have moved (an access, a tick, a SysTick write).</summary>
    internal long ScheduleEpoch;

    /// <summary>
    /// Put each time-aware peripheral's tick off until its next event or until something accesses it, instead of
    /// ticking all of them after every slice. Exactly the same results (the peripherals are synchronized before every
    /// register access and host call); off by default, which ticks every peripheral every sweep (the reference).
    /// Turning it off brings every peripheral up to date first.
    /// </summary>
    public bool DeferPeripheralTicks
    {
        get => _deferTicks;
        set
        {
            if (value == _deferTicks) return;
            if (!value) SyncPeripherals();
            _deferTicks = value;
            foreach (var e in _schedule) e.Reset(e.Deferrable == null ? _swept : e.Applied);
        }
    }

    /// <summary>The swept total peripheral <paramref name="index"/> should stand at: during a sweep, the ones whose
    /// turn has not come yet still stand where the previous sweep left them.</summary>
    internal long SweptFor(int index) => index >= _sweepCursor ? _swept - _sweepDelta : _swept;

    /// <summary>Bring every deferred peripheral up to date (before a host reads or changes them from outside).</summary>
    public void SyncPeripherals()
    {
        foreach (var e in _schedule) e.Sync();
        ScheduleEpoch++;
    }

    /// <summary>
    /// Before a host drives or releases a pad input: the cycles already run belong to the old level, so every deferred
    /// peripheral (and pin-level engine) is brought to where a tick per sweep would have it, or a later tick would
    /// sample the new level retroactively. Outside a run that is the end of the last slice; inside one (an event
    /// handler answering a pad change), the sweeps so far, which is what the eager machine has ticked by then: during
    /// a sweep the peripherals whose turn has not come stand at the previous sweep (<see cref="SweptFor"/>), and a
    /// peripheral advancing in its own tick does not re-enter it.
    /// </summary>
    public void SyncForHostInput() => SyncPeripherals();

    /// <summary>
    /// Before a host reads pad outputs or peripheral state from outside: the peripherals are brought to the end of the
    /// last slice. From inside a sweep (a handler of a peripheral's own edge) it does nothing: the peripherals already
    /// stand where that sweep has them. (A catch-up never crosses a peripheral's next event, so a read moves nothing
    /// the firmware or the host could see.)
    /// </summary>
    public void SyncForHostRead()
    {
        if (_sweepCursor != int.MaxValue) return;
        SyncPeripherals();
    }

    /// <summary>Maps the peripherals' swept cycles to core 0's clock (see <see cref="ClockTimeline"/>).</summary>
    public ClockTimeline Timeline { get; } = new();

    private bool _exactPwmPadEdges;

    /// <summary>
    /// Report the edges of pads muxed to PWM through <see cref="IoBank0Peripheral.PadsChanged"/>, each stamped with the
    /// core-0 cycle of the counter step that made it (<see cref="IoBank0Peripheral.LastPadEdgeCycle"/>), and the output
    /// changes of PWM register writes at the write. Off (the default), PWM pads move silently and a host polls them.
    /// The PWM tick then takes its output-changing steps one by one; deferred ticks still end at them, so the result is
    /// the same either way: only the reports differ.
    /// </summary>
    public bool ExactPwmPadEdges
    {
        get => _exactPwmPadEdges;
        set
        {
            if (value == _exactPwmPadEdges) return;
            SyncPeripherals();
            _exactPwmPadEdges = value;
            Pwm.OutputStep = value ? OnPwmStep : null;
            Pwm.OutputsWritten = value ? OnPwmWritten : null;
        }
    }

    // GPIO n is PWM slice (n >> 1) & 7: pads 2s, 2s+1, 2s+16, 2s+17.
    private static uint PwmSlicePads(int slice) => (3u << (2 * slice) | 3u << (2 * slice + 16)) & ((1u << 30) - 1);

    private void OnPwmStep(int slice, long offset, long window)
    {
        var pads = PwmSlicePads(slice) & IoBank0.PwmMuxedPins;
        if (pads == 0) return;
        var swept = _schedule[2].Applied - window + offset;
        IoBank0.NotifyPadOutputsAt(pads, Timeline.CycleAt(swept));
    }

    private void OnPwmWritten()
    {
        var pads = IoBank0.PwmMuxedPins;
        if (pads != 0) IoBank0.ReportPadOutputs(pads);
    }

    /// <summary>Tick the time-aware peripherals for a slice of <paramref name="delta"/> cycles, in which core 0 ran
    /// <paramref name="core0Delta"/>.</summary>
    private void Sweep(long delta, long core0Delta)
    {
        _lastSweepCpu0 = Cpu.Cycles;
        _swept += delta;
        _sweepDelta = delta;
        Timeline.Mark(_swept, delta, _lastSweepCpu0, core0Delta);
        try
        {
            var schedule = _schedule;
            for (var i = 0; i < schedule.Length; i++)
            {
                var e = schedule[i];
                // From here on this peripheral stands at the new total; the ones after it not yet.
                _sweepCursor = i + 1;
                if (!_deferTicks || e.Deferrable == null) { e.Applied = _swept; e.Target.Tick(delta); }
                else e.OnSweep(_swept);
            }
        }
        finally { _sweepCursor = int.MaxValue; }
    }

    /// <summary>
    /// Swept cycles from now until the first sweep at which some peripheral (or a SysTick) has something to do:
    /// 0 when that is the next sweep, <see cref="long.MaxValue"/> when nothing is ever due. Meaningful only with
    /// <see cref="DeferPeripheralTicks"/> on.
    /// </summary>
    public long SweptCyclesUntilEvent()
    {
        var at = long.MaxValue;
        foreach (var e in _schedule)
            if (e.Deferrable != null) at = Math.Min(at, e.EventAt);
        var until = at == long.MaxValue ? long.MaxValue : Math.Max(0, at - _swept);
        return Math.Min(until, _ppbRouter.CyclesUntilEvent(_core1Launched));
    }

    /// <summary>
    /// True when a <see cref="CortexM0Plus.Run"/> of <paramref name="cpu"/> would only charge its slots as sleep:
    /// asleep, not locked up, no event registered and nothing pending that an interrupt check would take or wake on.
    /// </summary>
    internal static bool QuietAsleep(CortexM0Plus cpu)
    {
        ref readonly var r = ref cpu.Registers;
        return r.Waiting && !r.EventRegistered && !cpu.IsLockedUp && !WouldAct(cpu);
    }

    private static bool WouldAct(CortexM0Plus cpu)
    {
        ref readonly var r = ref cpu.Registers;
        return (r.PendingInterrupts & r.EnabledInterrupts) != 0 || r.PendingNMI || r.PendingSVCall
               || r.PendingPendSV || r.PendingSystick;
    }

    /// <summary>
    /// Whether whole sleeping slices can be skipped (<see cref="SkipSleepingSlices"/>): both cores quietly asleep
    /// (core 1 counts only once launched), deferred ticks on, and PIO's wake heartbeat would change nothing.
    /// </summary>
    public bool CanSkipSleepingSlices()
    {
        if (!_deferTicks || !QuietAsleep(Cpu)) return false;
        if (_core1Launched && !QuietAsleep(Cpu1)) return false;
        return Pio0.HeartbeatInert(Cpu, Cpu1) && Pio1.HeartbeatInert(Cpu, Cpu1);
    }

    /// <summary>
    /// <paramref name="slices"/> sleeping slices of <paramref name="instructions"/> slots at once, with the result
    /// of running them one by one with <see cref="Run"/>: each core is charged the slots as sleep, the sweeps add
    /// their cycles up, and PIO's heartbeat runs as after the last of them. The caller makes sure none of the sweeps
    /// reaches an event (<see cref="SweptCyclesUntilEvent"/>) and that <see cref="CanSkipSleepingSlices"/> holds.
    /// </summary>
    public void SkipSleepingSlices(long slices, int instructions)
    {
        var cycles = slices * instructions;
        Cpu.Cycles += cycles;
        Cpu.Registers.InterruptsUpdated = false;
        if (_core1Launched)
        {
            Cpu1.Cycles += cycles;
            Cpu1.Registers.InterruptsUpdated = false;
        }
        LastElapsedCycles = instructions;
        _lastSweepCpu0 = Cpu.Cycles;
        _swept += cycles;
        foreach (var e in _schedule)
            e.Deferrable?.SkipTick();
    }

    /// <summary>Whether core 1 lets core 0 run bounded: not launched, or quietly asleep.</summary>
    public bool Core1Inert => !_core1Launched || QuietAsleep(Cpu1);

    /// <summary>
    /// Called at each slice end of a bounded run that would otherwise be booked, with the sweep's result already
    /// standing (nothing is due in it): a host that looks at something after every slice (pads that follow a PWM
    /// counter) does it here; true ends the run with that slice.
    /// </summary>
    public Func<bool>? BoundaryCheck;

    /// <summary>Set by a host to end a bounded run (<see cref="RunAwakeBounded"/>) with the slice in flight.</summary>
    public bool EndBoundedRun;

    private readonly ArmGate _gate = new();

    /// <summary>
    /// Run core 0 for up to <paramref name="maxSlices"/> slices of <paramref name="instructions"/> each, with the
    /// result of calling <see cref="Run"/> that many times, in one interpreter loop. Core 1 must be
    /// <see cref="Core1Inert"/>, core 0 awake and deferred ticks on. A slice whose end changes nothing (core 1 still
    /// inert, no peripheral due, no <see cref="EndBoundedRun"/>) is booked without running: core 1 is charged its
    /// sleep and the sweep only adds its cycles up. The run returns at the first slice that needs more, with core 0's
    /// part of it done; the caller finishes it with <see cref="FinishSlice"/>(instructions, <see cref="BoundedSliceDelta"/>).
    /// </summary>
    /// <returns>The number of slices booked before the last one.</returns>
    /// <param name="cutWatch">Nonzero: the run is also cut as <see cref="RunCut"/> cuts a slice, on these pads, and the
    /// slice it ends in may then be shorter (<see cref="BoundedSliceSlots"/>). A <see cref="CutLimit"/> cuts it too.</param>
    public int RunAwakeBounded(int instructions, int maxSlices, uint cutWatch = 0)
    {
        var g = _gate;
        g.Machine = this;
        g.N = instructions;
        g.MaxSlices = maxSlices;
        g.Booked = 0;
        g.SliceStart = Cpu.Cycles;
        g.Plan();
        EndBoundedRun = false;
        _activeCoreId = 0;
        if (cutWatch == 0 && CutLimit == long.MaxValue)
        {
            Cpu.RunGated(g, instructions);
            BoundedSliceSlots = instructions;
            return g.Booked;
        }
        ArmCut(cutWatch);
        try { Cpu.RunGatedCut(g, instructions); }
        finally { DisarmCut(); }
        BoundedSliceSlots = instructions - Cpu.CutLeft;
        return g.Booked;
    }

    /// <summary>Instructions core 0 ran in the slice a bounded run ended in: the slice, unless a cut ended it early.</summary>
    public int BoundedSliceSlots { get; private set; }

    /// <summary>Core-0 cycles of the slice a bounded run ended in, for <see cref="FinishSlice"/>.</summary>
    public long BoundedSliceDelta => Cpu.Cycles - _gate.SliceStart;

    /// <summary>Core-0 cycle count at the start of the slice a bounded run ended in.</summary>
    public long BoundedSliceStart => _gate.SliceStart;

    /// <summary>The books of a bounded run: where its slices end and what their ends would have done.</summary>
    private sealed class ArmGate : ISliceGate
    {
        public RP2040Machine Machine = null!;
        public int N, MaxSlices, Booked;
        public long SliceStart;
        private long _eventAt, _epoch;

        public void Plan()
        {
            var m = Machine;
            _epoch = m.ScheduleEpoch;
            var until = m.SweptCyclesUntilEvent();
            _eventAt = until >= long.MaxValue - m._swept ? long.MaxValue : m._swept + until;
        }

        /// <summary>Core 0 finished a slice; true books its end and starts the next one.</summary>
        public bool NextSlice(CortexM0Plus cpu)
        {
            var m = Machine;
            // A core that went to sleep (or locked up) on the slice's last slot: the next slice is a sleeping one.
            if (Booked + 1 >= MaxSlices || m.EndBoundedRun || cpu.Registers.Waiting || cpu.IsLockedUp) return false;
            // A cut run whose cut falls on this slice end: the plain slices would end here too, and sweep.
            if (cpu.CutRequest || cpu.Cycles >= cpu.CutCycle) return false;
            var launched = m._core1Launched;
            if (launched && !QuietAsleep(m.Cpu1)) return false;
            var d0 = cpu.Cycles - SliceStart;
            var delta = launched ? Math.Max(d0, N) : d0;
            if (m.ScheduleEpoch != _epoch) Plan();
            if (m._swept + delta >= _eventAt) return false;
            // The host's own look at the slice end (pads it polls): the sweep would change nothing it sees.
            if (m.BoundaryCheck is { } check && check()) return false;
            // Book core 1's turn (a quiet sleeper's Run only charges the slots) and the sweep (nothing due).
            if (launched)
            {
                m.Cpu1.Cycles += N;
                m.Cpu1.Registers.InterruptsUpdated = false;
            }
            m._swept += delta;
            m._lastSweepCpu0 = cpu.Cycles;
            m.LastElapsedCycles = delta;
            Booked++;
            SliceStart = cpu.Cycles;
            return true;
        }
    }

    /// <summary>Running estimate of cycles per instruction, used by <see cref="RunCycles"/> to size slices.</summary>
    private double _cyclesPerInstruction = 2.0;

    /// <summary>
    /// Run until at least <paramref name="cycleBudget"/> wall-clock cycles have elapsed, ticking the
    /// time-aware peripherals after every slice. <see cref="Run"/> counts instructions and an
    /// instruction costs one to a dozen cycles, so each slice is sized from the cycles still owed and
    /// the cycles per instruction seen so far, aiming a little short of the budget. The tail narrows to
    /// single instructions, which keeps the overshoot to about one instruction.
    /// </summary>
    /// <param name="cycleBudget">Cycles to run. Zero or less does nothing.</param>
    /// <param name="maxSliceInstructions">Upper bound on one slice. It sets how often peripherals are
    /// ticked (a PIO program or a PWM counter only advances at slice boundaries), so lower it to
    /// interleave a circuit more finely and raise it for throughput.</param>
    /// <returns>The cycles that elapsed (also left in <see cref="LastElapsedCycles"/>). With Core 1
    /// launched, each slice counts as the longer of the two cores, as in <see cref="Run"/>.</returns>
    public long RunCycles(long cycleBudget, int maxSliceInstructions = 16_384)
    {
        if (cycleBudget <= 0)
        {
            LastElapsedCycles = 0;
            return 0;
        }

        var cap = Math.Max(1, maxSliceInstructions);
        long elapsed = 0;
        while (elapsed < cycleBudget)
        {
            var remaining = cycleBudget - elapsed;
            var slice = (int)Math.Clamp((long)(remaining / (_cyclesPerInstruction * 1.15)), 1, cap);
            Run(slice);
            var spent = LastElapsedCycles;
            if (spent <= 0)
            {
                // Both cores are stopped (locked up): time still passes, so burn the rest of the budget
                // instead of spinning on a clock that never moves.
                Cpu.Cycles += remaining;
                Sweep(remaining, remaining);
                spent = remaining;
            }
            else
            {
                _cyclesPerInstruction = Math.Clamp(0.5 * _cyclesPerInstruction + 0.5 * spent / slice, 1.0, 16.0);
            }
            elapsed += spent;
        }

        LastElapsedCycles = elapsed;
        return elapsed;
    }

    /// <summary>
    /// Reset both cores like <see cref="Reset"/>, then start Core 0 where <see cref="LoadFlash"/> does:
    /// at the start of flash, with SP taken from the firmware's vector table, bypassing the BootROM
    /// reset handler. For a host that loaded an image and wants to run it again from the top.
    /// </summary>
    /// <param name="imageLength">Length of the image in flash, to bound the vector table search.</param>
    internal unsafe void ResetToFlash(int imageLength)
    {
        Reset();
        if (TryFindVectorTable(Bus.PtrFlash, imageLength, out var sp, out _, out _))
            Cpu.Registers.SP = sp;
        Cpu.Registers.PC = BusInterconnect.FLASH_START_ADDRESS;
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
        var core0Delta = delta;
        if (_core1Launched)
        {
            var before1 = Cpu1.Cycles;
            _core1TurnBase0 = Cpu.Cycles - delta;
            _core1TurnBase1 = before1;
            _activeCoreId = 1;
            Cpu1.Run(instructions);
            _activeCoreId = 0;
            delta = Math.Max(delta, (int)(Cpu1.Cycles - before1));
        }
        LastElapsedCycles = delta;
        Sweep(delta, core0Delta);
    }

    /// <summary>Number of system resets (watchdog reboots) performed.</summary>
    public int ResetCount { get; private set; }

    /// <summary>
    /// Full system reset as raised by the watchdog (timer expiry, <c>CTRL.TRIGGER</c>, <c>watchdog_reboot()</c>,
    /// <c>rp2040.reboot()</c>): both cores restart from the BootROM vector with clean core state; flash, SRAM and
    /// the watchdog SCRATCH/REASON registers are preserved (firmware reads them to learn why it rebooted).
    /// Invoked from the watchdog Tick, i.e. between core <see cref="Run"/> batches, so touching CPU state is safe.
    /// </summary>
    public void SystemReset()
    {
        ResetCount++;
        Cpu.Registers = default;
        Cpu1.Registers = default;
        Reset();
        Ppb.Reset();
        Ppb1.Reset();
        Usb.Reset();
        RebaseEngines();
    }

    /// <summary>
    /// <c>AIRCR.SYSRESETREQ</c> (<c>NVIC_SystemReset()</c>) on Core 0. Datasheet 2.4.2.9: the NVIC resets the
    /// Cortex-M0+ core only, not the debug block, the power-on state machine or the peripherals, so only this core
    /// restarts, from the bootrom vector, while SRAM, peripherals and Core 1 keep their state. The cycle counter
    /// is not rewound: the write happens inside <see cref="CortexM0Plus.Run"/>, whose caller measures elapsed time
    /// from it, and time does not go backwards on a core reset.
    /// </summary>
    private void ResetCore0FromAircr()
    {
        ResetCount++;
        var cycles = Cpu.Cycles;
        Cpu.Registers = default;
        Cpu.Reset();
        Cpu.Cycles = cycles;
        Ppb.Reset();
    }

    /// <summary><c>AIRCR.SYSRESETREQ</c> on Core 1: it falls back to the bootrom's wait-for-launch state, like a PSM reset.</summary>
    private void ResetCore1FromAircr()
    {
        ResetCount++;
        var cycles = Cpu1.Cycles;
        _core1Launched = false;
        Sio.ResetMulticoreLaunch();
        Cpu1.Registers = default;
        Cpu1.Reset();
        Cpu1.Cycles = cycles;
        Ppb1.Reset();
        Sio.SignalCore1BootromReady();
        // Park it: the batch in flight must not run bootrom code the emulator does not execute for Core 1.
        Cpu1.Registers.EventRegistered = false;
        Cpu1.Registers.Waiting = true;
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
    private sealed class PerCorePpbRouter : IMemoryMappedDevice, IAddressDecoder, ITickable
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

        // The private peripheral bus is 0xE0000000-0xE00FFFFF; the rest of the region is not mapped (ARMv6-M B3.1)
        public uint DecodeLimit => 0x00100000;
        public bool[]? SlotPresent => null;
        public int SlotShift => 0;

        public uint   ReadWord(uint address)              => Active.ReadWord(address);
        public ushort ReadHalfWord(uint address)          => Active.ReadHalfWord(address);
        public byte   ReadByte(uint address)              => Active.ReadByte(address);
        public void   WriteWord(uint address, uint value) { Active.WriteWord(address, value); OnWrite?.Invoke(); }
        public void   WriteHalfWord(uint address, ushort value) { Active.WriteHalfWord(address, value); OnWrite?.Invoke(); }
        public void   WriteByte(uint address, byte value) { Active.WriteByte(address, value); OnWrite?.Invoke(); }

        /// <summary>Raised after every PPB write (a SysTick write may move its next exception).</summary>
        public Action? OnWrite;

        /// <summary>A lower bound on the swept cycles until either SysTick raises its exception: each core runs at
        /// most a sweep's cycles per sweep. Core 1's counts only while it runs.</summary>
        public long CyclesUntilEvent(bool core1Runs) =>
            Math.Min(_ppb0.CyclesUntilSysTick(), core1Runs ? _ppb1.CyclesUntilSysTick() : long.MaxValue);

        /// <summary>Tick both PPBs (SysTick, etc.) by the same delta cycles.</summary>
        public void Tick(long deltaCycles)
        {
            _ppb0.Tick(deltaCycles);
            _ppb1.Tick(deltaCycles);
        }
    }
}

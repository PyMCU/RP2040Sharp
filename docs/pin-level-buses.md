# Pin-level UART, SPI and I2C

By default the hardware UARTs, SPI and I2C blocks of the RP2040 exchange whole transactions with the host through
callbacks: UART TX bytes reach `OnByteTransmit` (the serial channels of the C ABI), SPI words go through
`OnTransfer`, I2C bytes through `OnWrite` / `OnRead`, and no pad moves. A circuit simulator that only sees pads (the
Proteus model of the Silicon Twin demo) then gets bit-banged buses and PIO, not `machine.SPI`, `machine.I2C` or
`machine.UART`.

With pin-level buses the blocks drive and sample the pads themselves, once firmware routes their function to a pad
(FUNCSEL 1 SPI, 2 UART, 3 I2C):

| Block | Pads (role from the pin, RP2040 datasheet 2.19.2) | What it does |
|---|---|---|
| UART0/1 | TX, RX (CTS/RTS are not modelled) | PL011 frames at the programmed baud: start bit, 5-8 data bits LSB first, no / odd / even / stick parity, 1 or 2 stop bits; BRK holds TX low; LBE loops TX back to RX internally. RX confirms the start bit at its middle and samples every bit at its middle; framing, parity and break errors land in UARTDR / UARTRSR (and FERIS / PERIS / BERIS / OERIS). 32-deep TX FIFO with TXRIS at the TXIFLSEL level, BUSY, TXFE / TXFF; `OnByteTransmit` still fires at write time |
| SPI0/1 | SCK, TX (MOSI), RX (MISO), CSn | PL022 master, Motorola format, SPO / SPH (all four modes), DSS 4-16 bits, SCK period CPSDVSR x (1 + SCR) clk_peri cycles. SPH = 0 samples MISO on the leading edge and shifts on the trailing one, SPH = 1 the other way round. The hardware CSn is low for the frame (and across back-to-back words with SPH = 1); MicroPython drives its own CS as a GPIO. Real 8-deep FIFOs, BSY, RXRIS / TXRIS at half full, RORRIS; DREQs follow the FIFOs |
| I2C0/1 | SDA, SCL | DW_apb_i2c master: START, repeated START, STOP, 7-bit address, data, ACK / NACK, open-drain (output enable on = pulled low, off = released to the pull-up), SCL high / low from IC_FS/SS_SCL_HCNT / LCNT (+1, with the DW minimums), SDA hold from IC_SDA_HOLD, clock stretching (SCL is waited for), arbitration loss. An address or data NACK raises TX_ABRT with the source bit and issues the STOP; START_DET / STOP_DET / ACTIVITY, live TX_EMPTY (TX_EMPTY_CTRL), RX_FULL against IC_RX_TL, 16-deep command FIFO |

A block whose pads firmware never routes keeps its transaction behaviour, so an image that uses the UART only as a
REPL over the serial channel is unaffected. Slave modes (SPI slave, I2C slave) stay at transaction level.

The engines are those of RP2350Sharp (`src/RP2040Sharp/Peripherals/PinLevel`), event-driven: every edge sits on its
own cycle.

## Enabling

| Where | How |
|---|---|
| Library | `new RP2040Machine(pinLevelBuses: PinLevelBus.All)` (or `I2c`, `Spi`, `Uart`); `None` (the default) leaves the machine exactly as before |
| C ABI | `st_rp2040_create_ex(flash, ST_RP2040_BUS_I2C \| ST_RP2040_BUS_SPI \| ST_RP2040_BUS_UART)` or `ST_RP2040_BUS_ALL`; `st_rp2040_create` is `create_ex(flash, ST_RP2040_BUS_ALL)`, as on RP2350 |

The flag values are RP2350's `ST_BUS_*` (1, 2, 4, 7), and the Pico W board flag `ST_RP2040_CYW43` moved to 0x100, the
value of `ST_RP2350_CYW43`, so one host passes the same bits to both chips. `create_ex(flash, 0)` keeps all three
blocks at transaction level.

## Time

The engines run on core 0's cycle counter (`RP2040Machine.EngineClock`): every register access, DMA DREQ query and
pad input first brings the block's engine up to the cycle of that access (core 1's accesses map to core 0's clock
as its pad writes do). A word written to SSPDR starts its frame at the write's cycle; a status poll sees the frame as
it is at the poll's cycle. What firmware reads and the edges it starts therefore do not depend on how the run is
sliced. In the peripheral sweeps the blocks are deferred like the other peripherals ([peripheral-deadlines.md](
peripheral-deadlines.md)): `CyclesUntilEvent` is the engine's next step, and a sweep ticks a block only when that
step is due.

The dividers count clk_peri (UART, SPI) and clk_sys (I2C) as firmware programmed them (`ClocksPeripheral.ClkPeriHz`,
`ClkSysHz`, from the PLL registers), while the core runs a fixed 125 MHz: the engines scale the counts to core
cycles, so a bus runs at the rate firmware asked for. MicroPython v1.28.0 runs clk_peri from the USB PLL at 48 MHz:
`SPI(baudrate=1000000)` is CPSDVSR 2, SCR 23, a 125-cycle SCK period (62.5 cycles a half, rounded to 63); UART
115200 is IBRD 26, FBRD 3, 1085.3 cycles a bit; `I2C(freq=100000)` is HCNT 500, LCNT 750, a 1252-cycle SCL period.
Until firmware switches clk_sys to its aux source (`clocks_init`), the counts are core cycles.

## Pad events and the event-bounded guard

An engine's pad change is reported (pin callback, `IoBank0.PadChanged`) with the cycle of the edge itself
(`IoBank0Peripheral.LastPadEdgeCycle`, valid while `FunctionDriving` is set), whichever access or sweep brought the
engine there.

The host answers a pad edge only after the call that reported it returns, so a call must not run from a watched
pad's edge into the sample of an input the circuit is answering on (an SPI MISO edge, an I2C ACK, a UART echo). With
pin-level buses, a watch and a guard above 1, `st_rp2040_run_until_edge` arms the **event-bounded guard**
(`Chip.EventGuard`): every slice of core 0 is a *cut* run (`RP2040Machine.RunCut`, `CortexM0Plus.RunCut`) that

- stops before the first instruction that would start at or after the next step a pin-level engine with work makes
  on a watched pad (`RP2040Machine.CyclesUntilPadEdge`), followed as accesses move it;
- stops right after an instruction that moved a watched pad (a SIO write, an engine edge an access brought on);
- sleeps (WFI / WFE) only up to that cycle;

and core 1 then runs as many instructions as core 0 did. The call returns at the instruction boundary guard 1 would
have returned at after each such edge, whatever the guard, so a lock-step host answers on time. The guard still
bounds the rest: core 1's turn, interrupts raised in the sweeps (TIMER, PWM...) and the sleep steps (asleep, guard 1
runs slices of two slots, so a wake-up can be seen a cycle later than with a coarse guard whose sleeping slice ends on
the edge). The cut check is a separate instantiation of the interpreter loop (`CutRunLimit`, `SliceCutRunLimit`);
`Run` and the bounded runs without a watch are unchanged.

`st_rp2040_cycles_until_edge(chip, watch)` returns that same next step (`UINT64_MAX` when no engine on a watched pad
has one coming), which the Proteus host's EdgeWait uses to wait for the circuit no longer than until the chip's next
sample.

Before a host input is applied (`st_rp2040_pin_set_input`) every deferred peripheral and engine is brought up to the
chip's time, so the cycles already run see the old level, never the new one retroactively.

## Cost

`CoSimBench` (`RP2040_NATIVE_PERF=1`, JIT, best of three, host seconds per simulated second, the old and the new
tree alternated on a shared host): without bus flags nothing changed (busy loop at guard 75 0.41-0.44 against 0.42,
idle MicroPython REPL at guard 300 0.0090 against 0.0088, `Machine.Run` 176-181 MIPS against 177). With
`ST_RP2040_BUS_ALL` and nothing routed, the fast paths cost 8-20 % more (four more deferred blocks in every sweep,
cut slices): busy loop 0.45 at guard 75 and 300, idle REPL 0.0096 at guard 75. The eager reference (every block ticked
every sweep) is about 25 % slower with buses on.

## Tests

`tests/RP2040Sharp.Native.Tests`, through the C ABI:

- `PinLevelDifferentialTests` (75): hand-assembled firmware (`PinLevelFirmware`) for SPI (modes 0-3, DSS 4 / 8 / 12 /
  16, polled, DMA on both directions and an SPI0_IRQ handler under WFI), UART (8N1, 7E2, 5O1, 6 bits stick-0
  parity) and I2C (writes, reads with RESTART, an absent address with TX_ABRT) against a lock-step host
  (`BusHost`: MOSI wired to MISO, TX to RX, a bit-level I2C slave), at plain `run` and guards 1, 4, 75, 300: the
  eager, deferred and fast chips give the same call returns, pad events with cycles, host inputs with cycles, SRAM,
  both cores and the SPI / UART / I2C / DMA registers; at every guard the data received is what was sent.
- `PinLevelLockstepTests` (53): guard 1 receives what was sent; guards 75 and 300 give the guard-1 exchange (pad
  events and cycles, host inputs and cycles, data) — the interrupt-driven SPI within the one-cycle wake-up noted
  above; without the event guard a coarse guard answers late and the data is wrong; `cycles_until_edge`; a MISO level
  driven between calls is not sampled retroactively (all three modes, `run` and `run_until_edge`).
- `MicroPythonBusTests` (6): MicroPython v1.28.0 RPI_PICO at guard 75 with the bus pads watched: `machine.SPI`
  write_readinto loopback in all four modes (5 bytes polled, 40 bytes by DMA, SCK half period 63 cycles),
  `machine.I2C` scan / readfrom_mem / writeto_mem / an absent address (OSError 5) against the bit-level slave (SCL
  99.84 kHz), `machine.UART` loopback at 115200 (frames back to back, 10852 cycles apart).

# Deferred ticks, sleeping stretches and bounded runs

`st_rp2040_run_until_edge` runs the chip in slices of `guard` instructions (twice that while both cores
sleep, at most `PeripheralQuantum` = 512), and after every slice it ticks the time-aware peripherals.
At a fine guard that sweep, not the firmware, is the cost: the idle MicroPython REPL at guard 1 is
two instruction slots and a sweep of twelve peripherals per slice. Three techniques, ported from
RP2350Sharp, take those slices several at a time. **Each gives exactly the result of the plain
slices**, which stay the reference: the differential tests below compare call returns, every pad
event and its cycle, every serial chunk and the cycle it arrived at, both cores' registers and cycle
counts, retired counts, SRAM and the peripheral registers. The core is register-accurate; none of
this changes what a slice does, only how many are run one by one.

| Switch | Where | Default |
|---|---|---|
| `RP2040Machine.DeferPeripheralTicks` | library | off (every peripheral every sweep) |
| `RP2040McuOptions.DeferPeripheralTicks` | `RP2040Mcu` (SiliconTwin.Abstractions adapter) | on |
| `Chip.DeferPeripheralTicks` | native chip | on |
| `Chip.SleepingStretches` | native chip | on (needs deferred ticks) |
| `Chip.AwakeBoundedRuns` | native chip | on (needs deferred ticks) |

## Deferred peripheral ticks

`DeferredTick` (`src/RP2040Sharp/Peripherals/DeferredTick.cs`) keeps, for each tickable, how far it
has been ticked (`Applied`) against the machine's swept total (`SweptCycles`, the sum of every sweep's
cycles). A sweep only adds its cycles; a peripheral is ticked, with everything it owes, at the first
sweep that reaches its next event (`IDeferrableTickable.CyclesUntilEvent`), at its own place in the
sweep order. A tick of the sum equals the ticks one by one as long as no event falls in between, so
the event sweep sees the state a tick per sweep would have left.

Before anything reads or changes a deferred peripheral it is brought to where the plain sweeps would
have it (`Sync`, then `Touch` asks for its next event again):

- every bus access through the APB/AHB bridges (`RegisterSchedule`), CPU or DMA, narrow writes and
  atomic aliases included; during a sweep a peripheral whose turn has not come yet is synced only to
  the previous sweep (`SweptFor`), exactly as the plain sweep has it;
- host-side calls: USB (`SendSetupPacket`, `EndpointReadDone`, the bus reset/resume/SOF signals,
  `Reset`, DPRAM writes), `Pwm.SetBInput` (the gated/edge DIVMODE input), `Pwm.GetChannelOutput` and
  the inspection getters, `Timer.GetCounter`, `Rtc.SetDateTime`/`GetSnapshot`, `Pio.InjectRxData`;
- `SyncPeripherals()` for anything else.

| Peripheral | Next event |
|---|---|
| TIMER | the nearest armed alarm (one already due: the next microsecond); a sync charges the counter from the core-0 cycle of the last sweep (`SweepClock`), as the per-sweep tick did, so `TIMERAWL` reads are unchanged |
| PWM | the first counter step, over the enabled free-running or gated-high slices, that wraps, turns at TOP or changes a channel output (8.4 dividers with the fraction carried) |
| PIO | the next divider edge of an enabled state machine; the next sweep if its interrupt lines are about to change or the IRQ latch is behind the flags. A put-off tick still runs its heartbeat (`SkipTick` = `EndTick`) |
| RTC, watchdog, ADC | the next second, the countdown reaching zero, the next START_MANY conversion |
| USB | a SETUP to raise or a SOF bit to clear (next sweep), the next SOF, the next endpoint completion |
| DMA, SPI | the next sweep while a transfer is paused on the beat budget / the TX FIFO holds words |
| Pin-level SPI, UART, I2C | the engine's next step ([pin-level-buses.md](pin-level-buses.md)): a timed wait inside a frame, a queued word / byte / command to take, a stretched SCL seen high, an RX start bit. The engines run on core 0's clock and are brought up to the cycle of every access, DREQ query and pad input, so a tick only catches them up; the UART and I2C blocks are in the sweep only with their bus enabled |
| SysTick (PPB) | stays eager (it counts from the cores' own cycle counters); its next exception bounds stretches and bounded runs |

## The managed adapter (`RP2040Mcu`)

`RP2040Mcu` (the `IMcu` / `IPinBank` adapter a fixed-step host such as iCircuit drives with `Run(750)` per step) builds
its machine with deferred ticks on (`new RP2040Mcu()`, `RP2040McuOptions`; `RP2040Mcu(machine)` keeps the machine's
own setting). The results are those of the eager machine (`RP2040McuLockstepTests`):

- `IPinBank.SetInputs` / `ReleaseInputs` bring every deferred peripheral up first (`RP2040Machine.SyncForHostInput`),
  so the cycles already run see the old level; inside a run (a `PinChanged` handler answering an edge) that is the
  sweeps so far, which is what the eager machine has ticked by then. Pads already held at the requested level are
  skipped without a sync (a circuit solver re-applies its inputs many times per step).
- `Run` ends with nothing owed, and `OutputLevels` / `OutputEnableMask` sync before they read.
- `PinChanged` stamps each change with its own cycle: the instruction for a SIO / IO_BANK0 / PADS write (core 1's
  mapped onto core 0's clock, `RP2040Machine.EngineClock`), the edge cycle for the pin-level UART / SPI / I2C engines,
  the end of the slice for other peripherals (PIO, DMA into SIO). PWM pads are reported too
  (`RP2040Machine.ExactPwmPadEdges`, on while a handler is attached): a counter edge at the cycle of the step that made
  it (the divider's fractional phase included; `SweepTimeline` maps the sweep's cycles onto core 0's), a PWM register
  write at the instruction. The counter advances at the sweeps, as before, so a write and the counter edges of the same
  slice can be delivered out of cycle order (the native chip sorts its events; a handler sees them as they happen).

## Sleeping stretches

While both cores are quietly asleep (WFI/WFE, no event registered, nothing pending and enabled; core 1
counts only once launched), a sleeping slice is a `Run` that charges its slots as sleep and a sweep
that only counts. `Chip.RunUntilEdge` books the slices before the first sweep with something due
(`RP2040Machine.SweptCyclesUntilEvent`) at once (`SkipSleepingSlices`): both cores charged, the swept
total moved, PIO's wake heartbeat run once (it is idempotent, and the stretch is refused when it would
change an NVIC pending bit: `PioPeripheral.HeartbeatInert`). The slice the event falls in, and the last
slice of the call, run normally. Retired counts are untouched (sleeping slices credit nothing).

## Bounded awake runs

With core 0 awake and core 1 unlaunched or quietly asleep, `RP2040Machine.RunAwakeBounded` runs core
0's slices one after the other in a single interpreter loop: `CortexM0Plus.RunGated`, the same
`RunGeneric` loop with a type-parameter limit (`IRunLimit`; `NoRunLimit` folds away, so `Run` is the
loop it was). At each slice end the gate (`ArmGate.NextSlice`) books the end without running it when
it changes nothing: core 1's turn is a quiet sleeper's (charged `n` slots), the sweep reaches no
event, no watched pad moved (`EndBoundedRun`, the PWM pads the host polls: `BoundaryCheck`), core 0
is still awake and the call goes on. Anything else ends the run with that slice, which the chip
finishes as a plain slice (`FinishSlice`: core 1's turn and the real sweep). Accesses inside the run
sync against the booked sweeps, and any access or tick bumps `ScheduleEpoch`, so the gate plans again
at the next slice end. Falling asleep charges the rest of the slice, as `Run` does.

## Differential tests

`tests/RP2040Sharp.Native.Tests`: `Differential.Check` drives the same host script on an Eager chip
(phase-1 behaviour), a Deferred one and a Fast one (all three techniques), and compares their whole
traces line by line.

- `SliceDifferentialTests` (80 cases + 1): hand-assembled firmware: TIMER-polled square wave, busy
  loop, WFI forever, TIMER alarm ISR with WFI and with a TIMERAWL polling loop, PWM wrap ISR (DIV 3,
  TOP 1499, CC += 7 per wrap, GP14 watched) with WFI and with a CTR polling loop, SysTick ISR every
  1000 cycles; plain `run` and guards 1, 4, 75, 300; 20 k and 1 M instruction calls; a host input
  every 7 calls. Identical in all 80.
- `MicroPythonDifferentialTests.Repl_pwm_and_gpio_are_the_same_in_every_mode` (3): MicroPython
  v1.28.0 RPI_PICO (SHA-256 `00aedb84...8152`) boots (plain runs), then at guards 1, 75 and 300 with
  every pad watched takes `1+1`, a 60-character print, `time.sleep_ms(3)`, a list comprehension,
  `PWM(Pin(14), freq=1000, duty_u16=20000)`, a duty change, 20 kHz, duty 0 and a GP15 toggle script
  with `sleep_us(200)`: identical transcript with arrival cycles, 445-457 pad events, everything else.
- `MicroPythonDifferentialTests.Pico_W_boot_and_wlan_are_the_same_in_every_mode` (1): the Pico W
  image with `ST_RP2040_CYW43`, boot and `WLAN(STA_IF).active(True)` at guard 75 (82 k calls).

The flash unique ID (`SsiPeripheral.UniqueId`) and the CYW43439 MAC are random or per-process per
machine; the harness pins them so two machines can be compared. The same comparison found
`RoscPeripheral.RANDOMBIT` shared by every machine in the process (now per machine): pico_rand seeds
from it, so the second Pico W run in a process brought WLAN up 2 cycles later than the first.

## Measurements

See the tables in [native-abi.md](native-abi.md#throughput).

## Not done

- **Exact cycles of peripheral-driven pad edges** (PIO, DMA into SIO): still the end of the
  slice in which they happened, as in phase 1. PWM counter edges carry their step's cycle when
  `RP2040Machine.ExactPwmPadEdges` is on (the managed adapter turns it on). (The pin-level UART / SPI / I2C engines report their own edge cycles.)
- **Pin-level UART/SPI/I2C**: done ([pin-level-buses.md](pin-level-buses.md)), with exact edge cycles and the
  event-bounded guard; `PinLevelDifferentialTests` (75 cases) compares them in the three modes.
- Core 1 awake: plain slices (bounded runs need core 1 unlaunched or asleep). Core 0 asleep with
  core 1 awake: plain slices.
- PIO with an enabled state machine at a small divider: its next event is every edge, so it is
  ticked every sweep and bounded runs end at every slice.

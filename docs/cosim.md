# The co-simulation core (`RP2040CoSim`)

A circuit simulator drives the RP2040 in steps and answers its pads in between (Proteus through the C ABI,
iCircuit through `RP2040Mcu`). Both go through one loop in the core library, `RP2040CoSim`
(`src/RP2040Sharp/Peripherals/RP2040CoSim.cs`), with its pad-event recorder and clock timeline. It mirrors
RP2350Sharp's `RP2350CoSim`: the request, result and flag names are the same, so the chip-free types can move to
a shared package later. The core is register-accurate: CPU instructions and peripheral registers behave as on
silicon; timing is modelled, not cycle-exact.

## The run

```csharp
var cosim = new RP2040CoSim(machine);                 // its PadEventRecorder: Drain delivery
cosim.Pads.Attach();
var r = cosim.RunUntilEdge(new RunRequest
{
    MaxSlots = 1_000_000,          // instruction slots per core (long.MaxValue: no limit)
    TargetCycle = long.MaxValue,   // core-0 cycle to stop at (long.MaxValue: none)
    Guard = 75, Watch = 1u << 15,  // slices of 75 while GP15 is watched
    Flags = CoSimFlags.Default,
});
// r.Slots, r.Cycles (wall-clock cycles elapsed: the longer core per slice), r.Reason
cosim.Pads.Drain(e => Console.WriteLine($"GP{e.Gpio}={e.Level} oe {e.OutputEnable} @{e.Cycle}"));
```

A run is a sequence of slices, each of the guard while a pad is watched and the guard is nonzero (twice it while
both cores sleep, `AdaptiveSleep`), else of `Quantum` (512) instructions per core, never more than the quantum.
After every slice the peripherals sweep. `Run(n)` is `RunUntilEdge` with `MaxSlots = n` and nothing watched: its
slices are the quantum counted from the start of the call, the last one shorter (the chunk boundaries depend only
on the calls, never on the firmware).

| Flag | What it does | Result |
|---|---|---|
| `SleepingStretches` | both cores quietly asleep: the sleeping slices before the next peripheral event are booked at once | that of the slices |
| `BoundedRuns` | core 0 awake, core 1 inert: core 0's slices in one interpreter loop, booked while their ends change nothing | that of the slices |
| `EventGuard` | pin-level buses, a watch and a guard above 1: core 0's slices end at the next engine edge on a watched pad and right after an instruction that moved one | guard 1's exchange at any guard ([pin-level-buses.md](pin-level-buses.md)) |
| `StopOnWatchedEdge` | return after the slice in which a watched pad changed | `run_until_edge` |
| `AdaptiveSleep` | watched slices are twice the guard while both cores sleep (a wake-up follows an event) | `run_until_edge` |

Stretches and bounded runs need deferred peripheral ticks (`RP2040Machine.DeferPeripheralTicks`); without them
the loop runs every slice plainly, which is the reference the differential tests compare against
([peripheral-deadlines.md](peripheral-deadlines.md)).

**Target cycle.** With `TargetCycle` set, every slice of core 0 is a cut run (`RP2040Machine.CutLimit`): it stops
before the first instruction that would start at or after the target and sleeps only up to it, so a run ends at
the first instruction boundary at or past the target (a sleeping core exactly on it). Core 1 then runs as many
instructions as core 0 did, and the peripherals sweep. Bounded runs end at the target too; sleeping stretches
book only the slices that end before it. Both cores locked up: time still passes to the target
(`RP2040Machine.ChargeStopped`; with core 0 locked up and core 1 running, core 1 is not run for that rest).

`Retired[core]` counts retired instructions conservatively (a slice is credited only to a core that was neither
asleep, locked up nor unlaunched at its start and end), as the C ABI's `st_rp2040_instructions`.

## Host inputs and reads

- `SetInputs(mask, levels)` / `ReleaseInputs(mask)`: pads already held at the level (or not held) are left alone;
  for the rest the deferred peripherals are brought up first (`RP2040Machine.SyncForHostInput`), so the cycles
  already run see the old level. Called from inside a run (a handler answering an edge from a sweep or a CPU
  access) that sync stops at the sweeps so far, which is what the eager machine has ticked by then; a peripheral
  advancing in its own tick does not re-enter it, so no cycle is ticked twice.
- `SyncToNow()` (`RP2040Machine.SyncForHostRead`): before the host reads peripheral state from outside; nothing
  from inside a sweep.
- `CyclesUntilPadEdge(watch)`: the next step of a pin-level engine with work on a watched pad.

## Pad events (`PadEventRecorder`)

Every change that goes through IO_BANK0 is recorded while it happens, with its own core-0 cycle (`Stamp`):

| Change made by | Cycle |
|---|---|
| a CPU write (SIO, IO_BANK0, PADS) | the instruction's; core 1's mapped onto core 0's clock from the start of its turn (`RP2040Machine.ExecCycle`) |
| a pin-level UART / SPI / I2C engine | the edge's (`IoBank0Peripheral.LastPadEdgeCycle`) |
| a PWM counter step (`RP2040Machine.ExactPwmPadEdges`) | the step's, the divider's fractional phase included; `ClockTimeline` maps the sweep's swept cycles onto core 0's |
| a PWM register write | the instruction's |
| anything else in a sweep (PIO, DMA into SIO), `Poll` | core 0's clock: the end of the slice |

Nothing is reported past core 0's own clock (core 1 may run more cycles in a slice): a stamp is clamped to it on
delivery. `Drain` hands the events over sorted by cycle (stable: they are in order but for core 1's, which follow
core 0's of the same slice); `Immediate` delivery hands each batch over while it happens, so a handler can answer
at once (the managed adapter). The counter of a PWM slice still advances at the sweeps, so in immediate mode a
register write and the counter edges of the same slice can arrive out of cycle order. `LevelsOnlyWhenDriven`
gives the SiliconTwin.Abstractions rule (an output register moving under an undriven pad is no change); the C ABI
counts every level and output-enable change.

## Who uses it

- The C ABI chip (`src/RP2040Sharp.Native/Exports.cs`, [native-abi.md](native-abi.md)) is a thin wrapper: its
  run calls, pin input and `cycles_until_edge` go to the co-simulation; the pin callback is its recorder drained
  after each call.

## AOT

Everything here is plain classes and closed struct instantiations (`CutRunLimit`, `SliceCutRunLimit` of the
interpreter loop): no generic virtual methods, nothing created by reflection, so it runs under NativeAOT and the
iOS no-JIT constraints.

## Differences from RP2350Sharp to reconcile

- `RunResult.Cycles` is the wall-clock cycles elapsed (the peripherals' swept cycles), not an absolute count.
- `SyncForHostInput` syncs inside a sweep as well (the RP2040 sweep cannot tick a cycle twice: a peripheral is
  marked applied before its tick runs), where RP2350 returns; inside a CPU run it syncs to the sweeps so far and
  does not cut the run.
- `ClockTimeline` maps swept cycles to core 0's (the RP2040 clock is fixed); RP2350 also follows clock changes.

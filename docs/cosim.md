# The co-simulation core (`RP2040CoSim`)

A circuit simulator drives the RP2040 in steps and answers its pads in between (Proteus through the C ABI,
iCircuit through `RP2040Mcu`). Both go through one loop in the core library, `RP2040CoSim`
(`src/RP2040Sharp/Peripherals/RP2040CoSim.cs`), with its pad-event recorder and clock timeline. It mirrors
RP2350Sharp's `RP2350CoSim`: the request, result and flag names are the same, so the chip-free types can move to
a shared package later. The core is register-accurate: CPU instructions and peripheral registers behave as on
silicon; timing is modelled, not cycle-exact.

## The run

```csharp
var cosim = new RP2040CoSim(machine);                 // using RP2040.CoSim; its PadEventRecorder reports deferred
cosim.Pads.Attach();
var r = cosim.RunUntilEdge(new RunRequest
{
    MaxSlots = 1_000_000,          // instruction slots per core (long.MaxValue: no limit)
    TargetCycle = long.MaxValue,   // core-0 cycle to stop at (long.MaxValue: none)
    Guard = 75, Watch = 1u << 15,  // slices of 75 while GP15 is watched
    Flags = CoSimFlags.Default,
});
// r.Slots, r.Cycles (wall-clock cycles elapsed: the longer core per slice), r.Reason
cosim.Pads.Drain((gpio, level, oe, cycle) => Console.WriteLine($"GP{gpio}={level} oe {oe} @{cycle}"));
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
| a PWM counter step (`RP2040Machine.ExactPwmPadEdges`) | the step's, the divider's fractional phase included; `SweepTimeline` maps the sweep's swept cycles onto core 0's |
| a PWM register write | the instruction's |
| anything else in a sweep (PIO, DMA into SIO), `Poll` | core 0's clock: the end of the slice |

Nothing is reported past core 0's own clock (core 1 may run more cycles in a slice) nor before the previous report
(`ReportedTo`): a stamp is clamped into that window on delivery. `PadReportMode.Deferred` keeps every change and
`Drain` hands them over sorted by cycle (stable: they are in order but for core 1's, which follow core 0's of the
same slice); `PadReportMode.Immediate` raises each batch through `Immediate` (a `PadChangeHandler`: the pads that
moved, output enable and driven levels of the `ImmediateWatch` pads, the cycle) while it happens, so a handler can
answer at once (the managed adapter). The counter of a PWM slice still advances at the sweeps, so in immediate mode a
register write and the counter edges of the same slice can arrive out of cycle order. `LevelsOnlyWhenDriven`
gives the SiliconTwin.Abstractions rule (an output register moving under an undriven pad is no change); the C ABI
counts every level and output-enable change.

## Who uses it

- The C ABI chip (`src/RP2040Sharp.Native/Exports.cs`, [native-abi.md](native-abi.md)) is a thin wrapper: its
  run calls, pin input and `cycles_until_edge` go to the co-simulation; the pin callback is its recorder drained
  after each call.
- `RP2040Mcu.Run(budget)` (the SiliconTwin.Abstractions adapter) is `RunUntilEdge` with
  `TargetCycle = now + budget`, `SleepingStretches | BoundedRuns`, no watch, and `MaxSliceInstructions` as the
  quantum; the pin bank's recorder in immediate mode raises `PinChanged`. The step ends at the first instruction
  boundary at or past the target (it used to be sized from the cycles per instruction seen so far, narrowing to
  single instructions at the tail), with nothing owed. `RP2040McuOptions.CoSim = false` (or `RP2040Mcu.CoSim`)
  keeps the old `RP2040Machine.RunCycles` path. `RP2040McuLockstepTests` checks both paths, eager against
  deferred.

| `RP2040Mcu` (warm JIT, Windows x64, host s per simulated s; parent tree -> this one, default options) | before | after |
|---|---:|---:|
| busy loop, `Run(750)` | 0.55 | 0.45 |
| MicroPython idle REPL, `Run(750)` | 0.11 | 0.063 |
| MicroPython idle REPL, `Run(125000)` | 0.032 | 0.0011 |
| busy loop, `Run(125000)` | 0.45 | 0.45 |

With one slice per step (`Run(750)`), the deferred machine is a little slower than the eager one through the new
path (idle 0.063 against 0.050): it ticks the same peripherals at the end of the step (`Run` leaves nothing owed)
after deciding per sweep which ones are due. Whole stretches are where deferral pays (`Run(125000)` idle: 0.0011
against 0.033 eager).

## AOT

Everything here is plain classes and closed struct instantiations (`CutRunLimit`, `SliceCutRunLimit` of the
interpreter loop): no generic virtual methods, nothing created by reflection, so it runs under NativeAOT and the
iOS no-JIT constraints.

## Differences from RP2350Sharp to reconcile

- `RunResult.Cycles` is the wall-clock cycles elapsed (the peripherals' swept cycles), not an absolute count.
- `SyncForHostInput` syncs inside a sweep as well (the RP2040 sweep cannot tick a cycle twice: a peripheral is
  marked applied before its tick runs), where RP2350 returns; inside a CPU run it syncs to the sweeps so far and
  does not cut the run.
- Namespace `RP2040.CoSim`, as RP2350's `RP2350.CoSim`. `ClockTimeline` is RP2350's (segments of constant clk_sys,
  `CycleToPs`, `PsIn`, `NoteWrite`, `Track`); on the RP2040 it keeps one segment (`RP2040CoSim.Clock`). The mapping of
  the peripherals' swept cycles onto core 0's clock is `SweepTimeline`, which RP2350 does not need (its peripherals
  run on core 0's clock).
- `PadEventRecorder` deferred mode keeps every change of a pad (the RP2040 C ABI reports each one); RP2350's keeps
  each pad's last change per report. RP2350's also hooks SIO, CTRL and PWM itself and stamps PIO by lockstep round
  (`PioRounds`); here IO_BANK0's `PadsChanged` carries all of them (PWM through `ExactPwmPadEdges`), and PIO edges
  carry the end of the slice. The extra members here: `Watch` / `WatchedChanged` / `WatchedEdge` (the run loop's),
  `LevelsOnlyWhenDriven`, `Poll`, `Record`, `Stamp`, `Pending`, `Baseline`.

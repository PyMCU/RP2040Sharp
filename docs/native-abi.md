# Native C ABI (`st_rp2040_*`)

`src/RP2040Sharp.Native` exposes the RP2040 to C and C++ hosts (the Proteus VSM model of the
Silicon Twin demo, first of all) through a flat C ABI, declared in
[`include/silicontwin_rp2040.h`](../src/RP2040Sharp.Native/include/silicontwin_rp2040.h). It
mirrors `silicontwin_rp2350.h` of RP2350Sharp wherever the RP2040 allows it, so one host can drive
a Pico, a Pico W and a Pico 2 with the same code. The core is register-accurate: CPU instructions
and peripheral registers behave as on silicon; timing is modelled, not cycle-exact.

## Packaging: an exports library, not a DLL of its own

Only one NativeAOT runtime can live in a process, so `RP2040Sharp.Native` is a plain class library
whose `[UnmanagedCallersOnly]` entry points become exports of the single NativeAOT library the host
ships. In the host's `.csproj`:

```xml
<ProjectReference Include="...\src\RP2040Sharp.Native\RP2040Sharp.Native.csproj" />
<UnmanagedEntryPointsAssembly Include="RP2040Sharp.Native" />
```

For local tests it can also be published alone (never shipped that way):

```bash
dotnet publish src/RP2040Sharp.Native -c Release -r win-x64 -p:StNativeLib=true -o out
# -> out/silicontwin_rp2040.dll (.so / .dylib), import library under bin/.../native/
```

`-p:StTestHooks=true` adds `st_rp2040_test_inject_fault` for the fault-isolation tests.

## The ABI

| Function | Notes |
|---|---|
| `st_rp2040_version`, `st_rp2040_clock_hz` | `"silicontwin-rp2040 0.1.0"`, 125 MHz |
| `st_rp2040_create(flash)`, `st_rp2040_create_ex(flash, flags)` | `flash` 0 = 2 MiB, else a power of two in 4 KiB..16 MiB. `ST_RP2040_BUS_I2C` (1), `ST_RP2040_BUS_SPI` (2), `ST_RP2040_BUS_UART` (4), `ST_RP2040_BUS_ALL` (7): pin-level buses ([pin-level-buses.md](pin-level-buses.md)); `st_rp2040_create` is `create_ex(flash, ST_RP2040_BUS_ALL)`, as on RP2350. `ST_RP2040_CYW43` (0x100, the value of `ST_RP2350_CYW43`; it was 1) attaches the CYW43439 of the Pico W (WL_ON GP23, DATA GP24, CS GP25, CLK GP29). The Pico W images of MicroPython and CircuitPython need it to boot |
| `st_rp2040_destroy`, `st_rp2040_status`, `st_rp2040_last_error` | Opaque handles, a sticky FAULTED state, no exception ever crosses an export (same model as RP2350) |
| `st_rp2040_load_uf2` | RP2040 family `0xE48BFF56` (or no family). Any other family, e.g. an RP2350 image, rejects the whole file with `-1` and a message naming the family. Absolute/data family blocks are skipped. Core 0 starts at `0x10000000` as after the bootrom |
| `st_rp2040_run(n)` | `n` instructions per core, peripherals ticked every 512 at most |
| `st_rp2040_run_until_edge(max, guard, watch)` | Same semantics as RP2350: slices of `guard` (twice that while both cores sleep) whenever `watch` and `guard` are nonzero, including before firmware routes the pads; stops after the slice in which a watched pad's level or OE changed. The result is that of peripherals ticked at the end of every slice; deferred ticks, sleeping stretches and bounded awake runs take several slices at once with exactly that result ([peripheral-deadlines.md](peripheral-deadlines.md)). With pin-level buses and a guard above 1, the event-bounded guard ends a slice of core 0 at the next engine edge on a watched pad and right after an instruction that moved one ([pin-level-buses.md](pin-level-buses.md#pad-events-and-the-event-bounded-guard)) |
| `st_rp2040_cycles_until_edge` | Cycles until the next step of a pin-level engine with work on a watched pad, `UINT64_MAX` when none has one coming (as on RP2350: the Proteus host's EdgeWait) |
| `st_rp2040_cycles`, `st_rp2040_time_ps`, `st_rp2040_cycle_to_ps`, `st_rp2040_clk_sys_hz` | Core-0 cycles at a fixed 125 MHz (the core does not model clock changes): 8000 ps per cycle |
| `st_rp2040_instructions(core)` | Conservative retired count, as on RP2350 |
| `st_rp2040_pin_get`, `st_rp2040_pin_get_pull`, `st_rp2040_pin_set_input` | `-1` releases a pad: it reads its pull and follows later pull changes. `pin_set_input` first applies the peripheral time still pending, so no tick samples the new level retroactively; a pad already held at the level (or already released) is left alone, without that sync, as on RP2350 |
| `st_rp2040_adc_set_voltage` | Channels 0..3 (GP26..29); the temperature sensor reads 27 °C |
| `st_rp2040_set_pin_callback`, `st_rp2040_set_serial_callback`, `st_rp2040_serial_write` | Channels UART0, UART1 and USB CDC: an emulated USB host (`UsbCdcHost`) enumerates the device and asserts DTR, so the MicroPython REPL works over CDC |

Type names carry the chip (`st_rp2040_pin_cb`, `ST_RP2040_STATUS_OK`...) so that a translation unit
can include both headers; values and callback signatures are those of the RP2350 header.

### Pad-change cycles

Every pad change that goes through IO_BANK0 is recorded while it happens, not found by a scan at
the end of the slice. A change made by a CPU instruction (a SIO, IO_BANK0 or PADS write, on either
core) carries the cycle of that instruction, so the cycles a host sees, and pulse widths, are the
same at every guard: the tests check a timer-timed 37 µs pulse and a square wave at guards 1, 75
and 300. Changes made by the pin-level UART / SPI / I2C engines carry the cycle of the edge (the
engines run on core 0's clock). A PWM counter edge carries the cycle of the counter step that made it
(the divider's fractional phase included; `RP2040Machine.ExactPwmPadEdges`, on in the chip), a PWM register
write the cycle of the instruction. Changes made by other peripherals (PIO, DMA into SIO) carry the end of
the slice in which they happened. Changes are reported in cycle order (`PadEventRecorder`, stable), core 1's
never past core 0's clock.

The PWM counter itself still advances at the end of each slice (as every peripheral does), so it starts
counting from the start of the slice in which firmware enabled it: the period and every later edge are
exact, the phase of the first one follows where that slice began.

## Inside: the co-simulation core

The chip is a thin C face over `RP2040CoSim` (in the core library, [cosim.md](cosim.md)), the loop that the
managed adapter `RP2040Mcu` runs too: `st_rp2040_run(n)` is `Run(n)` (slices of the 512-instruction quantum from
the call's start), `st_rp2040_run_until_edge` is `RunUntilEdge` with the guard, the watch and
`CoSimFlags.Default` (event guard, sleeping stretches, bounded runs, stop on a watched edge, twice the guard
asleep); pad events come from its `PadEventRecorder` in deferred mode, `st_rp2040_pin_set_input` goes through
`RP2040CoSim.SetInputs` / `ReleaseInputs`.

## Missing compared with RP2350

- **Exact peripheral-driven edges** for PIO and DMA: their pad changes carry the end of their slice (the
  pin-level UART / SPI / I2C engines' and the PWM counter's carry their own cycle).
- **Pin-level slaves and flow control**: SPI and I2C slave modes stay at transaction level; UART CTS /
  RTS are not modelled; the UART RX interrupt is raised per byte (no RX trigger level or receive
  timeout), as for bytes from the serial channel.
- **Clock changes.** clk_sys is fixed at 125 MHz; `machine.freq()` changes nothing. (The pin-level
  engines scale their dividers by the clocks firmware programmed, so buses run at the requested rate.)
- SRAM-only UF2 images (`no_flash` binaries) are not loaded.

## Throughput

`run_until_edge(1000000, guard, all 30 pads)`, Windows x64, host seconds per simulated second (lower is
better), best of 3. "Before" is the phase-1 tree (every peripheral ticked after every slice, every slice
run on its own); "after" adds deferred ticks, sleeping stretches and bounded awake runs
([peripheral-deadlines.md](peripheral-deadlines.md)), with the same results. JIT, `CoSimBench`
(`RP2040_NATIVE_PERF=1`); idle and PWM are MicroPython v1.28.0 RPI_PICO after the REPL is up (PWM:
`PWM(Pin(14), freq=1000, duty_u16=20000)`), 200 ms simulated; busy is a hand-assembled core-0 loop, 50 ms.

| Workload | guard 1 before / after | guard 75 before / after | guard 300 before / after |
|---|---:|---:|---:|
| Idle REPL | 7.02 / 0.057 | 0.189 / 0.025 | 0.032 / 0.025 |
| Idle REPL + 1 kHz PWM | 8.35 / 0.067 | 1.39 / 0.034 | 0.183 / 0.016 |
| Busy loop | 9.38 / 0.82 | 0.64 / 0.58 | 0.51 / 0.53 |

The two columns were measured an hour apart on a host shared with other work; `CoSimBench` now also
alternates both modes on the same chip within one run, which gives the same picture under load (idle
guard 1: 15.3 / 0.044, PWM guard 1: 17.6 / 0.14, busy guard 1: 18.9 / 1.65, busy guard 300: 0.69 /
0.70). The busy loop at coarse guards is the interpreter itself (about 100-170 MIPS depending on the
load); the plain `RP2040Machine.Run` loop is unchanged (old and new builds alternated: 95-112 and
92-116 MIPS).

With pin-level buses ([pin-level-buses.md](pin-level-buses.md#cost)) and nothing routed, the fast paths cost 8-20 %
more; without bus flags the figures above are unchanged.

NativeAOT (`abi_selftest.c`, idle REPL, single runs): guard 1 0.090x real time before, 18.4x after;
guard 75 5.3x before, 67x after.

## Tests

- `tests/RP2040Sharp.Native.Tests`: export-level tests through unmanaged function pointers (handles,
  rejected arguments, UF2 families, time, pulls, re-entrancy, destroy from a callback, injected
  faults with `-p:StTestHooks=true`), `run_until_edge` semantics and guard invariance on a
  hand-assembled firmware, MicroPython on the Pico (REPL over CDC, an `exec`'d script toggling GP15)
  and on the Pico W (`network.WLAN(network.STA_IF).active(True)` with `ST_RP2040_CYW43`).
  The Pico image is the one under `tests/RP2040Sharp.IntegrationTests/Firmware`; the Pico W image
  comes from `RP2040_MICROPYTHON_PICOW_UF2` or the integration tests' firmware cache, and the test
  skips without it.
- `SliceDifferentialTests`, `MicroPythonDifferentialTests`: the speed-ups against plain slices (see
  [peripheral-deadlines.md](peripheral-deadlines.md#differential-tests)); `CoSimBench`: the measurements above.
- `PinLevelDifferentialTests`, `PinLevelLockstepTests`, `MicroPythonBusTests`: the pin-level buses
  ([pin-level-buses.md](pin-level-buses.md#tests)).
- `tests/RP2040Sharp.Native.Tests/abi_selftest.c`: the same ABI from C against the NativeAOT library.

# Changelog

All notable changes to **RP2040Sharp** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.1] - 2026-10-23

### Fixed: RESETS did not reset I2C, SPI, UART, PWM, ADC or DMA, so CircuitPython could not re-create I2C after a reload

Only USBCTRL and PIO reacted to their RESETS bit. CircuitPython deinits `busio.I2C` with `reset_block` and refuses to
construct one while `IC_ENABLE` is still set, so after Ctrl-C + Ctrl-D (or any soft reload) `busio.I2C(...)` raised
`ValueError: I2C peripheral in use`. Asserting the RESETS bit of ADC, DMA, I2C0/1, PWM, SPI0/1 or UART0/1, through a
plain write or the SET/CLR/XOR aliases, now returns that block's registers, FIFOs and IRQ lines to their reset state.
TIMER, RTC, IO_BANK0 and PADS_BANK0 are left as they were. Tested by `ResetsBlockTests`.

### Fixed: registers that disagreed with the RP2040 SVD

- **SIO divider.** `DIV_CSR` had READY and DIRTY swapped: READY read 0 after every divide, so
  `hw_divider_wait_ready()` (the `hardware_divider` API, `pico_divider_compiler`) spun forever. READY is bit 0 and
  always set, DIRTY is bit 1, set by any write and cleared by reading `DIV_QUOTIENT`.

### Added: release gate

`publish.yml` runs the `Release` category of the integration tests before packing. Real MicroPython 1.28.0 and
CircuitPython 10.3.1 boot, a never-ending program's output arrives after a late port open, I2C, SPI, UART, PWM and ADC
are re-created after two reloads, `machine.reset()`, the watchdog and `microcontroller.reset()` reboot twice, and a
long-named module and a `lib/` package import. A tag, prerelease tags included, does not publish if the gate fails or
its firmware cannot be fetched. Pull requests do not run it.

## [1.2.0] - 2026-10-22

### Performance: scalar loads, stores and stack bursts stay call-free on the SRAM path

The spec-audit fault checks had made every load and store spill around the fault call, costing 8-15% of
throughput. SRAM and XIP accesses are inline again and faults, peripherals and unmapped space take out-of-line
paths, with the same faults as before. MicroPython CPU loop, NativeAOT, Switch decoder: 266-270 to 281-286 MIPS.

### Changed: "register-accurate" instead of "cycle-accurate" in the package description and README

CPU instructions and peripheral registers behave as on silicon; timing follows rp2040js's per-instruction cycle-cost
model and is not cycle-exact. `RP2040Sharp.Wireless` drops the `emulator` tag: the CYW43439 is not emulated, its gSPI
host protocol is translated to a virtual network.

### Fixed: `CircuitPythonFs.BuildVolume` truncated names to 8.3 and could not hold folders; it now bakes whole projects

`BuildVolume` wrote root-directory entries only, with each name cut to 8.3 and no long-name entries, so a module such as
`adafruit_pioasm.py` landed on CIRCUITPY as `ADAFRUIT.PY` and `import adafruit_pioasm` failed, two modules sharing their
first eight letters overwrote each other, and a `lib/<package>/__init__.py` project could not be baked at all.
`BuildVolume`, `Bake`, `ReadFile` and the new `ListFiles` and `BuildVolumeFromDirectory` now go through `FatVolume`:
names may be paths with `/` (folders are created on demand), long names get VFAT entries, single-case 8.3 names keep their
case (`code.py` stays `code.py`), empty files take no cluster, and the output is deterministic (timestamps 1980-01-01).
`FatVolume.Format(int sizeBytes)` creates an empty CIRCUITPY volume with CircuitPython's own geometry and
`FatVolume.ListFiles()` lists every file recursively. `BuildVolumeFromDirectory(sizeBytes, directory)` bakes a host
folder, skipping dot-files and `__pycache__`. **Breaking:** every bad input (invalid or duplicate path, a file and a
folder with the same name, a volume that is full or has no room left in the root) now throws `FatException` instead of
`ArgumentException` or `InvalidOperationException`. Tested by `CircuitPythonFsTests` (a realistic project with a 70 KB
file and a `.mpy`, 8.3-prefix collisions, LFN checksums and 13/26-character names, deep nesting, a folder spanning more
than one cluster, the 512-entry root, overflow, path validation) and `CircuitPythonProjectTests`, which bakes a long-named
module and a nested package and boots CircuitPython 9.2.1 on the RP2040 until `ICIRCUIT pio 42` is printed.

### Added: the Pico W onboard LED (`Pin("LED")`) is modelled, and `st_rp2040_wifi_gpio` reports it

On the W boards the LED hangs off the CYW43439's WL_GPIO0, which the cyw43-driver sets with the `WLC_SET_VAR "gpioout"`
iovar (le32 mask, le32 value) and reads back with `WLC_GET_VAR "ccgpioin"`. Both were acked and dropped, so
`Pin("LED").on()` lit nothing and `.value()` read 0. `Sdpcm` now keeps the WL GPIO outputs (`WlGpioOut`, the bits under
the mask take the value's), answers `ccgpioin` from them, raises `OnWlGpioChanged` when they change and clears them when
WL_ON goes low (`PowerDown`, called by `Cyw43439Device`). New ABI function `uint32_t st_rp2040_wifi_gpio(st_rp2040 *chip)`:
the WL GPIO outputs, bit 0 = the LED, cheap enough for a host to read after every step; it rejects nothing and reads 0
on a chip without the CYW43439 or for a bad handle. No Wi-Fi event is raised for it (a blinking LED would flood the
event queue and the `changes` count). The CYW43439 is still not emulated: this is the gSPI protocol translation
answering two more iovars. Documented in `docs/native-abi.md`. Tested by `Cyw43WlGpioTests` (the ioctl decode, read-back, power-off), `PicoWLedTests`
(MicroPython v1.28.0 RPI_PICO_W: `led.on()` / `led.off()` read 1 then 0 through the export and through `led.value()`)
and `abi_selftest.c`.

### Fixed: a forwarded connection lost the guest's answer when the guest reset it after closing

A connection the guest had closed (FIN) and then reset was aborted at once, with a reset to the host socket, even when
the guest's last bytes, already acknowledged to it, were still queued for the writer task. That task runs on the thread
pool while the guest's clock runs on, and MicroPython resets a half-closed connection 10 simulated seconds after its FIN:
on a busy pool the browser got "connection forcibly closed" and no page. A reset after the guest's FIN now ends the
connection as the FIN did (the rest is written, then a FIN); a reset before it still aborts both sides. The same holds for
outbound bridges. Tested by `PicoWForwardHeldPoolTests`, which holds the thread pool while the guest answers.
### Added: `st_rp2040_set_flash_unique_id`, and a deterministic default unique id

The flash's `0x4B` (Read Unique ID) answer, which is what `machine.unique_id()` reads on the RP2040, was a random GUID per
chip: unique, but different on every run, so two simulations of the same firmware (the MicroPython USB serial number and
its random seed come from it) could not be compared. The default is now a hash of a per-process creation counter and the
flash size: unique per chip, and the same on every run for the same creation order. The host can set it:
`void st_rp2040_set_flash_unique_id(st_rp2040 *chip, const uint8_t id[8])` (NULL rejected with `last_error`, a dead handle
ignored), `SsiPeripheral.SetUniqueId` and the `flashUniqueId` argument of `RP2040Machine`; `SsiPeripheral.UniqueId` is
now a property that validates the length. The firmware reads the id once at start-up: set it before the first run.
Tested with MicroPython v1.28.0 on the Pico and the Pico W through the C ABI (`UniqueIdTests`: non-zero, different
between two chips, equal to what the setter gave), `SsiUniqueIdTests`, `abi_selftest.c`. The 0x9F (JEDEC) and SFDP
commands read 0 here as before; the RP2040 SDK does not issue them.

### Added: outbound TCP bridges and DNS names for the Wi-Fi (W3)

`st_rp2040_wifi_bridge_tcp(chip, gateway_port, host, port)` relays the guest's connections to `192.168.4.1:gateway_port`
to `host:port` on the PC's network, so a Pico W running MicroPython reaches an MQTT broker (or any TCP service) on
the PC; `port` 0 removes the bridge. `st_rp2040_wifi_dns_add(chip, name, ipv4)` gives the gateway's DNS a name (`ipv4`
NULL removes it); names ending in `.local`, which lwIP sends to mDNS, are answered there too. The guest reaches only
the `host:port` the host configured for each gateway port. Its SYN is answered once the host connection is up, and a
host that refuses, does not answer within 10 s of host time, or has a name the PC cannot resolve resets the guest's
`connect()` at once (`ST_RP2040_WIFI_EV_BRIDGE_FAILED`, `bridge_error`); to a loopback host no SYN is retried, so a closed port
on this PC is refused in milliseconds. Past the handshake a bridged connection uses the forwards' TCP (window,
retransmission, half-close, ACKs at the next pump) and is pumped with them by `st_rp2040_wifi_poll` and the run calls; a
chip without bridges pays the same null check as before. `st_rp2040_wifi_info` grew at its end from 160 to 208 bytes (`bridges`,
`bridge_open`, `bridge_connected`, `bridge_failed`, `bridge_to_host`, `bridge_to_guest`, the ports of the last bridge
event, `bridge_error`); new events `ST_RP2040_WIFI_EV_BRIDGE_OPEN` (10), `_CLOSE` (11), `_FAILED` (12). `VirtualNet.BridgeTcp`
now uses the same relay (it connected synchronously and did not retransmit), and the forwards' zero-window probes no
longer reset a guest that answers them. Tested with MicroPython v1.28.0 and umqtt.simple publishing to and receiving
from an MQTT broker on 127.0.0.1 (`PicoWBridgeTests`, `abi_selftest.c`).

### Fixed: the virtual USB host waits 20 SOFs after opening the port before it sends

`UsbCdcHost` delivered queued host data in the same millisecond as `SET_CONTROL_LINE_STATE` (DTR), which a real host
application does not do: it writes once its port is open. Data arriving inside the firmware's own post-DTR window can
starve TinyUSB's `tud_task` while SOF events fill its 16-entry event queue, and an overflow drops events (on the
RP2350's RISC-V cores it ends in TinyUSB's unconditional `ebreak`; that is upstream behaviour, not an emulator bug).
The host now holds host-to-device data for `UsbCdcHost.PortOpenFrames` (20) SOFs after the port opens
(`HoldingAfterPortOpen`); a bus reset clears the hold. `Cyw43MqttFleetTests` lets the window pass before its first
byte. `UsbCdcHostPortOpenTests` cover the hold.

### Fixed: emulated time no longer restarts at 0 after a watchdog reset or `st_rp2040_fs_commit(chip, 1)`

`RP2040Machine.SystemReset` (the watchdog: timer expiry, `CTRL.TRIGGER`, `watchdog_reboot()`, MicroPython's
`machine.reset()`, CircuitPython's `microcontroller.reset()`; and `st_rp2040_fs_commit(chip, 1)`) reset both cores
through `CortexM0Plus.Reset`, which zeroes the cycle counter. That counter is the machine's timebase, so
`st_rp2040_cycles` and `st_rp2040_time_ps` went back to 0, a pad change after the reboot carried a cycle from before
it, and `RP2040CoSim.RunUntilEdge` returned a negative cycle count for the call. The cores' counters now carry on
through the reset, as `AIRCR.SYSRESETREQ` already did; SysTick restarts with the PPB and the pin-level engines restart
at the current time. `StopReason.Reset` is no longer returned (a target still applies after a reset). Host-requested
resets (`RP2040Mcu.Reset`) keep their behaviour. Tests: `ResetTimelineTests` (native suite: monotonic time and pad
stamps across `fs_commit` with a reset, and across a watchdog reset and `SYSRESETREQ` stored by firmware).

### Added: files onto CIRCUITPY from the host (`st_rp2040_fs_status`, `st_rp2040_fs_put`, `st_rp2040_fs_commit`)

CircuitPython keeps CIRCUITPY read-only to its own code while USB is connected, so a host cannot copy a project onto
the board through the raw REPL. The core now does what a PC does when it copies files to the drive: `FatVolume`
(the same code as RP2350Sharp's) writes them into the FAT12/FAT16 volume in emulated flash, all or nothing, with long
file names, subdirectories and replaced files freeing their old clusters; `st_rp2040_fs_commit(chip, 1)` then reboots
the chip like a watchdog reset. The volume is found after the end of the UF2's RP2040 blocks, not at a fixed offset.
`st_rp2040_fs_status` fills a growable `st_rp2040_fs_info` (48 bytes, the layout of RP2350's `st_fs_status`) with the
volume, `ST_RP2040_FS_BOOT_OUT` and new flash erase/program counters (`SsiPeripheral.EraseCount`, `ProgramByteCount`,
counted for the SSI and the bootrom flash calls). .NET hosts use `CircuitPythonFs.PutFiles` and
`RP2040Machine.FlashMemory`. See `docs/native-abi.md`. Tests: `FatVolumeTests` (synthetic images),
`CircuitPyFilesTests` in the native suite (CircuitPython 9.2.1 for the Pico through the ABI) and `abi_selftest.c`.

### Fixed: the USB host waits the reset recovery time before SET_ADDRESS

`UsbCdcHost` wrote SET_ADDRESS into DPRAM from inside the firmware's write that clears `SIE_STATUS.BUS_RESET`
(SETUP_REC followed one tick later). Newer TinyUSB (CircuitPython 11 on the RP2350) drops a SETUP that arrives before
it has processed the reset. The host now keeps a real host's timing on simulated time: SET_ADDRESS 10 ms after the
reset ends (TRSTRCY, USB 2.0 §7.1.7.5 and §9.2.6.2), the next request 2 ms after the SET_ADDRESS status stage
(SetAddress recovery, §9.2.6.3). `UsbPeripheral.SendSetupPacketAfter` schedules the SETUP for `Tick` (it bounds
`CyclesUntilEvent`; a controller reset cancels it). The two waits add up to 12 ms of simulated time to enumeration.

### Fixed: an LDMIA abandoned by a bus fault keeps its original base

A load or store that cannot complete already took a precise HardFault (stacked PC = the instruction, no
destination or writeback). One case slipped through: `LDMIA rn, {..., rn, ...}` that loaded the base from an
earlier word and then faulted on a later one left the base holding the loaded word. The abandoned LDM now leaves
the base at its original value, like every register the fault does not touch.

### Fixed: CPSIE/CPSID are ignored in unprivileged Thread mode

ARMv6-M executes CPS only when `CurrentModeIsPrivileged()`. The core applied it from unprivileged code too, so a
task running with `CONTROL.nPRIV = 1` could mask or unmask interrupts. MRS and MSR already honoured privilege;
new tests pin that, and that MRS of every xPSR view reads EPSR (the T bit) as zero and IEPSR as IPSR.

### Added: Wi-Fi port forwarding into the guest (`st_rp2040_wifi_forward_tcp`, `st_rp2040_wifi_poll`)

A Pico W can serve the PC: `st_rp2040_wifi_forward_tcp(chip, 8080, 80)` listens on `127.0.0.1:8080` (loopback only)
and relays each connection to the station's port 80 through the virtual gateway's TCP (`TcpForwarder`, several
connections, MSS and window, retransmission in simulated time, FIN / half-close / RST both ways), so a browser
reaches a MicroPython socket or Microdot server. Socket I/O runs on background threads; the guest side only on the
caller's thread, in `st_rp2040_wifi_poll` or the run calls (at most once per millisecond of host time, nothing on a
chip without forwards). The status struct grows at its end to 160 bytes (connection counters and the last
connection's ports) and three events are new (`TCP_OPEN`, `TCP_CLOSE`, `TCP_REFUSED`). Destroy frees the ports.
The virtual network grants SDPCM bus credit 16 packets ahead instead of 4: a driver stalled for credit reads an
incoming frame into the buffer of the frame it is sending, which lost about one TCP segment in eight of a large
response. See [docs/wireless.md](docs/wireless.md).

### New: native C ABI (`st_rp2040_*`)

`src/RP2040Sharp.Native` exposes a Pico or Pico W (`ST_RP2040_CYW43`) to C/C++ hosts through
`include/silicontwin_rp2040.h`, mirroring the RP2350 ABI: handles with fault isolation, UF2 loading
with family check, `run` / `run_until_edge`, pads with exact pad-change cycles for CPU writes,
UART0/1 and USB CDC serial channels. It is an exports library for a host's single NativeAOT
library (`<UnmanagedEntryPointsAssembly Include="RP2040Sharp.Native" />`). See
[docs/native-abi.md](docs/native-abi.md). `IoBank0Peripheral.PwmMuxedPins` is new and public.

### New: co-simulation speed-ups with the result of plain slices

Ported from RP2350Sharp, each proven by differential tests to give exactly what guard-sized slices
give (pad events and their cycles, CDC transcript with arrival cycles, both cores, retired counts,
SRAM, peripheral registers, call returns). See [docs/peripheral-deadlines.md](docs/peripheral-deadlines.md).

- **Deferred peripheral ticks** (`RP2040Machine.DeferPeripheralTicks`, off by default in the
  library, on in the native chip): TIMER, PWM, both PIO blocks, RTC, watchdog, ADC, USB, DMA and
  SPI are ticked only at their next event (`IDeferrableTickable.CyclesUntilEvent`), and brought
  up to date before every bus access (APB/AHB bridges) and host call (`DeferredTick`).
- **Sleeping stretches**: while both cores sleep quietly the native `run_until_edge` books the
  sleeping slices before the next event at once (`RP2040Machine.SkipSleepingSlices`).
- **Bounded awake runs**: with core 0 awake and core 1 unlaunched or asleep, its slices run in one
  interpreter loop (`CortexM0Plus.RunGated`, a type-parameter limit; `Run` is unchanged) that
  stops only at a slice whose end matters.
- `PwmPeripheral` advances its counters in closed form (the same steps, without a loop per step).
- Fixed: `RoscPeripheral.RANDOMBIT` was a process-wide counter, so pico_rand's seed (and with it
  the Pico W WLAN bring-up) depended on every other machine in the process.

### New: the co-simulation core (`RP2040CoSim`)

The loop of `st_rp2040_run_until_edge` (slices, sleeping stretches, bounded runs, the event-bounded
guard, retired counts) moves from the native chip into the core library as `RP2040CoSim`, with
`RunRequest` / `RunResult` / `StopReason` / `CoSimFlags` (the names of RP2350Sharp's), a target
cycle (`RP2040Machine.CutLimit`: runs stop at the first instruction boundary at or past it),
`SetInputs` / `ReleaseInputs` (skip unchanged pads, sync the rest) and `SyncToNow`. Pad events go
through `PadEventRecorder` (drain: stable cycle order, clamped to core 0's clock; immediate) and
the chip-free types live in the `RP2040.CoSim` namespace with RP2350Sharp's names (`PadReportMode`,
`PadChangeHandler`, `ClockTimeline`; `SweepTimeline` maps swept cycles to core 0's). The native chip is a thin wrapper over it; it turns
`RP2040Machine.ExactPwmPadEdges` on, so PWM edges reach the pin callback at the cycle of their
counter step at every guard. See [docs/cosim.md](docs/cosim.md).

### Changed: `RP2040Mcu` (the SiliconTwin.Abstractions adapter) for fixed-step hosts

- `new RP2040Mcu()` turns deferred peripheral ticks on; `RP2040McuOptions` (flash size, bootrom,
  pin-level buses, `DeferPeripheralTicks`, `CoSim`) builds the machine.
- `Run(budget)` goes through `RP2040CoSim` with `TargetCycle = now + budget` (sleeping stretches,
  bounded runs; a step ends at the first instruction boundary at or past the target);
  `RP2040Mcu.CoSim = false` keeps `RP2040Machine.RunCycles`. MicroPython idle at `Run(125000)`
  steps: 0.032 -> 0.0011 host s per simulated s. Same results as the eager machine
  (`RP2040McuLockstepTests`: `Run(750)` per step, PWM, SPI loopback, MicroPython).
- `IPinBank.SetInputs` / `ReleaseInputs` sync the deferred peripherals first
  (`RP2040Machine.SyncForHostInput`) and skip pads already held at the level; `Run` ends with
  nothing owed; output reads sync first.
- `PinChanged` cycles are those of the change itself (instruction, core 1 mapped onto core 0's
  clock, pin-level engine edge) instead of core 0's clock at the report, and PWM pads are reported
  (`RP2040Machine.ExactPwmPadEdges`, `PwmPeripheral.OutputStep`, `SweepTimeline`): counter edges
  at the cycle of their step, register writes at the instruction.
- New public members: `IoBank0Peripheral.NotifyPadOutputsAt`, `ReportPadOutputs`,
  `ExternalHeldMask`, `ExternalLevels`.

### New: pin-level UART, SPI and I2C

The hardware UART0/1, SPI0/1 (master) and I2C0/1 (master) can drive and sample the GPIO pads
themselves once firmware routes their function to them, so `machine.SPI`, `machine.I2C` and
`machine.UART` reach a circuit that only sees pads. The bit-level engines are RP2350Sharp's
(`Peripherals/PinLevel`): PL011 frames in every LCR_H format with error flags, PL022 in all four
modes with DSS 4-16 and the hardware CSn, DW_apb_i2c START / repeated START / STOP, ACK / NACK with
TX_ABRT, open-drain lines and clock stretching. See [docs/pin-level-buses.md](docs/pin-level-buses.md).

- Library: `new RP2040Machine(pinLevelBuses: PinLevelBus.All)`; `None`, the default, leaves the
  machine as it was. IO_BANK0 gains `RegisterPadFunction`, `FunctionPads`, `IsFunctionRouted`,
  `LastPadEdgeCycle` / `FunctionDriving`; the blocks gain `EnablePinLevel` and `PinEngine`;
  `ClocksPeripheral.ClkSysFromAux`.
- The engines run on core 0's cycle counter and are brought up to the cycle of every access, so what
  firmware reads does not depend on the slicing; in the sweeps they are deferred like the other
  peripherals. Their dividers are scaled from the clocks firmware programmed (MicroPython's 48 MHz
  clk_peri) to the core's fixed 125 MHz.
- C ABI: `ST_RP2040_BUS_I2C` / `SPI` / `UART` / `ALL` (1, 2, 4, 7, RP2350's values) in
  `st_rp2040_create_ex`; `st_rp2040_create` is `create_ex(flash, ST_RP2040_BUS_ALL)`, as on RP2350.
  **Breaking:** `ST_RP2040_CYW43` is now 0x100 (RP2350's `ST_RP2350_CYW43`; it was 1, which is now
  `ST_RP2040_BUS_I2C`). Hosts that use the name only need a rebuild.
- Engine edges are reported with their own cycle. `st_rp2040_run_until_edge` arms an event-bounded
  guard with pin-level buses (slices of core 0 end at the next engine edge on a watched pad and right
  after an instruction that moved one, `RP2040Machine.RunCut`), so a lock-step host's answers are on
  time whatever the guard; `st_rp2040_cycles_until_edge` returns the engines' next edge (it was always
  `UINT64_MAX`). `st_rp2040_pin_set_input` applies pending peripheral time first, so an input is never
  sampled retroactively.
- Tests: `PinLevelDifferentialTests` (75), `PinLevelLockstepTests` (53), `MicroPythonBusTests` (6,
  MicroPython v1.28.0 RPI_PICO through the C ABI).

### New: the SiliconTwin.Abstractions contract

`RP2040Mcu` implements [`SiliconTwin.Abstractions`](https://github.com/silicon-twin/abstractions)
(`IMcu`, `IGpio`, `IPwmSource`, `IAdcInput`, `IPeripheralMap`), the shared embedder contract of
the Silicon Twin emulators, so a host drives an RP2040 and an AVR the same way.
`new RP2040Mcu()` owns its machine; `new RP2040Mcu(machine)` wraps one you already have.

- `IMcu.Run(cycleBudget)` is backed by the new `RP2040Machine.RunCycles`, which sizes its
  instruction slices from the measured cycles per instruction. It overshoots by about one
  instruction and costs the same as plain instruction batches. `Load` takes UF2, raw binary,
  and `Auto`.
- One `GPIO` bank of 30 pins, resolved through the function mux, with `SetInputs`,
  `ReleaseInputs`, pull masks, and `PinChanged` batched per pad sweep with the cycle of the
  write. It is armed on the first subscriber, so it costs nothing otherwise.
- `IPwmSource` from the PWM slices, `IAdcInput.ReadChannelVolts` mapped onto `ReadChannel`
  (3.3 V reference, 12-bit codes), and UART, SPI and I2C at byte level, including the
  firmware-as-I2C-slave side.
- Core additions: `IoBank0Peripheral.PadsChanged`, `GetPadOutputState`, `ReleaseExternalInput`,
  `SioPeripheral.ReleaseGpioExternal` (a pad driven from outside can now be given back to its
  pull), and `UartPeripheral.TryInjectByte`.

### Fixed: `spi.write()` of 32 bytes or more never returned

MicroPython switches `spi.write()` to DMA at 32 bytes (`dma_min_size_threshold`), claiming one 8-bit
channel that feeds SSPDR paced by `SPIx_TX` and one that drains SSPDR into a sink paced by `SPIx_RX`,
then blocking on the RX channel. That channel stalled BUSY forever with all but one beat pending, so
any SPI device moving real blocks — a display, an SD card, an external flash — hung the guest.

Four defects combined:

- `WriteByte`/`WriteHalfWord` implemented a sub-word write to SSPDR as a read-modify-write, and
  reading SSPDR pops the RX FIFO — so every 8-bit DMA beat ate the word received by the previous one.
  Sub-word writes to a FIFO register now go straight to the data path. The same read-modify-write was
  eating received bytes on UARTDR and IC_DATA_CMD, and is fixed there too.
- The SPI TX DREQ lines were hardwired always-ready, so a paced channel drained its whole source
  buffer at trigger time instead of following the shift register. The SPI peripheral now models TX
  FIFO occupancy (drained on `Tick`) and drives `SPIx_TX` from it, as the RP2350 already did.
- Nothing re-armed a channel stalled on a SPI or ADC DREQ, which is what turned any shortfall into a
  permanent hang rather than a slow transfer. `RP2040Machine` now wires the resume hooks.
- `ResumeDreq` was reentrancy-guarded by one global flag, but a duplex transfer needs nested resumes
  from two different DREQ lines, and the inner one was swallowed. The guard is now per DREQ line.

The per-channel `_executing` guard that stops a channel restarting inside its own beat loop — added to
the RP2350 for the equivalent PIO case — is ported here as well, since it is what keeps the relaxed
resume guard safe.

### Fixed: spec audit findings (F-xx) against the RP2040 datasheet, the SVD and ARMv6-M

Each item names the audit finding it closes. Behaviour the audit listed as open is not changed here.

- **CPU (ARMv6-M).** `BX`, `BLX` and `POP {PC}` to an even address raise a HardFault instead of clearing bit 0 (F-05).
  `EXC_RETURN` values are honoured only in Handler mode, so `BX LR` in Thread mode no longer unstacks (F-20).
  Unaligned LDR/STR/LDRH/STRH/LDM/STM/PUSH/POP raise a HardFault (F-06). A synchronous fault (UNDEFINED, BKPT)
  stacks the address of the faulting instruction, not the next one (F-08). An SVC that cannot be taken escalates
  to HardFault (F-09).
- **Decoder.** `YIELD` (`BF10`) is a hint, not `CBNZ` (F-03). `CBZ`, `CBNZ`, `CLZ` and `UDF.W` are undefined, as
  they are in ARMv6-M (F-04). Barriers reject second halfwords other than DSB/DMB/ISB (not an audit finding).
- **Bus.** Reads and writes to unmapped addresses raise a bus error (HardFault) instead of reading 0 and dropping
  the write (F-07). Narrow writes to IO registers are replicated across the bus, as the datasheet describes (F-19).
- **Bootrom.** With bootrom B2, the flash erase and program hooks sit at the B2 table entries, so the stack is no
  longer unbalanced after a call (F-16).
- **SysTick and SCB.** Reading `SYST_CSR` clears COUNTFLAG (F-02). CLKSOURCE selects the 1 us reference tick or the
  core clock (F-23). Writing CVR clears the counter and COUNTFLAG, and RVR is used as written, including 0 (F-24).
  `AIRCR.SYSRESETREQ` resets the core that wrote it, and ICSR reports VECTPENDING and ISRPENDING (F-25). SCR, CCR,
  SHCSR and the MPU registers are stored (F-17, partial: the MPU does not check accesses).
- **Timer and RTC.** TIMELW is applied when TIMEHW is written, so the 64-bit time can be set (F-10, partial: `PAUSE`
  is still missing). The RTC uses the SVD register layout for the date, the alarm and the interrupt registers (F-14).
- **PIO.** `DBG_CFGINFO` reports the SVD layout (F-15). The state machine reset values are CLKDIV 1.0, a right shift
  for IN and OUT, and SET_COUNT 5 (F-37).
- **GPIO and SIO.** The IO_BANK0 overrides (OUTOVER, OEOVER, INOVER, IRQOVER) take effect (F-38). Edge IRQs fire on
  pads driven by SIO (F-18). PROC1 has its own INTE/INTF/INTS and drives core 1's IRQ (F-39). Pads reset to 0x56,
  and IE and OD take effect (F-40). GPIO_OUT and GPIO_OE are 30 bits wide (F-41).
- **Deferred.** The DMA findings F-11, F-12, F-13, F-33, F-34, F-35 and F-36 are not in this change. Applying
  them made arduino-pico I2S and ADC streaming produce less output, and one I2S example hard-faulted, so they are
  tracked in the README roadmap until that regression is understood.

### Fixed: bugs found by running arduino-pico 6.3.0 firmware (examples, API sketches and ~3,400 randomised runs)

Each fix has a regression test under `tests/RP2040Sharp.Tests/Regression/` that fails without it (R08 is covered by the
watchdog sketch only).

- **NVIC lines 26-31 (R01).** The SDK's user IRQs never fired; arduino-pico runs its USB task on one, so the tail of USB
  output stalled.
- **SIO divider and interpolators are per core (R10)**, as on silicon: two cores dividing at once got each other's results.
- **DMA.** A write into a PIO TX FIFO no longer re-enters the same channel mid-transfer (R02: duplicated words, lost first RX
  word); DMA paced by UART, SPI, I2C or ADC DREQs resumes when the FIFO refills, and the I2C DREQs 32-35 are registered (R03).
- **ADC (R04).** The ADC is ticked, so `adc_run`, the FIFO and DMA produce data, and the sample period is `1 + DIV` ADC clocks
  instead of 96x that.
- **Watchdog.** A watchdog reset or `watchdog_reboot` now resets the chip (R07) and the USB CDC host re-enumerates after it
  (R08); the counter runs at the rate the SDK expects for erratum RP2040-E1 (R09: timeouts took twice as long).
- **PIO.** Changing FJOIN_TX/FJOIN_RX flushes both FIFOs (R05); side-set applies on the instruction's first cycle even when it
  stalls (R06: the `uart_tx` idle line was low); `SM_RESTART` clears the delay counter (R11).


### Fixed: high-register `ADD` read PC as the instruction address + 2 instead of + 4

ARMv6-M §A6.7.3 defines the value read from R15 by a high-register data-processing instruction as
the instruction address plus 4 (no alignment — that is the ADR form). The `ADD` high-register forms
(`ADD Rdn, PC`, `ADD SP, PC`, `ADD PC, PC`) read `Registers[15]` raw, which during dispatch holds
`insn + 2`, so every GCC/binutils PIC idiom (`ldr rN, [pc, #off]` + `add rN, pc`) and rustc/LLVM's
halfword jump tables landed 2 bytes short — e.g. MicroPython native modules computed a wrong GOT base
and branched wild. `MOV`/`CMP` high-register forms already compensated; `ADD` now matches them, and
tests pin the PC read of all three (including an instruction at a halfword-aligned address).

### Fixed: garbage firmware could crash or corrupt the embedding host instead of faulting the emulated CPU

The core is embedded in third-party apps (iCircuit), so a corrupt, truncated, random or merely buggy
image must end in a HardFault or a lockup of the *emulated* core — never an exception out of `Run`, and
never a read or write outside the emulator's native buffers. Fuzzing random images with hostile
PC/SP/VTOR/registers, plus register pokes at every peripheral window, found these ways to break that:

- **Null fetch pointer.** The fetch loop re-validates its cached window only when the PC's region
  *changes*. A fetch from a region with no backing store (APB/AHB, SIO, PPB, unmapped) correctly raised
  HardFault, but left the cache on that same region with a null pointer, so a HardFault handler — or a
  reset vector — in that region matched the cache and read through null (`NullReferenceException` /
  `AccessViolationException`). An unfetchable region now poisons the cached region id, so every fetch
  there faults again and a handler that cannot be fetched ends in lockup. The hot path is unchanged.
- **Tail spill.** The bus masks an address into a buffer and then reads or writes a whole
  halfword/word, so an unaligned access at the last 1-3 bytes of SRAM, flash or boot ROM touched bytes
  past the allocation. Every backing store now carries a 16-byte tail pad (`RandomAccessMemory.TailPad`).
- **Burst overrun.** The Thumb `PUSH`/`POP`/`STMIA`/`LDMIA` SRAM fast paths masked only the start
  address and advanced a raw pointer, so SP or a base near the top of SRAM overran the buffer by up to
  36 bytes (a host heap *write*). A burst that does not fit takes the existing per-word path, which wraps
  each access like the scalar loads and stores.
- **Bootrom flash hooks.** `flash_range_erase` / `flash_range_program` checked `offset + count` in
  `int`, so a count near 2^31 wrapped past the bounds check and filled or copied up to 2 GB from inside
  the flash buffer. The check is now done in `long`.
- **SSI erase.** A sector/block erase whose 24-bit address lay beyond the end of the flash made
  `end - start` wrap to ~4 GB for `InitBlock`. An erase starting past the flash is now ignored.
- **DMA `CHAIN_TO`.** The field is 4 bits wide but there are 12 channels, so `CHAIN_TO` 12-15 threw
  `IndexOutOfRangeException` when the transfer completed. Transfers also chain recursively, so two
  enabled channels chaining to each other overflowed the host stack (uncatchable). Chaining to a
  nonexistent channel is ignored, and a chain is cut after one lap of the channels.
- **USB DPRAM.** An endpoint's `BUFFER_ADDRESS` (up to 0xFFC0) plus `LENGTH` (up to 1023) can lie
  outside the 4 KB DPRAM; the copy threw `ArgumentOutOfRangeException`. It is now clamped to the DPRAM.

A second, line-by-line pass over every peripheral and the CYW43439 model (PIO, DMA, SSI, USB, SIO,
interpolators, timer/alarms, RTC, watchdog, ADC, PWM, UART/SPI/I2C, clocks, bridges, gSPI/SDPCM/HCI/virtual
network) found more:

- **PIO `OUT EXEC` / `MOV EXEC` recursion.** The word they execute is data, and that word can itself be a
  `MOV EXEC` (`X` holding the encoding of `mov exec, x`): the instruction re-ran itself from inside itself
  until the host stack overflowed (uncatchable). EXEC now nests at most 8 deep; deeper is a no-op.
- **PIO `WAIT` cleared IRQ flags.** A 1.1.0 edit dropped the braces around `if (WAIT IRQ, polarity 1) {
  Mutated(); _irq &= ~flag; }`, so *every* `WAIT` (GPIO, PIN, a stalled one, polarity 0) cleared IRQ flag
  `index & 7`. Only a successful `WAIT 1 IRQ` clears the flag it waited on again.
- **DMA re-entrancy.** A channel whose `WRITE_ADDR` is its own trigger register (or `MULTI_CHAN_TRIGGER`)
  re-entered `ExecuteChannel` on every beat and overflowed the host stack. The chain limit is now a single
  nesting limit (one level per channel) covering chains and beats that trigger channels.
- **DMA huge transfers.** A transfer runs inside the write that triggers it, and `TRANS_COUNT` is 32 bits:
  `0xFFFFFFFF` beats was minutes inside one bus access. One call now moves at most 2^20 beats (more than
  the SRAM holds); the rest stays `BUSY` and the next `Tick` moves the next slice, so the host keeps
  getting control back and real transfers (never that large) are not split.
- **Unbounded guest-driven buffers (host memory).** SSI TX/RX buffers and RX frame (a loop writing `DR0`
  forever), USB IN completions queued between ticks, the CYW43439 chip-to-host packet queue, backplane
  storage (sparse writes), the gSPI host-byte buffer (CS held, CLK toggling), and the virtual network's
  MAC / connection / lease tables and per-connection buffers all grew with guest traffic. Each now has a
  ceiling far above any legitimate use (e.g. 1024 queued packets, 1M backplane bytes, 1024 learned MACs).
- **Virtual network parsing.** A DHCP option 53 in the last two bytes of a frame, and a TCP segment whose
  IP total-length field exceeded the frame, indexed past the frame (`IndexOutOfRangeException` out of the
  guest's own bus write). Lengths are now clamped to the bytes actually received.

Adds `GarbageFirmwareTests`: a reproducer per bug, and deterministic fuzz (200 seeds on a bare core, on
the full two-core machine, and as raw MMIO pokes; every 16-bit opcode against hostile registers) that
runs in well under a second. `PioGarbageTests` (random programs, state-machine configuration and FIFO
traffic) and `WirelessGarbageTests` (SDPCM / Ethernet / DHCP / DNS / TCP / gSPI / HCI noise) do the same
for the PIO and the radio. `GARBAGE_FUZZ_SEEDS` / `GARBAGE_FUZZ_SCALE`, `PIO_FUZZ_SEEDS` and
`WIRELESS_FUZZ_SEEDS` widen any of them for a one-off hunt. The raw-MMIO fuzz still leaves the DMA window
out: a random 32-bit `TRANS_COUNT` is now a slice per `Run`, but each slice is still tens of milliseconds.

### Renamed: the TestKits are now `SiliconTwin.*`

| Old | New |
|---|---|
| `RP2040Sharp.TestKit` | `SiliconTwin.RP2040` |
| `RP2040Sharp.NanoFramework.TestKit` | `SiliconTwin.RP2040.NanoFramework` |

The old ids are unlisted. Update the `PackageReference`, or take the `SiliconTwin`
metapackage for the whole portfolio in one dependency.

`RP2040Sharp` and `RP2040Sharp.Wireless` — the emulator core — keep their names. They are
what embedders integrate, and they carry no entitlement check.

### Breaking: the TestKit checks entitlement in CI

Building a simulation now calls `SiliconTwin.Licensing.Entitlement.Require("rp2040")`.
BUSL-1.1 has said this since the relicensing; it is now enforced rather than only written
down.

- **Local development** — free, always. An expired or missing licence never stops anyone
  debugging on their own machine.
- **Public repositories** — free, verified against the `repository_visibility` claim of a
  GitHub-signed OIDC token.
- **Private CI** — needs a licence in the `SILICONTWIN_LICENSE` secret, or at
  `~/.silicontwin/license.key` on a self-hosted runner. See <https://silicontwin.co/licensing>.

Verification is local: an RSA signature check plus GitHub's public keys. Firmware, test
data and results never leave the runner, and air-gapped runners work. On GitHub Actions the
job needs `permissions: { contents: read, id-token: write }`, since a licence bound to no
organisation would work in any of them.

### Fixed

- **`ADD <Rdn>, <Rm>` read R15 two bytes low.** The Thumb T2 encoding of ADD (register) took
  the PC straight from the register file, where it has already been advanced by 2, instead of
  the instruction address plus 4 the architecture specifies (ARMv6-M §A6.7.4). Position
  independent code locates its own GOT with `add rN, pc`, so every natmod, every `-fpie`
  payload and every hand-rolled PIC stub read its global table from two bytes below where it
  sits. A CircuitPython native `.mpy` module therefore imported without raising and bound
  nothing: the failure looked like success (#68). `ADD SP, <Rm>` had the same omission and is
  fixed with it.

## [1.0.1-beta.6] - 2026-06-26

### Changed

- **Extracted the chip-agnostic nanoCLR introspection into the shared
  [NfClr.Core](https://github.com/begeistert/NfClr) package.** `ClrInspector`, `ClrLayout`,
  the heap walk and `ClrSession` now live once in `NfClr.Core` (`1.0.0-beta.1`);
  `RP2040Sharp.NanoFramework.TestKit` keeps only the RP2040 `IClrMemory`/`IClrHost` adapter
  (`Rp2040ClrHost` / `Rp2040ClrMemory`) and depends on `NfClr.Core` via **PackageReference**.
  `NanoClrHarness` is now a thin facade over `ClrSession`. Firmware symbols, layout and
  checksums derive from the nanoCLR ELF/DWARF when present.
- **Publishing migrated to NuGet Trusted Publishing (OIDC)** — no long-lived API key.

## [1.0.1-beta.5] - 2026-06-25

### Changed

- **Renamed the nanoFramework TestKit package** `RP2040Sharp.TestKit.NanoFramework` →
  **`RP2040Sharp.NanoFramework.TestKit`** (the previous id had the segments reversed). The
  rename is full: package id, assembly (`RP2040Sharp.NanoFramework.TestKit.dll`), namespaces
  (`RP2040Sharp.NanoFramework.TestKit[.SourceGen]`, were `RP2040.NanoFramework.TestKit*`),
  project folders, the bundled analyzer, and the `buildTransitive` targets file.
  **Migration:** switch to the new package id and update
  `using RP2040.NanoFramework.TestKit;` → `using RP2040Sharp.NanoFramework.TestKit;`. The old
  `RP2040Sharp.TestKit.NanoFramework` package (beta.2–beta.4) is unlisted.

## [1.0.1-beta.4] - 2026-06-25

### Changed

- **`RP2040Sharp.Wireless`: renamed the assembly and namespaces** to match the package id.
  The DLL is now `RP2040Sharp.Wireless.dll` and the namespaces are
  `RP2040Sharp.Wireless.Ble` / `RP2040Sharp.Wireless.Cyw43` (were `RP2040.Wireless.*`).
  **Breaking for `RP2040Sharp.Wireless` consumers:** update `using RP2040.Wireless.*` to
  `using RP2040Sharp.Wireless.*`.

## [1.0.1-beta.3] - 2026-06-25

Re-cut of `1.0.1-beta.2`, which only partially published (nuget.org rejects BUSL-1.1 as a
license *expression*, and the Wireless package shipped under a package id outside the
publisher's reserved prefix). This release ships the full **BUSL-1.1** package set with a
clean licensing and naming model.

**Licensing model:** only the **`RP2040Sharp` emulator core stays MIT**. Everything else —
`RP2040Sharp.TestKit`, `RP2040Sharp.Wireless`, and `RP2040Sharp.TestKit.NanoFramework` — is
**BUSL-1.1**, shipped as a license *file* with a generous Additional Use Grant (free for
individuals and for personal, educational, research, evaluation and open-source use;
Commercial Use — enterprise CI/CD and revenue-generating products/services — needs a
commercial license). Change Date 2030-06-25, converting to MIT.

### Added

- **`RP2040Sharp.Wireless` (BUSL-1.1)** — firmware-agnostic CYW43439 Wi-Fi + Bluetooth LE
  virtualization for the Raspberry Pi Pico W (gSPI/PIO, SDIO backplane, WHD/SDPCM, BT HCI).
- **`RP2040Sharp.TestKit.NanoFramework` (BUSL-1.1)** — boot a deployed .NET nanoFramework
  app on the emulator and assert against it: managed static/instance fields by name,
  run-until-managed-method, and a bundled **NanoSymbols** source generator (shipped as an
  analyzer with a `buildTransitive` targets file) that emits strongly-typed symbols for the
  app's methods and static fields.
- **B2 bootrom + revision selection**, **PIO FIFO probe**, and an emulator **strict mode**
  that surfaces silent accuracy gaps.

### Changed

- **`RP2040Sharp.TestKit` is now BUSL-1.1** (was MIT).
- **Renamed the Wireless package** `RP2040.Wireless` → **`RP2040Sharp.Wireless`** for a
  consistent `RP2040Sharp.*` id prefix.
- **Renamed the solution** `RP2040.sln` → `RP2040Sharp.sln`.
- BUSL packages ship the license as a **file** (`LICENSE-BUSL.txt`) instead of a license
  expression, so nuget.org accepts them.
- `publish.yml` packs and publishes all three BUSL-1.1 packages alongside the MIT core.

### Fixed

- **SSI direct-command flash RX** so the bootrom/CLR flash-access path works.
- **USB CDC** OUT delivery moved to a push model; `EP_ABORT` no longer drops deferred
  completions.

## [1.0.1-beta.2] - 2026-06-25 [YANKED]

Partial release — superseded by `1.0.1-beta.3`. `RP2040Sharp` (MIT),
`RP2040Sharp.TestKit.NanoFramework` (BUSL-1.1) and `RP2040Sharp.TestKit` (MIT) reached
nuget.org; the Wireless package failed to publish and `RP2040Sharp.TestKit` shipped under
the wrong (MIT) license. See `1.0.1-beta.3` for the corrected set.

## [1.0.0] - 2026-06-06

First stable release. Promotes `1.0.0-rc.2` after the TestKit gained CI guardrails for
use as a compiler/firmware validator (e.g. PyMCU). The public API is now considered
stable under [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

### Added

- **CI guardrails for the TestKit**, aimed at using the emulator to validate compiler
  output (e.g. PyMCU) without flaky or hanging builds:
  - `RP2040TestSimulation.RunUntilHalt(predicate, maxInstructions)` — a bounded run that
    can never hang: it returns a diagnostic `RunResult` (`PredicateMet` / `LockedUp` /
    `BudgetReached`) instead of stalling on wedged firmware.
  - CPU-health assertions: `Should().NotBeLockedUp()`, `NotHaveFaulted()`,
    `BeInThreadMode()`, and `HaveExecutedAtMost(n)`.
  - `RP2040TestSimulation.InstructionCount` — a deterministic, reproducible
    instruction-count metric for compiler-size regression checks.
- **`rp2040sharp` runner CLI** (`src/RP2040Sharp.Runner`) — loads a UF2/bin, runs it under
  a hard instruction budget, watches UART or USB-CDC for an expected string, and exits with
  a CI-friendly code (0 found · 1 not found · 2 firmware crashed). The `rp2040js`-style
  `--expect-text` workflow, headless.

### Changed

- Repository moved to the [PyMCU organization](https://github.com/PyMCU/RP2040Sharp);
  package metadata URLs updated accordingly. Published by `begeistert` as before.

## [1.0.0-rc.2] - 2026-06-06

### Changed

- **License hygiene:** pinned `FluentAssertions` to **6.12.0**, the last
  MIT-licensed release (7.x+ moved to a commercial Xceed license). This keeps the
  published `RP2040Sharp.TestKit` package and everything it pulls in MIT-compatible.
  The TestKit's custom assertions were ported back to the 6.x extension model.

## [1.0.0-rc.1] - 2026-06-06

First public release candidate. A high-performance RP2040 emulator in modern C#
(.NET 10) that runs real, unmodified firmware including MicroPython. C# port of
[rp2040js](https://github.com/wokwi/rp2040js) by Uri Shaked.

### Added

- **ARM Cortex-M0+ core** — full Thumb-1 instruction set, exceptions, NVIC,
  SysTick and PendSV, with an O(1) flat-table instruction decoder.
- **Dual-core support** — Core 1 launches through the SIO FIFO multicore
  handshake (RP2040 §2.8.3); both cores advance in lock-step.
- **GDB Remote Serial Protocol server** — debug Core 0 with `arm-none-eabi-gdb`
  via `target remote :3333` (registers, memory, single-step, breakpoints,
  detach). The demo accepts a `--gdb` flag.
- **Real RP2040 B1 BootROM** embedded as a resource; ROM table lookups and
  bit-manipulation helpers run natively.
- **Flash erase/program** through native C# hooks, so MicroPython's LittleFS
  filesystem works correctly.
- **MicroPython** boots to an interactive REPL over emulated USB-CDC.
- **Peripherals** — GPIO, SIO (spinlocks, interpolators, hardware divider),
  UART0/1, SPI0/1, I2C0/1 (master and slave-mode simulation), ADC, PWM (all 8
  slices), PIO0/1 (state machines + GPIO), DMA (all channels, DREQ sources),
  Timer/Alarms, Watchdog, RTC, USB CDC-ACM host, Clocks and Resets.
- **Per-pin GPIO API** (`SetGpioExternalIn`, `GetGpioOut`,
  `GetGpioOutputEnable`) for embedding in circuit simulators.
- **RP2040Sharp.TestKit** — a fluent harness for writing firmware integration
  tests.
- NuGet packaging for `RP2040Sharp` and `RP2040Sharp.TestKit` (AOT-compatible,
  trimmable), versioned automatically from git tags via MinVer.

### Known limitations

- XOSC, ROSC, PLL, PSM and VREG are register stubs (report stable/locked; no
  frequency model).
- Flash programming uses C# hooks rather than the SSI XIP hardware path.
- USB host support is CDC-only (sufficient for the MicroPython REPL).

[Unreleased]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.6...HEAD
[1.0.1-beta.6]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.5...v1.0.1-beta.6
[1.0.1-beta.5]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.4...v1.0.1-beta.5
[1.0.1-beta.4]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.3...v1.0.1-beta.4
[1.0.1-beta.3]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.2...v1.0.1-beta.3
[1.0.1-beta.2]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.1...v1.0.1-beta.2
[1.0.0]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.0-rc.2...v1.0.0
[1.0.0-rc.2]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.0-rc.1...v1.0.0-rc.2
[1.0.0-rc.1]: https://github.com/PyMCU/RP2040Sharp/releases/tag/v1.0.0-rc.1

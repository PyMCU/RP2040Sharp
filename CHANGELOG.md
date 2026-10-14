# Changelog

All notable changes to **RP2040Sharp** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

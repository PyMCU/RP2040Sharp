# Changelog

All notable changes to **RP2040Sharp** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.5...HEAD
[1.0.1-beta.5]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.4...v1.0.1-beta.5
[1.0.1-beta.4]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.3...v1.0.1-beta.4
[1.0.1-beta.3]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.2...v1.0.1-beta.3
[1.0.1-beta.2]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.1-beta.1...v1.0.1-beta.2
[1.0.0]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.0-rc.2...v1.0.0
[1.0.0-rc.2]: https://github.com/PyMCU/RP2040Sharp/compare/v1.0.0-rc.1...v1.0.0-rc.2
[1.0.0-rc.1]: https://github.com/PyMCU/RP2040Sharp/releases/tag/v1.0.0-rc.1

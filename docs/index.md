# RP2040Sharp

**RP2040Sharp** is a high-performance emulator for the Raspberry Pi **RP2040**
microcontroller, written entirely in modern **C# (.NET 10)**. It runs real, unmodified
RP2040 firmware — including **MicroPython** — and reaches an interactive REPL in ~3–4 s of
simulated time (~460 MIPS on Apple Silicon).

Its headline use case is **firmware integration testing in CI/CD**: run your real
firmware headlessly and assert on what it actually does — toggles a pin, prints over UART,
echoes bytes — with runs that are **deterministic** and **never hang**. It's how the
[PyMCU](https://docs.pymcu.org) compiler validates its output on every push. It is a C#
port and re-imagination of [rp2040js](https://github.com/wokwi/rp2040js) by Uri Shaked.

```bash
dotnet add package RP2040Sharp.TestKit   # fluent firmware-testing harness
```

```csharp
using RP2040.TestKit.Boards;       // PicoSimulation
using RP2040.TestKit.Extensions;   // .Should() for pins, UART, CPU

using var pico = new PicoSimulation(withUsbCdc: false);
pico.LoadFlash(File.ReadAllBytes("firmware.bin"));

// Run real firmware and assert on its behavior — bounded, deterministic, headless.
pico.RunUntilOutput(pico.Uart0, "ready", timeoutMs: 5_000).Should().BeTrue();
pico.Gpio[25].Should().BeHigh();
pico.Cpu.Should().NotHaveFaulted();
```

→ Start with the **[firmware integration testing guide](guides/ci-validation.md)**.

---

## Why RP2040Sharp

::::{grid} 1 2 2 3
:gutter: 3

:::{grid-item-card} Firmware testing in CI
:link: guides/ci-validation
:link-type: doc

A fluent TestKit to drive real firmware and assert on GPIO, UART, and CPU state —
`dotnet test`, no hardware.
:::

:::{grid-item-card} Deterministic by design
Time is driven by executed cycles, never by wall-clock. Runs are reproducible across
machines — ideal for CI and compiler regression checks.
:::

:::{grid-item-card} Never hangs in CI
Bounded execution: wedged or crashed firmware fails a test with a diagnostic reason
instead of stalling the build.
:::

:::{grid-item-card} Real firmware, unmodified
Boots the real RP2040 B1 BootROM and runs stock MicroPython/CircuitPython UF2 images —
no patches, no shims.
:::

:::{grid-item-card} GDB debugging
Attach `arm-none-eabi-gdb` over `target remote :3333` — registers, memory, single-step,
breakpoints.
:::

:::{grid-item-card} Dual-core
Core 1 launches through the SIO FIFO multicore handshake (RP2040 §2.8.3); both cores
advance in lock-step.
:::

:::{grid-item-card} AOT-friendly
The core library is trimmable and NativeAOT-compatible — embed it anywhere.
:::
::::

---

## How it compares to rp2040js

| | rp2040js | **RP2040Sharp** |
|---|---|---|
| Language | TypeScript | **C# (.NET 10)** |
| GDB stub | Yes | **Yes** |
| Dual-core | Partial | **Yes (working handshake)** |
| Test harness | — | **Fluent TestKit + assertions** |
| CI guardrails | — | **Bounded runs, health assertions, headless runner** |
| Deterministic clock | Yes | **Yes** |
| AOT / trimming | n/a | **Yes** |

---

```{toctree}
:maxdepth: 1
:hidden:
:caption: Getting Started

getting-started/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Guides

guides/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Peripherals

peripherals/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Compatibility

compat/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Reference

reference/index
```

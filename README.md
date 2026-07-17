# RP2040Sharp

![Build Status](https://github.com/PyMCU/RP2040Sharp/actions/workflows/test.yml/badge.svg)
![License](https://img.shields.io/badge/license-BUSL--1.1-orange.svg)
![.NET Version](https://img.shields.io/badge/.NET-10.0-purple)

**RP2040Sharp** is a high-performance emulator for the Raspberry Pi RP2040 microcontroller, written entirely in **modern C# (.NET 10)**. It runs real RP2040 firmware — including **MicroPython** — without modification.

This project is a port and re-imagination of the excellent [rp2040js](https://github.com/wokwi/rp2040js) project by Uri Shaked. The goal is to bring embedded emulation to the .NET ecosystem with a strong focus on speed and type safety, leveraging the latest runtime features.

## Performance

Measured on Apple Silicon (macOS, .NET 10, Release build):

| Workload | Throughput |
|---|---|
| Tight arithmetic loop (Flash, steady-state) | **~460 MIPS** |
| MicroPython boot | ~250 MIPS |
| MicroPython REPL execution | ~250 MIPS |

The emulator boots MicroPython v1.21.0 and reaches the interactive REPL in approximately **3–4 seconds of simulated time** (wall time varies by host). On iOS/MAUI (Mono AOT, no JIT), throughput is lower but the proportional optimizations still apply.

## Features

- **ARM Cortex-M0+** full instruction set (Thumb-1), including exceptions and NVIC
- **Real RP2040 BootROM** (B1) — loaded as an embedded resource; `rom_table_lookup`, `memcpy44`, `memset4` and bit-manipulation helpers run natively
- **Flash erase/program** via C# native hooks — MicroPython's LittleFS filesystem works correctly
- **MicroPython** boots to interactive REPL over emulated USB-CDC
- **Dual-core:** Core 1 launches via the SIO FIFO multicore handshake (RP2040 §2.8.3); both cores advance in lock-step
- **GDB stub:** debug Core 0 with `arm-none-eabi-gdb` over `target remote :3333` (registers, memory, stepi, breakpoints)
- **Peripherals:** GPIO, SIO, UART0/1, SPI0/1, I2C0/1 (master + slave simulation), ADC, PWM, PIO0/1, DMA, Timer, Watchdog, RTC, USB (CDC-ACM host for the MicroPython REPL), Clocks, PSM, Resets, and more
- **Per-pin GPIO API** (`SetGpioExternalIn`, `GetGpioOutputEnable`, `GetGpioOut`) for embedding in circuit simulators
- **TestKit** fluent API for writing firmware integration tests

## Getting Started

```bash
git clone https://github.com/PyMCU/RP2040Sharp.git
cd RP2040Sharp
dotnet restore
dotnet build
```

**Run the demo** (downloads MicroPython, boots it, executes REPL snippets, reports MIPS):

```bash
dotnet run --project src/RP2040Sharp.Demo -c Release
```

**Run the tests:**

```bash
dotnet test
```

## Basic Usage

```csharp
using RP2040.Peripherals;

var machine = new RP2040Machine();
machine.LoadFlash(File.ReadAllBytes("firmware.bin"));

// Capture UART output
machine.Uart0.OnByteTransmit += b => Console.Write((char)b);

// Run 125 000 cycles (1 ms at 125 MHz)
machine.Run(125_000);
```

### TestKit

```csharp
using RP2040.TestKit;

var sim = RP2040TestSimulation.Create()
    .WithBinary(File.ReadAllBytes("firmware.bin"))
    .AddUart(0, out var uart);

sim.RunMilliseconds(100);
Assert.Contains("Hello", uart.Text);
```

### Validating firmware in CI

Built for using the emulator as a compiler/firmware testkit (e.g. for
[PyMCU](https://github.com/PyMCU/PyMCU)) without flaky or hanging builds. A run is
always **bounded** — wedged firmware fails the test with a reason instead of stalling the
job — and the instruction count is **deterministic** and reproducible across machines.

```csharp
var sim = RP2040TestSimulation.Create()
    .WithBinary(File.ReadAllBytes("firmware.bin"))
    .AddUart(0, out var uart);

// Never hangs: returns PredicateMet / LockedUp / BudgetReached.
var result = sim.RunUntilHalt(() => uart.Text.Contains("PASS"), maxInstructions: 5_000_000);

result.Succeeded.Should().BeTrue();
sim.Cpu.Should().NotHaveFaulted();
sim.Cpu.Should().HaveExecutedAtMost(2_000_000);   // compiler-size regression guard
```

Or headless from a pipeline, with the `rp2040sharp` runner CLI (exit 0 found · 1 not
found · 2 crashed):

```bash
dotnet run --project src/RP2040Sharp.Runner -c Release -- \
    firmware.uf2 --expect-text "PASS" --channel uart
```

### GPIO integration (circuit simulators)

```csharp
// Inject an external signal on GP5
machine.Sio.SetGpioExternalIn(5, high: true);

// Read firmware output state
bool isHigh = machine.Sio.GetGpioOut(3);
bool isOutput = machine.Sio.GetGpioOutputEnable(3);
```

### Debugging with GDB

Run the demo with `--gdb` to expose Core 0 over the GDB Remote Serial Protocol:

```bash
dotnet run --project src/RP2040Sharp.Demo -c Release -- --gdb
# in another terminal:
arm-none-eabi-gdb -ex "target remote :3333"
```

Or embed the server in your own host:

```csharp
using RP2040.Gdb;

var server = new GdbTcpServer(myGdbTarget, port: 3333); // myGdbTarget : IGdbTarget
server.Start();
```

## Solution Structure

| Project | Description |
|---|---|
| `src/RP2040Sharp` | Core library — CPU, bus, peripherals, machine |
| `src/RP2040.TestKit` | Fluent test harness for firmware integration tests |
| `src/RP2040Sharp.Runner` | Headless `rp2040sharp` CLI: run firmware, `--expect-text`, CI exit codes |
| `src/RP2040Sharp.Demo` | Demo: boots MicroPython and drives the REPL |

## Architecture Notes

- **Instruction decoder:** 65 536-entry flat table of `delegate*` function pointers — O(1) dispatch with no branch on opcode
- **Bus reads:** explicit SRAM → Flash → BootROM fast paths with direct pointer arithmetic; no table indirection
- **Native hook guard:** registered hooks are bounded by `_nativeHookMax`; Flash-region instructions skip the dictionary lookup entirely via a single uint comparison
- **Fetch cache:** region and base pointer cached in `Run()` locals; region changes (rare) flush the cache

## Roadmap

### Core / CPU
- [x] Full Thumb-1 instruction set
- [x] Exceptions, NVIC, SysTick, PendSV
- [x] Native hooks (BootROM ROM API, flash erase/program)
- [x] WFI / WFE sleep with correct peripheral wakeup
- [x] Dual-core (Core 1 launch, SIO FIFO)
- [x] GDB stub for step-debugging firmware

### Peripherals
- [x] GPIO, SIO (spinlocks, interpolator)
- [x] UART0 / UART1
- [x] SPI0 / SPI1
- [x] I2C0 / I2C1 (master + slave-mode simulation)
- [x] ADC
- [x] PWM (all 8 slices)
- [x] PIO0 / PIO1 (state machines, GPIO integration)
- [x] DMA (all channels, DREQ sources)
- [x] USB (CDC-ACM host driver for the MicroPython REPL)
- [x] Timer / Alarms, Watchdog, RTC
- [x] Clocks, Resets
- [~] XOSC, ROSC, PLL, PSM, VREG — register stubs (report stable/locked; no frequency model)
- [ ] Flash programming via SSI (XIP hardware path)

### Ecosystem
- [x] UF2 parser in demo
- [x] Real RP2040 B1 BootROM (embedded resource)
- [x] MicroPython v1.21.0 boots to REPL
- [x] Per-pin GPIO API for circuit simulator embedding
- [ ] iCircuit element (`RP2040Elm`) — in design
- [ ] NativeAOT targets (Windows, Linux, macOS, iOS)
- [ ] WebAssembly (WASM) target

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/my-feature`).
3. Ensure all tests pass (`dotnet test`).
4. Commit following [Conventional Commits](https://www.conventionalcommits.org/).
5. Open a Pull Request against `master`.

## License

RP2040Sharp is licensed under the **Business Source License 1.1** — the whole project, the emulator core included. It converts automatically to the **MIT License** (the Change License) on the Change Date, four years after each release. See [LICENSE](LICENSE) and [LICENSE-BUSL.txt](LICENSE-BUSL.txt).

Use for personal, hobby, educational, academic-research, evaluation or open-source purposes is free of charge. Commercial Use — as defined in [LICENSE-BUSL.txt](LICENSE-BUSL.txt) — requires a separate commercial license.

Based on the original work from [rp2040js](https://github.com/wokwi/rp2040js) © 2021 Uri Shaked, used under the MIT License; that notice is retained as attribution in [LICENSE](LICENSE) and [NOTICE.txt](NOTICE.txt). C# port © 2024-2026 Iván Montiel Cardona.

The emulator embeds the RP2040 bootrom (© Raspberry Pi Trading Ltd, 3-clause BSD), with its floating point library removed and reimplemented natively — see [NOTICE.txt](NOTICE.txt) for the full third-party attribution.

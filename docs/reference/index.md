# Reference

## Key public types

| Type | Namespace | Role |
|---|---|---|
| `RP2040Machine` | `RP2040.Peripherals` | The emulated machine: CPU(s), bus, peripherals, `LoadFlash`/`Run`. |
| `CortexM0Plus` | `RP2040.Core.Cpu` | The CPU core: registers, `Step()`, `IsLockedUp`. |
| `RP2040TestSimulation` | `RP2040.TestKit` | Fluent test harness: `RunUntilHalt`, probes, `InstructionCount`. |
| `PicoSimulation` | `RP2040.TestKit.Boards` | Raspberry Pi Pico board preset. |
| `RunResult` / `RunOutcome` | `RP2040.TestKit` | Diagnostic outcome of a bounded run. |
| `GdbTcpServer` / `GdbServer` | `RP2040.Gdb` | GDB Remote Serial Protocol server. |

## API documentation

The full public API is documented with XML-doc comments in the source. A generated API
reference (DocFX) is planned; until then, browse the
[source on GitHub](https://github.com/PyMCU/RP2040Sharp/tree/master/src) or rely on
IntelliSense from the NuGet packages.

```{note}
This page is a stub. Per-namespace reference pages will be added as the docs site grows.
```

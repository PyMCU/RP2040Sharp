# Peripherals

RP2040Sharp emulates the RP2040 peripheral set with enough fidelity to boot real firmware.
Most peripherals are accessed through `RP2040Machine` (e.g. `machine.Uart0`,
`machine.Sio`, `machine.Dma`) and observed in tests via TestKit probes.

## Coverage

| Peripheral | Status | Notes |
|---|---|---|
| GPIO / SIO | ✅ | Per-pin API, spinlocks, interpolators, hardware divider |
| UART0 / UART1 | ✅ | FIFO, baud config, DREQ for DMA |
| SPI0 / SPI1 | ✅ | Master mode, FIFO, DREQ |
| I2C0 / I2C1 | ✅ | Master + slave-mode simulation |
| ADC | ✅ | 5 channels incl. temperature sensor, FIFO |
| PWM | ✅ | All 8 slices |
| PIO0 / PIO1 | ✅ | 4 state machines each, GPIO integration |
| DMA | ✅ | All channels, DREQ sources |
| Timer / Watchdog / RTC | ✅ | Alarms, microsecond counter |
| USB | ✅ | CDC-ACM host (MicroPython REPL) |
| Clocks / Resets | ✅ | Clock mux, per-peripheral reset |
| XOSC / ROSC / PLL / PSM / VREG | 🟡 | Register stubs (report stable/locked; no frequency model) |
| SSI flash (XIP hardware path) | ⛔ | Flash program/erase via C# hooks instead |

```{note}
Per-peripheral reference pages are being filled in. For now, the API surface is documented
via XML-doc comments in the source; see [Reference](../reference/index.md).
```

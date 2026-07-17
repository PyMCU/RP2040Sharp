# Quickstart

## Run a firmware image and capture its serial output

```csharp
using RP2040.Peripherals;

var machine = new RP2040Machine();

// Load a raw flash image or a UF2 (use Uf2ToFlash for UF2 files).
machine.LoadFlash(File.ReadAllBytes("firmware.bin"));

// Forward UART0 TX to the console.
machine.Uart0.OnByteTransmit += b => Console.Write((char)b);

// Run 1 ms of simulated time (125 000 cycles at 125 MHz).
machine.Run(125_000);
```

Loading a UF2 image:

```csharp
var flash = RP2040Machine.Uf2ToFlash(File.ReadAllBytes("firmware.uf2"));
machine.LoadFlash(flash!);
```

## Drive a test with the TestKit

The TestKit wraps `RP2040Machine` in a fluent harness with probes and assertions:

```csharp
using RP2040.TestKit;

var sim = RP2040TestSimulation.Create()
    .WithBinary(File.ReadAllBytes("firmware.bin"))
    .AddUart(0, out var uart);

sim.RunMilliseconds(100);

Assert.Contains("Hello", uart.Text);
```

For bounded, never-hanging runs and CPU-health checks, see
[Firmware testing with the TestKit](../guides/testkit.md).

## Inject and read GPIO

```csharp
machine.Sio.SetGpioExternalIn(5, high: true);   // drive GP5 from outside
bool isHigh   = machine.Sio.GetGpioOut(3);       // read firmware's GP3 output
bool isOutput = machine.Sio.GetGpioOutputEnable(3);
```

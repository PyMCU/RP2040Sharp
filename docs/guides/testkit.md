# Firmware testing with the TestKit

`RP2040Sharp.TestKit` turns the emulator into a fluent test harness: build a simulation,
attach probes, run it under a bound, and assert on the result.

## Building a simulation

```csharp
using RP2040.TestKit;

var sim = RP2040TestSimulation.Create()
    .WithFrequency(125_000_000)
    .WithBinary(File.ReadAllBytes("firmware.bin"))
    .AddUart(0, out var uart);
```

`PicoSimulation` is a convenience board preset (UART0/1 + USB-CDC wired up):

```csharp
using RP2040.TestKit.Boards;

using var pico = new PicoSimulation();
pico.LoadFlash(RP2040Machine.Uf2ToFlash(uf2)!);
```

## Bounded runs that never hang

A fixed `RunMilliseconds` is fine for healthy firmware, but a wedged or crashed program
would run forever. `RunUntilHalt` is **bounded** and returns *why* it stopped:

```csharp
var result = sim.RunUntilHalt(
    () => uart.Text.Contains("PASS"),
    maxInstructions: 5_000_000);

switch (result.Outcome)
{
    case RunOutcome.PredicateMet:   /* success */            break;
    case RunOutcome.LockedUp:       /* firmware crashed */   break;
    case RunOutcome.BudgetReached:  /* timed out / wedged */ break;
}
```

There is a convenience overload for the common "wait for serial text" case:

```csharp
var result = sim.RunUntilHalt(uart, "PASS");
result.Succeeded.Should().BeTrue();
```

## CPU-health assertions

```csharp
using RP2040.TestKit.Extensions;   // brings in .Should() for the CPU

sim.Cpu.Should().NotBeLockedUp();
sim.Cpu.Should().NotHaveFaulted();    // not in HardFault (IPSR != 3)
sim.Cpu.Should().BeInThreadMode();    // IPSR == 0
```

## Deterministic instruction count

`InstructionCount` is reproducible across machines (the clock is driven by executed
cycles, not wall-clock), so it works as a compiler-size regression guard:

```csharp
sim.RunUntilHalt(uart, "PASS");
sim.Cpu.Should().HaveExecutedAtMost(2_000_000);
```

## Output, GPIO and peripherals

```csharp
uart.Text.Should().Contain("ready");          // captured UART text
pico.Gpio[25].Should().BeHigh();              // GpioPin assertions
```

See [Peripherals](../peripherals/index.md) for the per-peripheral API.

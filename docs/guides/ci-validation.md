# Firmware integration testing in CI

RP2040Sharp's headline use case: run your **real firmware** in CI and assert on what it
actually does — toggles a pin, prints over UART, echoes bytes — **without flaky or hanging
builds**. It's how [PyMCU](https://docs.pymcu.org) validates the firmware its compiler
produces on every push.

Why it works well in CI:

- **Deterministic** — time is driven by executed CPU cycles, never wall-clock, so a run is
  reproducible across machines and runners.
- **Never hangs** — execution is bounded; wedged or crashed firmware fails a test with a
  reason instead of stalling the job until the runner times out.
- **Fast & headless** — no hardware, no USB, no flashing; MicroPython boots in seconds,
  bare-metal firmware in milliseconds.

## Set up a test project

The TestKit ships on NuGet. Any .NET test runner works (this guide uses NUnit, like
PyMCU; xUnit is identical in spirit).

```bash
dotnet add package RP2040Sharp.TestKit
```

```xml
<!-- IntegrationTests.csproj -->
<PackageReference Include="RP2040Sharp.TestKit" Version="1.0.0" />
<PackageReference Include="NUnit" Version="3.14.0" />
<PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
<PackageReference Include="FluentAssertions" Version="6.12.0" />
```

Two namespaces give you everything:

```csharp
using RP2040.TestKit.Boards;      // PicoSimulation
using RP2040.TestKit.Extensions;  // .Should() for Gpio pins, UART, CPU
```

## The basic shape of a test

```csharp
[TestFixture]
public class BlinkTests
{
    private static byte[] _firmware = null!;

    // Build (or load) the firmware once for the whole fixture.
    [OneTimeSetUp]
    public void Build() => _firmware = MyCompiler.Build("blink");   // or File.ReadAllBytes(...)

    // A fresh machine per test keeps tests independent.
    private static PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);  // bare-metal: no USB host
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Led_is_high_after_boot()
    {
        using var pico = Sim();
        pico.RunMilliseconds(5);
        pico.Gpio[25].Should().BeHigh();
    }
}
```

```{tip}
For **bare-metal** firmware that doesn't use USB, construct `new PicoSimulation(withUsbCdc: false)`.
Attaching a USB host makes the device think a host is present (and, for CircuitPython, mounts
the filesystem read-only). For MicroPython/CircuitPython REPL tests, leave it on (the default).
```

`LoadFlash` takes a flat flash image (`byte[]`). For UF2 files, convert first:

```csharp
pico.LoadFlash(RP2040Machine.Uf2ToFlash(File.ReadAllBytes("firmware.uf2"))!);
```

## Asserting on behavior

### GPIO — the blink test

```csharp
[Test]
public void Led_toggles_over_time()
{
    using var pico = Sim();
    bool sawHigh = false, sawLow = false;

    // Sample across more than one blink period.
    for (int i = 0; i < 120 && !(sawHigh && sawLow); i++)
    {
        pico.RunMilliseconds(20);
        if (pico.Gpio[25].OutputValue) sawHigh = true; else sawLow = true;
    }

    sawHigh.Should().BeTrue("the LED should be driven high during a blink");
    sawLow.Should().BeTrue("the LED should be driven low during a blink");
}
```

Pin assertions: `Should().BeHigh()`, `BeLow()`, `BeOutput()`, `BeInput()`; raw state via
`Gpio[n].OutputValue` / `Gpio[n].DigitalValue`. You can also drive inputs from the test:
`pico.Sio.SetGpioExternalIn(5, high: true)`.

### UART — banners and round-trips

```csharp
[Test]
public void Boot_prints_banner()
{
    using var pico = Sim();
    pico.RunUntilOutput(pico.Uart0, "ECHO", timeoutMs: 20_000).Should().BeTrue();
    pico.Uart0.Should().Contain("ECHO");
}

[Test]
public void Echoes_a_byte()
{
    using var pico = Sim();
    pico.RunUntilOutput(pico.Uart0, "ECHO", timeoutMs: 20_000);
    var before = pico.Uart0.ByteCount;

    pico.Uart0.InjectByte(0x41);   // 'A' — drive the device's RX line
    pico.RunUntilOutput(pico.Uart0, _ => pico.Uart0.ByteCount > before, timeoutMs: 5_000)
        .Should().BeTrue("the firmware should echo the injected byte");

    pico.Uart0.Bytes[^1].Should().Be(0x41);
}
```

`RunUntilOutput` runs in batches until the text appears (or a predicate over the captured
text/state is true), or the timeout elapses — returning `bool`. UART probe surface:
`.Text`, `.Bytes`, `.ByteCount`, `.Contain(...)`, `.InjectByte(...)`.

### Pass/fail firmware and crash detection

If your firmware prints a result and you want a single bounded check that never hangs, use
`RunUntilHalt` — it returns *why* it stopped:

```csharp
var result = pico.RunUntilHalt(pico.Uart0, "PASS", maxInstructions: 5_000_000);

result.Succeeded.Should().BeTrue($"firmware halted with {result.Outcome}");  // PredicateMet / LockedUp / BudgetReached
pico.Cpu.Should().NotHaveFaulted();
```

See [Firmware testing with the TestKit](testkit.md) for the full assertion set
(`NotBeLockedUp`, `BeInThreadMode`, `HaveExecutedAtMost`, …).

## Compiling firmware on the fly (with caching)

If your suite *compiles* firmware (a compiler's own tests, like PyMCU), build each program
**once per session** and cache it — compilation, not emulation, is the slow part. A small
helper does the job:

```csharp
public static class Firmware
{
    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    public static byte[] Build(string name) =>
        Cache.GetOrAdd(name, _ => new Lazy<byte[]>(() =>
        {
            Gate.Wait();                       // bound parallel compiler invocations
            try { return Compile(name); }      // shell out to your build tool → return the .bin
            finally { Gate.Release(); }
        })).Value;
}
```

- `Lazy<byte[]>` ensures each program compiles exactly once even under parallel test runs.
- The `SemaphoreSlim` keeps a fixture-heavy suite from spawning one compiler per core.
- Call it from `[OneTimeSetUp]`, never per test.

## Run it in GitHub Actions

No special setup — it's just `dotnet test`:

```yaml
name: Integration tests
on: [push, pull_request]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet test -c Release
```

Because runs are deterministic and bounded, the job is stable: it won't flake on timing and
won't hang on broken firmware.

## Headless runner CLI

For pipelines that don't host C# — e.g. a build matrix that only needs an exit code — use
the `rp2040sharp` runner instead:

```bash
dotnet run --project src/RP2040Sharp.Runner -c Release -- \
    firmware.uf2 --expect-text "PASS" --channel uart --max-instructions 5000000
```

| Exit | Meaning |
|---|---|
| `0` | expected text found |
| `1` | text not found within the budget |
| `2` | firmware crashed (CPU lockup) |
| `64` / `66` | usage error / image not found |

| Option | Default | Description |
|---|---|---|
| `--expect-text <text>` | — | Pass only if `<text>` appears in serial output |
| `--channel uart\|usb` | `uart` | Serial channel to watch |
| `--max-instructions <n>` | `500000000` | Hard execution budget |
| `--quiet` | off | Don't echo serial output to stdout |

Serial output goes to **stdout**; the run summary to **stderr**.

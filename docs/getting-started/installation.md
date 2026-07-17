# Installation

RP2040Sharp targets **.NET 10**. You need the .NET 10 SDK installed.

## From NuGet

```bash
dotnet add package RP2040Sharp           # the emulator core
dotnet add package RP2040Sharp.TestKit   # fluent harness for firmware tests (optional)
```

| Package | Purpose |
|---|---|
| [`RP2040Sharp`](https://www.nuget.org/packages/RP2040Sharp) | CPU, bus, peripherals, `RP2040Machine`. AOT-compatible, trimmable. |
| [`RP2040Sharp.TestKit`](https://www.nuget.org/packages/RP2040Sharp.TestKit) | `RP2040TestSimulation`, probes, and FluentAssertions-based health checks. |

## From source

```bash
git clone https://github.com/PyMCU/RP2040Sharp.git
cd RP2040Sharp
dotnet build
dotnet test
```

## Headless runner CLI

The repository also ships a headless runner (`src/RP2040Sharp.Runner`) for CI pipelines —
see [Validating firmware in CI](../guides/ci-validation.md).

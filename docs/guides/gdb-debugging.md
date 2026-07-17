# Debugging with GDB

RP2040Sharp ships a GDB Remote Serial Protocol server, so you can debug Core 0 with
`arm-none-eabi-gdb` exactly as you would on real hardware.

## From the demo

```bash
dotnet run --project src/RP2040Sharp.Demo -c Release -- --gdb
```

Then, in another terminal:

```bash
arm-none-eabi-gdb -ex "target remote :3333"
```

```
(gdb) info registers
(gdb) x/4xw 0x20000000
(gdb) stepi
(gdb) break *0x10000100
(gdb) continue
```

## Embedding the server in your own host

```csharp
using RP2040.Gdb;

var server = new GdbTcpServer(myGdbTarget, port: 3333); // myGdbTarget : IGdbTarget
server.OnLog = Console.Error.WriteLine;
server.Start();
```

`IGdbTarget` exposes the machine and an `Execute()`/`Stop()` pair so your host controls
when the CPU is free-running versus halted.

## Supported commands

Registers (`g`, `p`/`P`, including xPSR and MSP/PSP/PRIMASK/CONTROL), memory (`m`/`M`),
`c` (continue), `s`/`vCont;s` (step), `D` (detach), and BKPT-driven stop replies with PC
rewind. `BASEPRI`/`FAULTMASK` read as 0 — the Cortex-M0+ does not implement them.

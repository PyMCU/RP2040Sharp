# Compatibility

RP2040Sharp runs real, unmodified firmware images.

## MicroPython

Stock MicroPython UF2 images for the Raspberry Pi Pico boot to an interactive REPL over
emulated USB-CDC. The integration suite runs against **v1.19.1**, **v1.20.0**, and
**v1.21.0**. Flash program/erase is implemented via native C# hooks, so MicroPython's
LittleFS filesystem works correctly.

```csharp
using var pico = new PicoSimulation();
pico.LoadFlash(RP2040Machine.Uf2ToFlash(microPythonUf2)!);
pico.RunUntilOutput(pico.UsbCdc, ">>> ", timeoutMs: 60_000);
```

## CircuitPython

CircuitPython boots to its REPL as well. USB host support is currently **CDC-only**
(sufficient for the REPL); MSC/HID host drivers are not included.

## Bare-metal (pico-sdk, PyMCU)

Any firmware built with the pico-sdk toolchain runs directly. This is the primary use
case for [PyMCU](https://docs.pymcu.org), which compiles Python to bare-metal ARM and uses
RP2040Sharp as its RP2040 integration testkit.

using NanoFramework.Clr;
using RP2040.Core.Cpu;
using RP2040.TestKit.Boards;

namespace RP2040Sharp.NanoFramework.TestKit;

/// <summary>
/// Adapts an RP2040Sharp <see cref="PicoSimulation"/> (plus its <see cref="NanoFirmware"/> for symbol
/// resolution) to the chip-agnostic <see cref="IClrHost"/>, so the shared <see cref="ClrSession"/> can
/// drive and read this emulator. This is the one RP2040-specific seam; the run/snapshot/profile logic
/// lives in NanoFramework.Clr.Core.
/// </summary>
public sealed class Rp2040ClrHost : IClrHost
{
    private readonly PicoSimulation _pico;
    private readonly NanoFirmware _firmware;

    public Rp2040ClrHost(PicoSimulation pico, NanoFirmware firmware)
    {
        _pico = pico;
        _firmware = firmware;
    }

    public uint ReadWord(uint address) => _pico.Rp2040.Bus.ReadWord(address);

    public ushort ReadHalfWord(uint address) => _pico.Rp2040.Bus.ReadHalfWord(address);

    public byte ReadByte(uint address) => _pico.Rp2040.Bus.ReadByte(address);

    public uint Pc => _pico.Cpu.Registers.PC;

    public long InstructionCount => _pico.InstructionCount;

    public bool IsLockedUp => _pico.Cpu.IsLockedUp;

    public uint ResolveSymbol(string name) => _firmware.ResolveSymbol(name);

    public uint ArgRegister(int index) => index switch
    {
        0 => _pico.Cpu.Registers.R0,
        1 => _pico.Cpu.Registers.R1,
        2 => _pico.Cpu.Registers.R2,
        3 => _pico.Cpu.Registers.R3,
        _ => _pico.Cpu.Registers.R0,
    };

    public void RunInstructions(long count) => _pico.RunInstructions((int)count);

    public void RunProfiled(long count, IClrObserver observer)
        => _pico.Rp2040.RunProfiled((int)count, new Bridge(observer));

    // Forwards the emulator's per-instruction hook to the chip-agnostic observer (drops the opcode,
    // which the core observers don't use).
    private sealed class Bridge : IProfilingObserver
    {
        private readonly IClrObserver _observer;

        public Bridge(IClrObserver observer) => _observer = observer;

        public void OnInstruction(uint pc, ushort opcode, long cycles) => _observer.OnInstruction(pc, cycles);
    }
}

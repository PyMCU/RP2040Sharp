using RP2040.Core.Cpu;
using RP2040.Core.Memory;
using RP2040.Peripherals;
using RP2040.Peripherals.Adc;
using RP2040.Peripherals.Dma;
using RP2040.Peripherals.Pio;
using RP2040.Peripherals.Sio;
using RP2040.Peripherals.Watchdog;

namespace RP2040.Peripherals.Tests.Regression;

/// <summary>Regression for R02-dma-same-channel-reentrancy: found by running arduino-pico firmware on the emulator.</summary>
public class R02_dma_same_channel_reentrancy
{
    private sealed class WakingDevice : IMemoryMappedDevice
    {
        public readonly List<uint> Writes = new();
        public Action? OnWrite;
        public uint Size => 0x1000;
        public byte ReadByte(uint a) => 0; public ushort ReadHalfWord(uint a) => 0; public uint ReadWord(uint a) => 0;
        public void WriteByte(uint a, byte v) { } public void WriteHalfWord(uint a, ushort v) { }
        public void WriteWord(uint a, uint v) { Writes.Add(v); OnWrite?.Invoke(); }
    }

    [Fact]
    public void Dma_channel_is_not_restarted_re_entrantly_by_a_dreq_wake_from_its_own_write()
    {
        var bus = new BusInterconnect();
        var cpu = new CortexM0Plus(bus);
        var dma = new DmaPeripheral(bus, cpu);
        var dev = new WakingDevice { OnWrite = () => dma.ResumeDreq(40) };   // like a PIO TX FIFO write waking a stalled `pull`
        bus.MapDevice(6, dev);
        dma.RegisterDreq(40, () => true);
        for (uint i = 0; i < 4; i++) bus.WriteWord(0x20000000 + i * 4, 0xC0DE0000 + i);
        dma.WriteWord(0x00, 0x20000000);                       // READ_ADDR
        dma.WriteWord(0x04, 0x60000000);                       // WRITE_ADDR (fixed)
        dma.WriteWord(0x08, 4);                                // TRANS_COUNT
        dma.WriteWord(0x0C, 1u | (2u << 2) | (1u << 4) | (40u << 15));   // CTRL_TRIG: EN, word, incr read, TREQ 40
        dev.Writes.Should().Equal(0xC0DE0000u, 0xC0DE0001u, 0xC0DE0002u, 0xC0DE0003u);
        dma.ReadWord(0x08).Should().Be(0u);
    }
}

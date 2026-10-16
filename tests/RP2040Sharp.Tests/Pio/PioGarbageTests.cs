using FluentAssertions;
using RP2040.Peripherals;
using RP2040.Peripherals.Pio;
using Xunit;

namespace RP2040.tests.Pio;

/// <summary>
/// Hostile-firmware robustness for the PIO: arbitrary programs, state-machine configuration and FIFO
/// traffic must stay inside the emulated block — never throw, hang or recurse on the host. The PIO
/// interprets 16-bit words the guest wrote, including EXEC (OUT/MOV EXEC) which feeds a data word back
/// in as the next instruction.
/// </summary>
public class PioGarbageTests
{
    private const uint Smb = 0x0C8;                      // SM0 register block
    private const uint InstrMem = 0x048;

    private static void Configure(PioPeripheral pio, int sm, uint exec, uint shift, uint pin, uint div = 1u << 16)
    {
        var b = Smb + (uint)sm * 0x18;
        pio.WriteWord(b + 0x00, div);
        pio.WriteWord(b + 0x04, exec);
        pio.WriteWord(b + 0x08, shift);
        pio.WriteWord(b + 0x14, pin);
    }

    [Fact]
    public void Mov_exec_of_a_mov_exec_does_not_recurse_until_the_host_stack_overflows()
    {
        // MOV EXEC, X with X holding the encoding of MOV EXEC, X: each execution runs the same
        // instruction again from inside itself, so it recursed without bound (uncatchable overflow).
        using var machine = new RP2040Machine();
        var pio = machine.Pio0;
        pio.InReset = false;
        const ushort movExecX = 0xA081;                  // mov exec, x
        pio.WriteWord(InstrMem + 0, movExecX);
        // X := 0xA081 through TXF + PULL + OUT X (SET only carries 5 bits).
        pio.WriteWord(0x010, movExecX);                  // TXF0
        pio.WriteWord(Smb + 0x10, 0x80A0);               // pull block
        pio.WriteWord(Smb + 0x10, 0x6020);               // out x, 32
        var act = () => pio.WriteWord(Smb + 0x10, movExecX);
        act.Should().NotThrow();
    }

    [Fact]
    public void Out_exec_chain_terminates()
    {
        using var machine = new RP2040Machine();
        var pio = machine.Pio0;
        pio.InReset = false;
        pio.WriteWord(Smb + 0x08, 1u << 19);             // shift right, no autopull
        pio.WriteWord(0x010, 0x60E0u | 0x1F);            // TXF0: `out exec, 31`
        pio.WriteWord(Smb + 0x10, 0x80A0);               // pull
        var act = () => pio.WriteWord(Smb + 0x10, 0x60E0 | 0x1F);   // out exec, 31 (OSR holds out exec)
        act.Should().NotThrow();
    }

    [Fact]
    public void Wait_on_gpio_or_pin_does_not_clear_irq_flags()
    {
        // Only a successful WAIT IRQ with polarity 1 clears the flag it waited on. The statement had
        // lost its braces, so every WAIT (GPIO, PIN, a stalled or polarity-0 IRQ wait) cleared
        // IRQ flag (index & 7) as a side effect.
        using var machine = new RP2040Machine();
        var pio = machine.Pio0;
        pio.InReset = false;
        pio.WriteWord(0x034, 0x0F);                      // IRQ_FORCE: flags 0-3
        pio.WriteWord(InstrMem + 0, 0x2003);  // wait 0 gpio 3 (pin low: passes)
        pio.WriteWord(InstrMem + 4, 0xE000);             // set pins, 0 (filler)
        pio.WriteWord(Smb + 0x04, 1u << 12);             // wrap 0..1
        pio.WriteWord(Smb + 0x00, 1u << 16);            // CLKDIV 1
        pio.WriteWord(0x000, 0x1);                       // enable SM0
        pio.Tick(8);
        pio.ReadWord(0x030).Should().Be(0x0F, "WAIT GPIO must leave the IRQ flags alone");
    }

    private static ushort RandomInstr(Random rng)
    {
        // Mostly well-formed instruction classes with random operands, some pure noise.
        if (rng.Next(8) == 0) return (ushort)rng.Next(0x10000);
        var op = rng.Next(8);
        return (ushort)((op << 13) | rng.Next(0x2000));
    }

    private static int Seeds()
    {
        var s = Environment.GetEnvironmentVariable("PIO_FUZZ_SEEDS");
        return int.TryParse(s, out var n) ? n : 150;
    }

    [Fact]
    public void Fuzz_random_programs_configuration_and_fifo_traffic_never_throw()
    {
        var seeds = Seeds();
        var first = int.TryParse(Environment.GetEnvironmentVariable("PIO_FUZZ_FIRST"), out var f) ? f : 1;
        for (var seed = first; seed < first + seeds; seed++)
        {
            var rng = new Random(seed);
            using var machine = new RP2040Machine();
            var pio = rng.Next(2) == 0 ? machine.Pio0 : machine.Pio1;
            pio.InReset = false;
            var act = () =>
            {
                for (var i = 0; i < 32; i++) pio.WriteWord(InstrMem + (uint)i * 4, RandomInstr(rng));
                for (var sm = 0; sm < 4; sm++)
                {
                    var exec = (uint)rng.Next() & 0x7FFFFFFF;
                    var shift = (uint)rng.Next();
                    var pin = (uint)rng.Next();
                    var div = rng.Next(3) == 0 ? (uint)rng.Next() : ((uint)rng.Next(1, 4) << 16);
                    Configure(pio, sm, exec, shift, pin, div);
                }
                for (var step = 0; step < 400; step++)
                {
                    switch (rng.Next(10))
                    {
                        case 8:
                        {
                            // A DREQ-paced DMA channel into a TX FIFO or out of an RX FIFO (re-enters the PIO
                            // from ResumeDreq whenever the SM moves a word).
                            var ch = (uint)rng.Next(12) * 0x40;
                            var blockBase = pio == machine.Pio0 ? 0x50200000u : 0x50300000u;
                            var smIdx = (uint)rng.Next(4);
                            var toPio = rng.Next(2) == 0;
                            var dreq = (toPio ? 0u : 4u) + smIdx + (pio == machine.Pio0 ? 0u : 8u);
                            machine.Bus.WriteWord(0x50000000 + ch + 0x00, toPio ? 0x20000000 : blockBase + 0x20 + smIdx * 4);
                            machine.Bus.WriteWord(0x50000000 + ch + 0x04, toPio ? blockBase + 0x10 + smIdx * 4 : 0x20000100);
                            machine.Bus.WriteWord(0x50000000 + ch + 0x10, 1u | (2u << 2) | (toPio ? 1u << 4 : 1u << 5) | (dreq << 15) | ((uint)rng.Next(12) << 11));
                            machine.Bus.WriteWord(0x50000000 + ch + 0x1C, (uint)rng.Next(1, 200));
                            break;
                        }
                        case 0: pio.WriteWord(0x000, (uint)rng.Next(0x1000)); break;
                        case 1: pio.WriteWord(0x010 + (uint)rng.Next(4) * 4, (uint)rng.Next()); break;
                        case 2: pio.ReadWord(0x020 + (uint)rng.Next(4) * 4); break;
                        case 3: pio.WriteWord(0x034, (uint)rng.Next()); break;
                        case 4: pio.WriteWord(Smb + (uint)rng.Next(4) * 0x18 + 0x10, (uint)rng.Next(0x10000)); break;
                        case 5: pio.WriteWord(Smb + (uint)rng.Next(4) * 0x18 + (uint)rng.Next(6) * 4, (uint)rng.Next()); break;
                        case 6: pio.WriteWord((uint)rng.Next(0x200) * 4, (uint)rng.Next()); break;
                        case 7: pio.WriteByte((uint)rng.Next(0x800), (byte)rng.Next()); break;
                        default: pio.Tick(rng.Next(1, 300)); break;
                    }
                    if (step % 50 == 0) machine.Run(100);
                }
            };
            act.Should().NotThrow($"pio seed {seed}");
        }
    }
}

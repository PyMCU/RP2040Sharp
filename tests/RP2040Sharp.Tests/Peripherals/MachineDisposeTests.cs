using System.Runtime.CompilerServices;
using FluentAssertions;
using RP2040.Peripherals;
using Xunit;

namespace RP2040Sharp.Tests.Peripherals;

/// <summary>
/// The machine owns native memory the interpreter reaches through raw pointers. A host is not expected to
/// call Dispose: the finalizers free the memory once nothing references the machine, which can never be
/// while a Run is in flight.
/// </summary>
public class MachineDisposeTests
{
    // SRAM: ldr r0, =SIO GPIO_OUT; movs r1, #1; str r1, [r0]; loop: adds r1, #1; b loop
    private static RP2040Machine MachineWritingGpioThenCounting()
    {
        var machine = new RP2040Machine();
        machine.Bus.WriteWord(0x20000000, 0x21014802);
        machine.Bus.WriteWord(0x20000004, 0x31016001);
        machine.Bus.WriteWord(0x20000008, 0x0000E7FD);
        machine.Bus.WriteWord(0x2000000C, 0xD0000010);
        machine.Cpu.Registers.SP = 0x20040000;
        machine.Cpu.Registers.PC = 0x20000000;
        return machine;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunAndDrop()
    {
        var machine = MachineWritingGpioThenCounting();
        machine.Run(100);
        return new WeakReference(machine);
    }

    private static void CollectAndFinalize()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void The_guest_counts_while_the_machine_is_alive()
    {
        using var machine = MachineWritingGpioThenCounting();
        machine.Run(100);
        machine.Cpu.Registers.R1.Should().BeGreaterThan(1);
        machine.Dispose();                               // idempotent with the using
    }

    [Fact]
    public void A_machine_nobody_disposed_is_collected()
    {
        var machine = RunAndDrop();
        CollectAndFinalize();
        machine.IsAlive.Should().BeFalse("nothing static may keep a dropped machine, and its native memory, alive");
    }

    [Fact]
    public void Collections_during_Run_leave_the_memory_in_place()
    {
        uint counted = 0;
        var done = false;
        // The machine is referenced only from the running thread, as when a host drops its own reference.
        var runner = new Thread(() =>
        {
            var machine = MachineWritingGpioThenCounting();
            for (var i = 0; i < 200; i++) machine.Run(1000);
            counted = machine.Cpu.Registers.R1;
            Volatile.Write(ref done, true);
        });

        runner.Start();
        while (!Volatile.Read(ref done)) CollectAndFinalize();
        runner.Join();

        counted.Should().Be(100_000);                    // 3 instructions to the loop, then one count every 2
    }

    [Fact]
    public void A_constructor_that_throws_leaves_nothing_for_the_finalizer_to_trip_on()
    {
        var construct = () => new RP2040Machine(flashSize: 0);
        construct.Should().Throw<ArgumentOutOfRangeException>();
        // An exception on the finalizer thread ends the process, so reaching the next line is the assertion.
        CollectAndFinalize();
    }
}

using RP2040.Peripherals.Fs;
using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// Emulated time never goes backwards across a chip-internal reset: st_rp2040_fs_commit(chip, 1), a watchdog reset
/// (CTRL.TRIGGER, what machine.reset() and watchdog_reboot() do) and AIRCR.SYSRESETREQ reboot the chip, but
/// st_rp2040_cycles, st_rp2040_time_ps, st_rp2040_cycle_to_ps and the pad-event stamps keep running on one timeline.
/// The firmware-triggered resets are stores core 0 runs inside st_rp2040_run.
/// </summary>
public unsafe class ResetTimelineTests
{
    private const uint WatchdogCtrl = 0x40058000, Trigger = 1u << 31;
    private const uint Aircr = 0xE000ED0C, SysResetReq = 0x05FA0004;
    private const uint Code = 0x20000400;

    private sealed class Rig : IDisposable
    {
        public readonly nint H = Abi.Create(0);
        public readonly Chip Chip;
        public readonly ulong EarlyCycle, EarlyPs;

        public Rig()
        {
            Chip = Exports.Lookup(H)!;
            var m = Chip.Machine;
            *(ushort*)(m.Bus.PtrSram + (Code & 0xFFFFF)) = 0x6001;       // str r1, [r0]
            *(ushort*)(m.Bus.PtrSram + (Code & 0xFFFFF) + 2) = 0xE7FE;   // b .
            Spin();
            Abi.Run(H, 20_000);
            EarlyCycle = Abi.Cycles(H);
            EarlyPs = Abi.CycleToPs(H, EarlyCycle);
            Abi.Run(H, 20_000);
        }

        public void Dispose() => Abi.Destroy(H);

        /// <summary>Core 0 spins in SRAM (a blank flash gives it nothing to run).</summary>
        public void Spin() => At(Code + 2);

        private void At(uint pc)
        {
            var r = Chip.Machine.Cpu;
            r.Registers.PC = pc;
            r.Registers.Waiting = false;
        }

        /// <summary>Core 0 runs STR r1, [r0] as the one instruction of an st_rp2040_run (on blank flash the bootrom
        /// it reboots into locks the core up, so the run stops at the reset).</summary>
        public void FirmwareStore(uint address, uint value)
        {
            var m = Chip.Machine;
            m.Cpu.Registers.R0 = address;
            m.Cpu.Registers.R1 = value;
            At(Code);
            var resets = m.ResetCount;
            Abi.Run(H, 1);
            Assert.Equal(resets + 1, m.ResetCount);
        }

        public void AssertTimelineContinues(ulong cyclesBefore, ulong psBefore)
        {
            Assert.True(Abi.Cycles(H) >= cyclesBefore, $"st_rp2040_cycles went back: {cyclesBefore} -> {Abi.Cycles(H)}");
            Assert.True(Abi.TimePs(H) >= psBefore, $"st_rp2040_time_ps went back: {psBefore} -> {Abi.TimePs(H)}");
            Assert.Equal(EarlyPs, Abi.CycleToPs(H, EarlyCycle));

            var ps = Abi.TimePs(H);
            Spin();
            Abi.Run(H, 20_000);
            Assert.True(Abi.TimePs(H) > ps, "time runs on after the reset");

            var seen = new List<ulong>();
            Chip.PinObserver = (_, _, _, cycle) => seen.Add(cycle);
            Chip.ScanPads();                              // what the reset itself changed
            seen.Clear();
            var m = Chip.Machine;
            m.Bus.WriteWord(0x40014000 + 2 * 8 + 4, 5);   // GP2: FUNCSEL = SIO
            m.Bus.WriteWord(0xD0000024, 1u << 2);         // GPIO_OE_SET
            Chip.ScanPads();
            Assert.Equal([Abi.Cycles(H)], seen);
            Assert.True(Abi.CycleToPs(H, seen[0]) >= psBefore);
        }
    }

    [Fact]
    public void Time_is_monotonic_across_fs_commit_with_reset()
    {
        using var r = new Rig();
        var volume = CircuitPythonFs.BuildVolume(1 << 20, [("boot_out.txt", "CircuitPython\n"u8.ToArray())]);
        volume.CopyTo(r.Chip.Machine.FlashMemory[0x100000..]);   // a formatted CIRCUITPY after the (empty) firmware
        Assert.Equal(0, Abi.FsPut(r.H, "code.py", "print('hi')\n"u8.ToArray()));
        var (c, ps) = (Abi.Cycles(r.H), Abi.TimePs(r.H));
        var resets = r.Chip.Machine.ResetCount;
        Assert.Equal(0, Abi.FsCommit(r.H, 1));
        Assert.Equal(resets + 1, r.Chip.Machine.ResetCount);
        r.AssertTimelineContinues(c, ps);
    }

    [Fact]
    public void Time_is_monotonic_across_a_watchdog_reset()
    {
        using var r = new Rig();
        var (c, ps) = (Abi.Cycles(r.H), Abi.TimePs(r.H));
        r.FirmwareStore(WatchdogCtrl, Trigger);
        r.AssertTimelineContinues(c, ps);
    }

    [Fact]
    public void Time_is_monotonic_across_aircr_sysresetreq()
    {
        using var r = new Rig();
        var (c, ps) = (Abi.Cycles(r.H), Abi.TimePs(r.H));
        r.FirmwareStore(Aircr, SysResetReq);
        r.AssertTimelineContinues(c, ps);
    }
}

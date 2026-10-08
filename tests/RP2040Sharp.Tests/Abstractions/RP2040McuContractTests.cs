// SPDX-License-Identifier: BUSL-1.1
using FluentAssertions;
using RP2040.Peripherals;
using SiliconTwin.Abstractions;
using Xunit;

namespace RP2040Sharp.Tests.Abstractions;

/// <summary>
/// The RP2040 adapter exercised only through the SiliconTwin.Abstractions interfaces, the way a circuit
/// host would use it. Direct machine access is limited to what stands in for firmware (register pokes
/// for the peripherals the tiny test program does not configure).
/// </summary>
public class RP2040McuContractTests
{
    private const int TogglePin = 15;
    private const ulong TogglePinBit = 1UL << TogglePin;

    // Hand-assembled Thumb: select SIO on GPIO15, enable its output, then flip it forever with a short
    // delay loop in between. Flash offset 0 branches to the code, the vector table sits at 0x100.
    private static byte[] ToggleImage()
    {
        var img = new byte[0x200];
        void W16(int o, ushort v) { img[o] = (byte)v; img[o + 1] = (byte)(v >> 8); }
        void W32(int o, uint v) { W16(o, (ushort)v); W16(o + 2, (ushort)(v >> 16)); }

        W16(0x000, 0xE082);                         // b 0x108
        W32(0x100, 0x20042000);                     // initial SP
        W32(0x104, 0x10000109);                     // reset vector
        W16(0x108, 0x4805);                         // ldr r0, =IO_BANK0 GPIO15_CTRL
        W16(0x10A, 0x2105);                         // movs r1, #5 (SIO)
        W16(0x10C, 0x6001);                         // str r1, [r0]
        W16(0x10E, 0x4A05);                         // ldr r2, =SIO base
        W16(0x110, 0x2301);                         // movs r3, #1
        W16(0x112, 0x03DB);                         // lsls r3, r3, #15
        W16(0x114, 0x6253);                         // str r3, [r2, #0x24] GPIO_OE_SET
        W16(0x116, 0x61D3);                         // loop: str r3, [r2, #0x1C] GPIO_OUT_XOR
        W16(0x118, 0x240A);                         // movs r4, #10
        W16(0x11A, 0x3C01);                         // delay: subs r4, #1
        W16(0x11C, 0xD1FD);                         // bne delay
        W16(0x11E, 0xE7FA);                         // b loop
        W32(0x120, 0x4001407C);
        W32(0x124, 0xD0000000);
        return img;
    }

    private static RP2040Mcu Booted()
    {
        var mcu = new RP2040Mcu();
        mcu.Load(FirmwareImage.FromBin(ToggleImage()));
        mcu.Reset();
        return mcu;
    }

    /// <summary>A minimal UF2 file wrapping <paramref name="payload"/> at the start of flash.</summary>
    private static byte[] Uf2Of(byte[] payload)
    {
        const int Chunk = 256;
        var blocks = (payload.Length + Chunk - 1) / Chunk;
        var file = new byte[blocks * 512];
        for (var b = 0; b < blocks; b++)
        {
            var s = file.AsSpan(b * 512, 512);
            BitConverter.TryWriteBytes(s[0..], 0x0A324655u);
            BitConverter.TryWriteBytes(s[4..], 0x9E5D5157u);
            BitConverter.TryWriteBytes(s[8..], 0u);
            BitConverter.TryWriteBytes(s[12..], 0x10000000u + (uint)(b * Chunk));
            BitConverter.TryWriteBytes(s[16..], (uint)Chunk);
            BitConverter.TryWriteBytes(s[20..], (uint)b);
            BitConverter.TryWriteBytes(s[24..], (uint)blocks);
            BitConverter.TryWriteBytes(s[28..], 0xE48BFF56u);
            payload.AsSpan(b * Chunk, Math.Min(Chunk, payload.Length - b * Chunk)).CopyTo(s[32..]);
            BitConverter.TryWriteBytes(s[508..], 0x0AB16F30u);
        }
        return file;
    }

    private static IPinBank GpioBank(IMcu mcu)
    {
        mcu.TryGet<IGpio>(out var gpio).Should().BeTrue();
        return gpio!.Banks[0];
    }

    // ── Identity and capabilities ────────────────────────────────────────

    [Fact]
    public void Exposes_every_capability_on_one_object()
    {
        using var mcu = new RP2040Mcu();

        mcu.Name.Should().Be("rp2040");
        mcu.ClockHz.Should().Be(125_000_000u);
        mcu.TryGet<IGpio>(out _).Should().BeTrue();
        mcu.TryGet<IPwmSource>(out _).Should().BeTrue();
        mcu.TryGet<IAdcInput>(out _).Should().BeTrue();
        mcu.TryGet<IPeripheralMap>(out _).Should().BeTrue();

        var bank = GpioBank(mcu);
        bank.Name.Should().Be("GPIO");
        bank.PinCount.Should().Be(30);
        ((IGpio)mcu).Banks.Should().HaveCount(1);
    }

    // ── Run, RunUntil, Reset ─────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(2_834)]
    [InlineData(125_000)]
    [InlineData(1_000_000)]
    public void Run_consumes_the_budget_and_overshoots_by_at_most_one_instruction(long budget)
    {
        using var mcu = Booted();
        var start = mcu.Cycles;

        var used = mcu.Run(budget);

        used.Should().BeGreaterThanOrEqualTo(budget);
        (used - budget).Should().BeLessThan(16, "the last instruction is the only allowed overshoot");
        (mcu.Cycles - start).Should().Be(used);
    }

    [Fact]
    public void Run_with_no_budget_does_nothing()
    {
        using var mcu = Booted();
        var start = mcu.Cycles;

        mcu.Run(0).Should().Be(0);
        mcu.Run(-5).Should().Be(0);
        mcu.Cycles.Should().Be(start);
    }

    [Fact]
    public void Carrying_the_overshoot_keeps_simulated_time_exact()
    {
        using var mcu = Booted();
        double carry = 0;
        var start = mcu.Cycles;

        for (var i = 0; i < 100; i++)
        {
            var budget = CycleBudget.Next(ref carry, mcu.ClockHz, 0.001);
            CycleBudget.Settle(ref carry, budget, mcu.Run(budget));
        }

        // 100 ms at 125 MHz, within the one overshoot still sitting in the carry.
        (mcu.Cycles - start).Should().BeInRange(12_500_000 - 16, 12_500_000 + 16);
    }

    [Fact]
    public void RunUntil_stops_at_the_requested_cycle()
    {
        using var mcu = Booted();
        var target = mcu.Cycles + 50_000;

        var used = mcu.RunUntil(target);

        mcu.Cycles.Should().BeInRange(target, target + 16);
        used.Should().BeGreaterThan(0);
        mcu.RunUntil(mcu.Cycles - 1).Should().Be(0);
    }

    [Fact]
    public void Soft_reset_keeps_counting_and_power_reset_starts_over()
    {
        using var mcu = Booted();
        mcu.Run(10_000);
        var before = mcu.Cycles;

        mcu.Reset(ResetKind.Soft);
        mcu.Cycles.Should().Be(before, "a soft reset restarts the core, not the time line");
        mcu.Run(1_000);
        mcu.Cycles.Should().BeGreaterThan(before);

        mcu.Reset(ResetKind.Power);
        mcu.Cycles.Should().Be(0);
        mcu.Run(1_000);
        mcu.Cycles.Should().BeInRange(1_000, 1_016);
    }

    [Fact]
    public void Reset_restarts_the_loaded_firmware_from_the_top()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        mcu.Run(5_000);
        bank.OutputEnableMask.Should().Be(TogglePinBit);

        mcu.Machine.IoBank0.WriteWord((uint)(TogglePin * 8 + 4), 31);   // pad back to NULL
        mcu.Reset();
        bank.OutputEnableMask.Should().Be(0);

        mcu.Run(5_000);
        bank.OutputEnableMask.Should().Be(TogglePinBit, "the program ran again and re-claimed the pad");
    }

    // ── Load ─────────────────────────────────────────────────────────────

    [Fact]
    public void Load_accepts_bin_uf2_and_auto_detects_both()
    {
        var bin = ToggleImage();
        var variants = new[]
        {
            FirmwareImage.FromBin(bin),
            FirmwareImage.FromUf2(Uf2Of(bin)),
            new FirmwareImage(FirmwareFormat.Auto, Uf2Of(bin)),
            new FirmwareImage(FirmwareFormat.Auto, bin),
        };

        foreach (var image in variants)
        {
            using var mcu = new RP2040Mcu();
            mcu.Load(image);
            mcu.Reset();
            mcu.Run(5_000);
            GpioBank(mcu).OutputEnableMask.Should().Be(TogglePinBit, $"{image.Format} should boot the program");
        }
    }

    [Fact]
    public void Load_at_an_offset_keeps_the_flash_below_it()
    {
        using var mcu = new RP2040Mcu();
        var bin = ToggleImage();
        mcu.Load(FirmwareImage.FromBin(bin.AsMemory(0, 0x180)));
        mcu.Load(FirmwareImage.FromBin(bin.AsMemory(0x180), 0x180));
        mcu.Reset();

        mcu.Run(5_000);

        GpioBank(mcu).OutputEnableMask.Should().Be(TogglePinBit);
    }

    [Fact]
    public void Load_keeps_cycles_monotonic()
    {
        using var mcu = Booted();
        mcu.Run(10_000);
        var before = mcu.Cycles;

        mcu.Load(FirmwareImage.FromBin(ToggleImage()));

        mcu.Cycles.Should().Be(before);
    }

    [Fact]
    public void Load_rejects_formats_the_core_cannot_read()
    {
        using var mcu = new RP2040Mcu();

        var hex = () => mcu.Load(FirmwareImage.FromIntelHex(":00000001FF"));
        var elf = () => mcu.Load(FirmwareImage.FromElf(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0 }));
        var autoElf = () => mcu.Load(new FirmwareImage(FirmwareFormat.Auto, new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }));
        var junkUf2 = () => mcu.Load(FirmwareImage.FromUf2(new byte[512]));
        var tooBig = () => mcu.Load(FirmwareImage.FromBin(new byte[16], 0xFFFFF0));

        hex.Should().Throw<NotSupportedException>();
        elf.Should().Throw<NotSupportedException>();
        autoElf.Should().Throw<NotSupportedException>();
        junkUf2.Should().Throw<InvalidDataException>();
        tooBig.Should().Throw<ArgumentException>();
    }

    // ── GPIO: chip side ──────────────────────────────────────────────────

    [Fact]
    public void Output_state_is_resolved_through_the_function_mux()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        bank.OutputEnableMask.Should().Be(0);

        mcu.Run(5_000);
        bank.OutputEnableMask.Should().Be(TogglePinBit);

        var seen = new HashSet<ulong>();
        for (var i = 0; i < 200; i++)
        {
            mcu.Run(7);
            seen.Add(bank.OutputLevels & TogglePinBit);
        }
        seen.Should().BeEquivalentTo(new ulong[] { 0, TogglePinBit }, "the pin is seen both low and high");
    }

    [Fact]
    public void Pwm_pad_counts_as_driven_through_the_mux()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        mcu.Machine.IoBank0.WriteWord(16 * 8 + 4, 4);
        bank.OutputEnableMask.Should().Be(0, "a stopped slice does not drive the pad");

        mcu.Machine.Pwm.WriteWord(0x04, 1 << 4);
        mcu.Machine.Pwm.WriteWord(0x10, 999);
        mcu.Machine.Pwm.WriteWord(0x0C, 250);
        mcu.Machine.Pwm.WriteWord(0x00, 1);

        bank.OutputEnableMask.Should().Be(1UL << 16);
    }

    [Fact]
    public void Pull_masks_follow_the_pads_registers()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        bank.PullUpMask.Should().Be(0);
        bank.PullDownMask.Should().Be(0);

        mcu.Machine.PadsBank0.WriteWord(4 + 5 * 4, (1 << 6) | (1 << 3));    // GPIO5: IE + PUE
        mcu.Machine.PadsBank0.WriteWord(4 + 9 * 4, (1 << 6) | (1 << 2));    // GPIO9: IE + PDE

        bank.PullUpMask.Should().Be(1UL << 5);
        bank.PullDownMask.Should().Be(1UL << 9);
    }

    // ── GPIO: events ─────────────────────────────────────────────────────

    [Fact]
    public void PinChanged_carries_the_cycle_of_each_toggle()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        var events = new List<PinChange>();
        bank.PinChanged += (in PinChange e) => events.Add(e);

        var start = mcu.Cycles;
        mcu.Run(20_000);
        var end = mcu.Cycles;

        events.Should().HaveCountGreaterThan(10);
        events.Should().OnlyContain(e => e.Changed == TogglePinBit && e.IsDriven(TogglePin));
        events[0].IsHigh(TogglePin).Should().BeFalse("the pin is first claimed as a low output");
        for (var i = 1; i < events.Count; i++)
            events[i].IsHigh(TogglePin).Should().Be(i % 2 == 1, "the pin alternates");
        events.Should().OnlyContain(e => e.Cycle > start && e.Cycle <= end);

        var gaps = events.Skip(2).Zip(events.Skip(3), (a, b) => b.Cycle - a.Cycle).Distinct().ToList();
        gaps.Should().ContainSingle("the loop runs the same instructions every time");
        gaps[0].Should().BeInRange(25, 45, "one pass of the delay loop");
    }

    [Fact]
    public void PinChanged_cycles_line_up_with_the_clock()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        PinChange last = default;
        bank.PinChanged += (in PinChange e) => last = e;
        mcu.Run(100_000);

        // The most recent edge is never ahead of the clock and never more than a loop behind it.
        last.Cycle.Should().BeLessThanOrEqualTo(mcu.Cycles);
        (mcu.Cycles - last.Cycle).Should().BeLessThan(100);
    }

    [Fact]
    public void WatchMask_limits_which_pins_report()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        bank.WatchMask.Should().Be((1UL << 30) - 1, "all pins by default");
        var count = 0;
        bank.PinChanged += (in PinChange _) => count++;

        bank.WatchMask = 1UL << 3;
        mcu.Run(20_000);
        count.Should().Be(0);

        bank.WatchMask = TogglePinBit;
        mcu.Run(20_000);
        count.Should().BeGreaterThan(10);
    }

    [Fact]
    public void Late_subscribers_are_not_told_about_the_past()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        mcu.Run(5_000);                         // pad already claimed and toggling
        var first = true;
        PinChange firstEvent = default;
        bank.PinChanged += (in PinChange e) =>
        {
            if (!first) return;
            first = false;
            firstEvent = e;
        };

        mcu.Run(1_000);

        first.Should().BeFalse();
        firstEvent.IsDriven(TogglePin).Should().BeTrue();
    }

    [Fact]
    public void Removing_the_last_subscriber_unhooks_the_machine()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        mcu.Machine.Sio.OnGpioChanged.Should().BeNull();

        PinChangeHandler handler = (in PinChange _) => { };
        bank.PinChanged += handler;
        mcu.Machine.Sio.OnGpioChanged.Should().NotBeNull();

        bank.PinChanged -= handler;
        mcu.Machine.Sio.OnGpioChanged.Should().BeNull("an idle bank costs the SIO write path nothing");
    }

    [Fact]
    public void A_wide_write_arrives_as_one_batch()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        for (var pin = 2; pin <= 4; pin++)
            mcu.Machine.IoBank0.WriteWord((uint)(pin * 8 + 4), 5);
        var events = new List<PinChange>();
        bank.PinChanged += (in PinChange e) => events.Add(e);

        mcu.Machine.Bus.WriteWord(0xD0000024, 0b11100);     // GPIO_OE_SET, three pads at once

        events.Should().ContainSingle();
        events[0].Changed.Should().Be(0b11100);
        events[0].OutputEnable.Should().Be(0b11100);
    }

    [Fact]
    public void An_output_register_under_an_undriven_pad_is_not_a_change()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        mcu.Machine.IoBank0.WriteWord(6 * 8 + 4, 5);
        var count = 0;
        bank.PinChanged += (in PinChange _) => count++;

        mcu.Machine.Bus.WriteWord(0xD0000014, 1 << 6);      // GPIO_OUT_SET with OE clear

        count.Should().Be(0);
    }

    // ── GPIO: circuit side ───────────────────────────────────────────────

    [Fact]
    public void SetInputs_drives_only_the_masked_pins()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        var sio = mcu.Machine.Sio;

        bank.SetInputs(1UL << 5, 1UL << 5);
        (sio.GpioIn & (1u << 5)).Should().NotBe(0);

        bank.SetInputs(1UL << 6, 0);                         // bit 5 is outside the mask
        (sio.GpioIn & (1u << 5)).Should().NotBe(0, "pin 5 was not part of the second call");
        (sio.GpioIn & (1u << 6)).Should().Be(0);

        bank.SetInputs((1UL << 5) | (1UL << 6), 1UL << 6);   // flip both in one call
        (sio.GpioIn & (1u << 5)).Should().Be(0);
        (sio.GpioIn & (1u << 6)).Should().NotBe(0);
    }

    [Fact]
    public void ReleaseInputs_returns_the_pad_to_its_pull()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        var sio = mcu.Machine.Sio;
        mcu.Machine.PadsBank0.WriteWord(4 + 5 * 4, (1 << 6) | (1 << 3));    // pull-up on GPIO5

        bank.SetInputs((1UL << 5) | (1UL << 7), 1UL << 7);
        (sio.GpioIn & (1u << 5)).Should().Be(0, "driven low from outside against the pull-up");
        (sio.GpioIn & (1u << 7)).Should().NotBe(0);

        bank.ReleaseInputs(1UL << 5);
        (sio.GpioIn & (1u << 5)).Should().NotBe(0, "released, the pull-up wins");
        (sio.GpioIn & (1u << 7)).Should().NotBe(0, "pin 7 was not released");

        bank.ReleaseInputs(1UL << 7);
        (sio.GpioIn & (1u << 7)).Should().Be(0, "no pull on pin 7, so it floats low");
    }

    [Fact]
    public void ReleaseInputs_latches_the_edge_for_gpio_interrupts()
    {
        using var mcu = new RP2040Mcu();
        var bank = GpioBank(mcu);
        mcu.Machine.PadsBank0.WriteWord(4 + 5 * 4, (1 << 6) | (1 << 3));
        bank.SetInputs(1UL << 5, 0);
        mcu.Machine.Bus.WriteWord(0x400140F0 + 0, 0xFFFFFFFF);     // INTR0: clear latched edges

        bank.ReleaseInputs(1UL << 5);

        // INTR0 holds 4 bits per pin; pin 5 starts at bit 20, EDGE_HIGH is its bit 3.
        (mcu.Machine.Bus.ReadWord(0x400140F0) & (1u << (5 * 4 + 3))).Should().NotBe(0);
    }

    [Fact]
    public void Released_pins_are_untouched_when_nobody_drove_them()
    {
        using var mcu = Booted();
        var bank = GpioBank(mcu);
        mcu.Run(5_000);

        bank.ReleaseInputs(TogglePinBit);

        bank.OutputEnableMask.Should().Be(TogglePinBit, "release is about the circuit side only");
    }

    // ── ADC ──────────────────────────────────────────────────────────────

    private static ushort Sample(RP2040Mcu mcu, int channel)
    {
        var bus = mcu.Machine.Bus;
        bus.WriteWord(0x4004C000, 1u | (uint)(channel << 12) | (1u << 2));    // EN, AINSEL, START_ONCE
        return (ushort)bus.ReadWord(0x4004C004);
    }

    [Fact]
    public void Adc_converts_volts_to_twelve_bit_codes_with_clamping()
    {
        using var mcu = new RP2040Mcu();
        mcu.ReferenceVolts.Should().Be(3.3);
        var volts = 0.0;
        mcu.ReadChannelVolts = ch => ch == 2 ? volts : 0.0;

        foreach (var (v, code) in new[] { (0.0, 0), (1.65, 2048), (3.3, 4095), (5.0, 4095), (-1.0, 0), (double.NaN, 0) })
        {
            volts = v;
            Sample(mcu, 2).Should().Be((ushort)code, $"{v} V");
        }

        Sample(mcu, 0).Should().Be(0, "other channels report what the callback says");
    }

    [Fact]
    public void Adc_callback_can_be_replaced_and_removed()
    {
        using var mcu = new RP2040Mcu();
        mcu.ReadChannelVolts = _ => 3.3;
        Sample(mcu, 1).Should().Be(4095);

        mcu.ReadChannelVolts = null;
        mcu.ReadChannelVolts.Should().BeNull();
        Sample(mcu, 1).Should().Be(0);
    }

    // ── PWM ──────────────────────────────────────────────────────────────

    [Fact]
    public void Pwm_reports_frequency_and_duty_of_a_routed_pin()
    {
        using var mcu = new RP2040Mcu();
        var pwm = (IPwmSource)mcu;
        pwm.TryGetPwm(0, 16, out _).Should().BeFalse("nothing routes GPIO16 to PWM yet");

        mcu.Machine.IoBank0.WriteWord(16 * 8 + 4, 4);
        pwm.TryGetPwm(0, 16, out var stopped).Should().BeTrue();
        stopped.Enabled.Should().BeFalse("routed but the slice is not running");

        mcu.Machine.Pwm.WriteWord(0x04, 1 << 4);        // DIV = 1.0
        mcu.Machine.Pwm.WriteWord(0x10, 999);           // TOP
        mcu.Machine.Pwm.WriteWord(0x0C, 250 | (750 << 16));
        mcu.Machine.Pwm.WriteWord(0x00, 1);             // EN

        pwm.TryGetPwm(0, 16, out var a).Should().BeTrue();
        a.Enabled.Should().BeTrue();
        a.FrequencyHz.Should().BeApproximately(125_000, 0.01);
        a.Duty.Should().BeApproximately(0.25, 1e-9);

        mcu.Machine.IoBank0.WriteWord(17 * 8 + 4, 4);
        pwm.TryGetPwm(0, 17, out var b).Should().BeTrue();
        b.Duty.Should().BeApproximately(0.75, 1e-9, "GPIO17 is channel B of the same slice");
    }

    [Fact]
    public void Pwm_honours_divider_inversion_and_phase_correct_mode()
    {
        using var mcu = new RP2040Mcu();
        var pwm = (IPwmSource)mcu;
        mcu.Machine.IoBank0.WriteWord(18 * 8 + 4, 4);                // slice 1, channel A
        mcu.Machine.Pwm.WriteWord(0x14 + 0x04, (2 << 4) | 8);        // DIV = 2.5
        mcu.Machine.Pwm.WriteWord(0x14 + 0x10, 99);
        mcu.Machine.Pwm.WriteWord(0x14 + 0x0C, 25);
        mcu.Machine.Pwm.WriteWord(0x14 + 0x00, 1 | (1 << 2));        // EN + A_INV

        pwm.TryGetPwm(0, 18, out var info).Should().BeTrue();
        info.FrequencyHz.Should().BeApproximately(125e6 / 2.5 / 100, 1e-6);
        info.Duty.Should().BeApproximately(0.75, 1e-9, "inverted");

        mcu.Machine.Pwm.WriteWord(0x14 + 0x00, 1 | (1 << 1));       // EN + PH_CORRECT
        pwm.TryGetPwm(0, 18, out info).Should().BeTrue();
        info.FrequencyHz.Should().BeApproximately(125e6 / 2.5 / 200, 1e-6, "phase-correct counts up and down");
    }

    [Fact]
    public void Pwm_rejects_unknown_banks_and_pins()
    {
        using var mcu = new RP2040Mcu();
        var pwm = (IPwmSource)mcu;

        pwm.TryGetPwm(1, 0, out _).Should().BeFalse();
        pwm.TryGetPwm(0, 30, out _).Should().BeFalse();
        pwm.TryGetPwm(0, -1, out _).Should().BeFalse();
    }

    // ── UART ─────────────────────────────────────────────────────────────

    [Fact]
    public void Uart_reports_transmitted_bytes_and_accepts_received_ones()
    {
        using var mcu = new RP2040Mcu();
        var map = (IPeripheralMap)mcu;
        map.Uarts.Should().HaveCount(2);
        var uart0 = map.Uarts[0];
        uart0.Index.Should().Be(0);
        map.Uarts[1].Index.Should().Be(1);

        var sent = new List<byte>();
        Action<byte> onTx = sent.Add;
        uart0.TxByte += onTx;
        mcu.Machine.Bus.WriteWord(0x40034000, 0x48);
        mcu.Machine.Bus.WriteWord(0x40034000, 0x69);
        sent.Should().Equal(0x48, 0x69);

        uart0.TxByte -= onTx;
        mcu.Machine.Bus.WriteWord(0x40034000, 0x21);
        sent.Should().HaveCount(2);

        uart0.TryInjectRx(0xA5).Should().BeTrue();
        (mcu.Machine.Bus.ReadWord(0x40034000) & 0xFF).Should().Be(0xA5);
    }

    [Fact]
    public void Uart_receive_fifo_drops_bytes_when_full()
    {
        using var mcu = new RP2040Mcu();
        var uart = ((IPeripheralMap)mcu).Uarts[1];

        for (var i = 0; i < 32; i++) uart.TryInjectRx((byte)i).Should().BeTrue();
        uart.TryInjectRx(0xFF).Should().BeFalse();
    }

    [Fact]
    public void Uart_baud_rate_comes_from_the_divisors()
    {
        using var mcu = new RP2040Mcu();
        var uart = ((IPeripheralMap)mcu).Uarts[0];
        uart.BaudRate.Should().Be(0, "not configured");

        mcu.Machine.Bus.WriteWord(0x40034024, 67);          // UARTIBRD
        mcu.Machine.Bus.WriteWord(0x40034028, 52);          // UARTFBRD
        mcu.Machine.Bus.WriteWord(0x40034030, 1 | (1 << 8) | (1 << 9));     // UARTEN, TXE, RXE

        uart.BaudRate.Should().BeInRange(115_000u, 115_400u);
    }

    // ── SPI ──────────────────────────────────────────────────────────────

    [Fact]
    public void Spi_transfer_callback_sees_each_frame()
    {
        using var mcu = new RP2040Mcu();
        var spi = ((IPeripheralMap)mcu).Spis[0];
        spi.Index.Should().Be(0);
        var bus = mcu.Machine.Bus;
        bus.WriteWord(0x4003C000, 7);                       // SSPCR0: 8-bit frames
        bus.WriteWord(0x4003C004, 2);                       // SSPCR1: SSE
        spi.FrameBits.Should().Be(8);

        var seen = new List<uint>();
        spi.Transfer = mosi =>
        {
            seen.Add(mosi);
            return mosi ^ 0xFF;
        };
        bus.WriteWord(0x4003C008, 0x3C);

        seen.Should().Equal(0x3Cu);
        (bus.ReadWord(0x4003C008) & 0xFF).Should().Be(0xC3);
        spi.Transfer.Should().NotBeNull();
    }

    [Fact]
    public void Spi_with_no_transfer_reads_all_ones()
    {
        using var mcu = new RP2040Mcu();
        var spi = ((IPeripheralMap)mcu).Spis[1];
        var bus = mcu.Machine.Bus;
        bus.WriteWord(0x40040000, 7);
        bus.WriteWord(0x40040004, 2);

        bus.WriteWord(0x40040008, 0x12);
        (bus.ReadWord(0x40040008) & 0xFF).Should().Be(0xFF, "an idle bus floats high");

        spi.Transfer = _ => 0x00;
        spi.Transfer = null;
        bus.WriteWord(0x40040008, 0x12);
        (bus.ReadWord(0x40040008) & 0xFF).Should().Be(0xFF, "removing the callback idles the bus again");
    }

    [Fact]
    public void Spi_frame_width_follows_the_configuration()
    {
        using var mcu = new RP2040Mcu();
        var spi = ((IPeripheralMap)mcu).Spis[0];

        mcu.Machine.Bus.WriteWord(0x4003C000, 15);
        spi.FrameBits.Should().Be(16);
        mcu.Machine.Bus.WriteWord(0x4003C000, 3);
        spi.FrameBits.Should().Be(4);
    }

    // ── I2C ──────────────────────────────────────────────────────────────

    private sealed class RecordingTarget(byte readValue = 0x77, bool ack = true) : II2cTarget
    {
        public readonly List<string> Log = [];

        public bool Probe(byte addr7, bool write)
        {
            Log.Add($"probe {addr7:X2} {(write ? "w" : "r")} {(ack ? "ack" : "nack")}");
            return ack;
        }

        public void Write(byte data) => Log.Add($"write {data:X2}");

        public byte Read()
        {
            Log.Add("read");
            return readValue;
        }

        public void Stop() => Log.Add("stop");
    }

    private const uint I2c0Base = 0x40044000;
    private const uint IcTar = I2c0Base + 0x04, IcData = I2c0Base + 0x10, IcEnable = I2c0Base + 0x6C, IcRawIntr = I2c0Base + 0x34;

    private static void EnableMaster(RP2040Mcu mcu, byte addr)
    {
        mcu.Machine.Bus.WriteWord(IcEnable, 0);
        mcu.Machine.Bus.WriteWord(IcTar, addr);
        mcu.Machine.Bus.WriteWord(IcEnable, 1);
    }

    [Fact]
    public void I2c_target_sees_a_write_transaction()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        port.Index.Should().Be(0);
        var target = new RecordingTarget();
        port.AttachTarget(0x3C, target);
        EnableMaster(mcu, 0x3C);

        mcu.Machine.Bus.WriteWord(IcData, 0x00);
        mcu.Machine.Bus.WriteWord(IcData, 0xAF | (1u << 9));    // last byte carries STOP

        target.Log.Should().Equal("probe 3C w ack", "write 00", "write AF", "stop");
    }

    [Fact]
    public void I2c_target_serves_reads_and_restarts_on_direction_change()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        var target = new RecordingTarget(readValue: 0x5A);
        port.AttachTarget(0x48, target);
        EnableMaster(mcu, 0x48);

        mcu.Machine.Bus.WriteWord(IcData, 0x01);                        // write the register pointer
        mcu.Machine.Bus.WriteWord(IcData, (1u << 8));                   // repeated START, read
        mcu.Machine.Bus.WriteWord(IcData, (1u << 8) | (1u << 9));       // read with STOP

        target.Log.Should().Equal("probe 48 w ack", "write 01", "stop", "probe 48 r ack", "read", "read", "stop");
        mcu.Machine.Bus.ReadWord(I2c0Base + 0x10).Should().Be(0x5A);
    }

    [Fact]
    public void I2c_without_a_target_nacks_the_address()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        port.AttachTarget(0x10, new RecordingTarget());
        EnableMaster(mcu, 0x22);

        mcu.Machine.Bus.WriteWord(IcData, 0x00 | (1u << 9));

        (mcu.Machine.Bus.ReadWord(IcRawIntr) & (1u << 6)).Should().NotBe(0, "TX_ABRT is raised");
    }

    [Fact]
    public void I2c_target_can_refuse_the_address()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        var target = new RecordingTarget(ack: false);
        port.AttachTarget(0x50, target);
        EnableMaster(mcu, 0x50);

        mcu.Machine.Bus.WriteWord(IcData, 0x00);

        target.Log.Should().Equal("probe 50 w nack");
        (mcu.Machine.Bus.ReadWord(IcRawIntr) & (1u << 6)).Should().NotBe(0);
    }

    [Fact]
    public void I2c_detaching_the_last_target_restores_the_bare_bus()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        var target = new RecordingTarget();
        port.AttachTarget(0x3C, target);
        port.DetachTarget(0x3C);
        EnableMaster(mcu, 0x3C);

        mcu.Machine.Bus.WriteWord(IcData, 0x00);

        target.Log.Should().BeEmpty();
        (mcu.Machine.Bus.ReadWord(IcRawIntr) & (1u << 6)).Should().NotBe(0);
        mcu.Machine.I2c0.OnWrite.Should().BeNull();
    }

    [Fact]
    public void I2c_ports_are_independent()
    {
        using var mcu = new RP2040Mcu();
        var map = (IPeripheralMap)mcu;
        var t0 = new RecordingTarget();
        var t1 = new RecordingTarget();
        map.I2cs[0].AttachTarget(0x20, t0);
        map.I2cs[1].AttachTarget(0x20, t1);

        mcu.Machine.Bus.WriteWord(0x40048000 + 0x6C, 0);
        mcu.Machine.Bus.WriteWord(0x40048000 + 0x04, 0x20);
        mcu.Machine.Bus.WriteWord(0x40048000 + 0x6C, 1);
        mcu.Machine.Bus.WriteWord(0x40048000 + 0x10, 0x99 | (1u << 9));

        t0.Log.Should().BeEmpty();
        t1.Log.Should().Equal("probe 20 w ack", "write 99", "stop");
    }

    [Fact]
    public void I2c_external_master_writes_to_the_firmware_slave()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        port.SlaveAddress.Should().Be(0);
        var announced = new List<byte>();
        port.SlaveAddressChanged += announced.Add;

        mcu.Machine.Bus.WriteWord(IcEnable, 0);
        mcu.Machine.Bus.WriteWord(I2c0Base + 0x08, 0x08);        // IC_SAR
        mcu.Machine.Bus.WriteWord(I2c0Base + 0x00, 0);           // IC_CON: slave, 7-bit
        mcu.Machine.Bus.WriteWord(IcEnable, 1);

        announced.Should().Equal(0x08);
        port.SlaveAddress.Should().Be(0x08);
        port.BeginMasterTransfer(0x09, write: true).Should().BeFalse("not our address");
        port.BeginMasterTransfer(0x08, write: true).Should().BeTrue();
        port.MasterWrite(0xC4).Should().BeTrue();
        port.MasterStop();

        (mcu.Machine.Bus.ReadWord(IcData) & 0xFF).Should().Be(0xC4);
    }

    [Fact]
    public void I2c_external_master_stops_writing_when_the_rx_fifo_is_full()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        mcu.Machine.Bus.WriteWord(I2c0Base + 0x08, 0x08);
        port.BeginMasterTransfer(0x08, write: true).Should().BeTrue();

        for (var i = 0; i < 16; i++) port.MasterWrite((byte)i).Should().BeTrue();
        port.MasterWrite(0xEE).Should().BeFalse();
    }

    [Fact]
    public void I2c_external_master_reads_what_the_firmware_queued()
    {
        using var mcu = new RP2040Mcu();
        var port = ((IPeripheralMap)mcu).I2cs[0];
        mcu.Machine.Bus.WriteWord(I2c0Base + 0x08, 0x08);
        port.BeginMasterTransfer(0x08, write: false).Should().BeTrue();

        port.TryMasterRead(out _).Should().BeFalse("firmware has not answered RD_REQ yet");
        mcu.Machine.Bus.WriteWord(IcEnable, 1);
        mcu.Machine.Bus.WriteWord(IcData, 0x6E);                 // firmware answers
        port.TryMasterRead(out var data).Should().BeTrue();
        data.Should().Be(0x6E);
        port.TryMasterRead(out _).Should().BeFalse();
        port.MasterStop();
    }

    // ── Ownership ────────────────────────────────────────────────────────

    [Fact]
    public void Wrapping_an_existing_machine_leaves_it_alive_after_dispose()
    {
        using var machine = new RP2040Machine();
        var mcu = new RP2040Mcu(machine);
        var bank = GpioBank(mcu);
        bank.PinChanged += (in PinChange _) => { };

        mcu.Dispose();

        machine.Sio.OnGpioChanged.Should().BeNull("dispose unhooks the machine");
        machine.Run(100);                                   // still usable
        mcu.Machine.Should().BeSameAs(machine);
    }
}

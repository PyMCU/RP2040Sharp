using Xunit.Abstractions;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The pin-level SPI master, UART and I2C master with deferred ticks, sleeping stretches, bounded awake runs and the
/// event-bounded guard against the plain slices (<see cref="Differential.Check"/>): the same lock-step host (MOSI wired
/// to MISO, TX to RX, an I2C slave), the same call returns, pad events and cycles, host inputs and cycles, SRAM (what
/// firmware received, by polling, DMA or an interrupt handler), both cores and the SPI / UART / I2C / DMA registers.
/// </summary>
public class PinLevelDifferentialTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var name in PinLevelScenarios.Catalog.Keys)
            foreach (var guard in new uint[] { 0, 1, 4, 75, 300 })
                yield return [name, guard];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Same_result_as_plain_slices(string name, uint guard)
    {
        var sc = PinLevelScenarios.Catalog[name]();
        var uf2 = Firmware.Uf2(sc.Image);
        IReadOnlyList<string>? received = null;
        var probe = Differential.Check(run =>
        {
            Assert.True(Abi.LoadUf2(run.H, uf2) > 0);
            var bus = new BusHost(run.H, run.Host);
            sc.Wire(bus);
            while (run.Cycles < sc.Span)
            {
                if (guard == 0) run.Run(20_000);
                else run.RunUntilEdge(20_000, guard, sc.Watch);
                bus.Answer();
            }
            run.Calls.AddRange(bus.Inputs.Select(i => $"in {i.Gpio}={i.Level} @{i.Cycle}"));
            received = PinLevelScenarios.Received(sc, run.Chip);
        }, flags: Abi.BusAll);
        output.WriteLine($"{name} guard {guard}: stretched {probe.Stretched}, bounded {probe.Bounded}, plain {probe.Plain}");
        // A host answering once per call is on time at guard 1 and with the event guard; a plain run answers late.
        if (guard != 0) Assert.Equal(sc.Expected(null!), received);
    }
}

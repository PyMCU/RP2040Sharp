using System; using System.IO; using RP2040.TestKit.Boards;
namespace RP2040Sharp.IntegrationTests.Tests;
/// FreeRTOS ported to PyMCU Python -- ALL subsystems at once: priority preemptive
/// scheduling, FIFO queue (strict order), counting semaphore, software timer with
/// callback, task notifications and event groups. Each must progress, the queue
/// stays in strict FIFO order, no lockup.
[Trait("Category","Integration")]
public class FreeRtosKernelTest {
    private const string Bin="/Users/begeistert/Repos/pymcu-nanort/dist/firmware.bin";
    [Fact(Skip="Requires a locally-built PyMCU/nanoRT firmware at the hard-coded Bin path; not available in CI. Remove Skip after `pymcu build`.")]
    public void Full_Kernel_AllSubsystems() {
        using var pico=new PicoSimulation(withUsbCdc:false);
        pico.LoadFlash(File.ReadAllBytes(Bin));
        for(int i=0;i<1000;i++) pico.RunInstructions(5000);
        var b=pico.Cpu.Bus;
        uint qgot=b.ReadWord(0x20025060), qerr=b.ReadWord(0x20025064), sgot=b.ReadWord(0x20025070);
        uint tmr=b.ReadWord(0x20025074), nt=b.ReadWord(0x20025078), ev=b.ReadWord(0x2002507C);
        Assert.False(pico.Cpu.IsLockedUp, "no lockup");
        Assert.True(qgot>10, $"queue FIFO items (got={qgot})");
        Assert.Equal(0u, qerr);
        Assert.True(sgot>5, $"semaphore takes (sgot={sgot})");
        Assert.True(tmr>5, $"timer callbacks (tmr={tmr})");
        Assert.True(nt>5, $"notifications (nt={nt})");
        Assert.True(ev>3, $"event-group waits (ev={ev})");
    }
}

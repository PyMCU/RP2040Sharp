namespace RP2040.NanoFramework.TestKit;

/// <summary>
/// Thrown when a deployed assembly's <c>.pe</c> declares a native-methods checksum that the flashed
/// firmware does not provide. This is the classic nanoFramework footgun: if a library's InternalCall
/// surface changes, its <c>.pe</c> checksum changes and the old firmware can no longer bind its
/// natives — the app then fails to run in a confusing way. The guard turns that into a clear error.
/// </summary>
public sealed class NanoChecksumMismatchException : Exception
{
    public string Assembly { get; }
    public uint DeploymentChecksum { get; }
    public uint FirmwareChecksum { get; }

    public NanoChecksumMismatchException(string assembly, uint deploymentChecksum, uint firmwareChecksum)
        : base($"Native checksum mismatch for '{assembly}': the deployment .pe declares " +
               $"0x{deploymentChecksum:X8} but the firmware provides 0x{firmwareChecksum:X8}. " +
               $"Rebuild the nanoCLR firmware for this assembly, or use a .pe that matches the firmware.")
    {
        Assembly = assembly;
        DeploymentChecksum = deploymentChecksum;
        FirmwareChecksum = firmwareChecksum;
    }
}

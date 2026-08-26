namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// SHA-256 + size pin for one cached firmware image, keyed by its cache-file stem
/// (e.g. "micropython-v1.21.0", "circuitpython-9.2.1", "micropython-picow-v1.21.0").
/// </summary>
public sealed record FirmwarePin(string Sha256, long SizeBytes);

/// <summary>
/// The versioned manifest of firmware images <see cref="FirmwareCache"/> downloads. Every hash was
/// computed from a real GET of the pinned build; a cached/downloaded file whose SHA-256 does not match
/// is rejected loudly (upstream reissued the build, or the cache is corrupt) rather than booted silently.
///
/// A version that is not listed here downloads unverified (kept for forward-compatibility when a test
/// bumps to a new version before its hash is pinned) — pin it here to get verification.
/// </summary>
public static class FirmwareManifest
{
    private static readonly Dictionary<string, FirmwarePin> Pins = new()
    {
        // MicroPython, RPI_PICO board — https://micropython.org/download/RPI_PICO/
        ["micropython-v1.19.1"] = new("958ad98a21a036a529c0b17bad1e0223e80cdd4b089cd5596d14966760ab3c3f", 609792),
        ["micropython-v1.20.0"] = new("d9d97d8b495da476006125e73dc203a428ce7b6be27a5a76eda1dd85b2efe99d", 638464),
        ["micropython-v1.21.0"] = new("a1166281fd87886e5d755e577e8eaf207881e20dac1d76f82161f37133547be3", 636928),
        // MicroPython, RPI_PICO_W board (bundles the CYW43 WLAN/BT firmware).
        ["micropython-picow-v1.21.0"] = new("1c7deb8409da29974e8cfb06d3798fd137ea682ca0785e5aec84bdbf0c017c96", 1604608),
        ["micropython-picow-v1.28.0"] = new("a0210c9c8a085391cb66f530c298a5a4fb804a9d072289254c24df5fdf210f7a", 1749504),
        // CircuitPython, raspberry_pi_pico board — https://circuitpython.org/board/raspberry_pi_pico/
        ["circuitpython-9.2.1"] = new("b23e50784711101d6fd9958778f5541ea0781e9eb317dfe1d775959e80512bd6", 1769472),
    };

    // Official RPI_PICO_W MicroPython UF2 (per version) for the CYW43 tests, which need the wireless build.
    private static readonly Dictionary<string, string> PicoWUrls = new()
    {
        ["v1.21.0"] = "https://micropython.org/resources/firmware/RPI_PICO_W-20231005-v1.21.0.uf2",
        ["v1.28.0"] = "https://micropython.org/resources/firmware/RPI_PICO_W-20260406-v1.28.0.uf2",
    };

    /// <summary>The pin for a cache-file stem, or null if that image is not pinned (downloads unverified).</summary>
    public static FirmwarePin? PinFor(string cacheKey) => Pins.GetValueOrDefault(cacheKey);

    /// <summary>The canonical RPI_PICO_W UF2 URL for a MicroPython version, or null if none is known.</summary>
    public static string? PicoWUrl(string version) => PicoWUrls.GetValueOrDefault(version);
}

using System.Security.Cryptography;

namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// Downloads and caches MicroPython and CircuitPython UF2 firmware images.
/// Firmware is stored in a local cache directory so subsequent test runs are offline-capable.
///
/// Images listed in <see cref="FirmwareManifest"/> are verified by SHA-256 on every use: a cached or
/// freshly-downloaded file whose hash does not match is deleted and a hard error is raised (upstream
/// reissued the build, or the cache is corrupt) — never a silent skip that would boot the wrong bytes.
/// </summary>
public static class FirmwareCache
{
    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "rp2040sharp-firmware-cache");

    /// <summary>
    /// Returns the local path to the MicroPython UF2 image for <paramref name="version"/>
    /// (e.g. "v1.21.0"), downloading it from GitHub Releases if not already cached.
    /// Returns <c>null</c> if the download fails (network unavailable, etc.).
    /// </summary>
    public static async Task<string?> GetMicroPythonAsync(string version)
    {
        Directory.CreateDirectory(CacheDir);

        var key = $"micropython-{version}";
        var path = Path.Combine(CacheDir, $"{key}.uf2");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return VerifyOrThrow(path, key);

        // Prefer firmware embedded in the test assembly — offline and free of network flakiness.
        if (TryLoadEmbedded(key) is { } embedded)
        {
            await File.WriteAllBytesAsync(path, embedded);
            return VerifyOrThrow(path, key);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RP2040Sharp-IntegrationTests/1.0");

            // Resolve the exact filename (includes build date) from the download index
            var url = await ResolveMicroPythonUrlAsync(http, version);
            if (url is null) return null;

            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return VerifyOrThrow(path, key);
        }
        catch
        {
            // Network unavailable or release doesn't exist — tests will be skipped
            if (File.Exists(path))
                File.Delete(path);
            return null;
        }
    }

    /// <summary>
    /// Loads a firmware UF2 embedded in this test assembly (under Firmware/python/), matched by a
    /// substring of its filename (e.g. "micropython-v1.21.0" or "circuitpython-9.2.1"). Matching by
    /// substring avoids depending on MSBuild's exact manifest-resource name mangling.
    /// Returns <c>null</c> when no embedded firmware matches (caller falls back to downloading).
    /// </summary>
    private static byte[]? TryLoadEmbedded(string fileNameContains)
    {
        var asm = typeof(FirmwareCache).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase)
                                 && n.Contains(fileNameContains, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<string?> ResolveMicroPythonUrlAsync(HttpClient http, string version)
    {
        // Firmware listed at https://micropython.org/download/RPI_PICO/
        // Entries look like: /resources/firmware/RPI_PICO-{date}-{version}.uf2
        var page = await http.GetStringAsync("https://micropython.org/download/RPI_PICO/");
        // Filenames are RPI_PICO-{date}-v{semver}.uf2 — keep the v prefix
        var tag = version.StartsWith('v') ? version : "v" + version;
        const string needle = "/resources/firmware/RPI_PICO-";
        var search = $"-{tag}.uf2";  // e.g. "-v1.21.0.uf2" (no trailing quote — rel slice excludes it)

        var start = page.IndexOf(needle, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = page.IndexOf('"', start + 1);
            if (end < 0) break;
            var rel = page[start..end];
            if (rel.EndsWith(search, StringComparison.OrdinalIgnoreCase))
                return "https://micropython.org" + rel;
            start = page.IndexOf(needle, start + 1, StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>
    /// Returns the local path to the CircuitPython UF2 image for <paramref name="version"/>
    /// (e.g. "9.2.1"), downloading it from downloads.circuitpython.org if not already cached.
    /// Returns <c>null</c> if the download fails (network unavailable, etc.).
    /// </summary>
    public static async Task<string?> GetCircuitPythonAsync(string version)
    {
        Directory.CreateDirectory(CacheDir);

        var key = $"circuitpython-{version}";
        var path = Path.Combine(CacheDir, $"{key}.uf2");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return VerifyOrThrow(path, key);

        // Prefer firmware embedded in the test assembly — offline and free of network flakiness.
        var embeddedTag = version.StartsWith('v') ? version[1..] : version;
        if (TryLoadEmbedded($"circuitpython-{embeddedTag}") is { } embedded)
        {
            await File.WriteAllBytesAsync(path, embedded);
            return VerifyOrThrow(path, key);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RP2040Sharp-IntegrationTests/1.0");

            // Direct stable URL — no scraping needed; version has no 'v' prefix
            var tag = version.StartsWith('v') ? version[1..] : version;
            var url = $"https://downloads.circuitpython.org/bin/raspberry_pi_pico/en_US/" +
                      $"adafruit-circuitpython-raspberry_pi_pico-en_US-{tag}.uf2";

            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return VerifyOrThrow(path, key);
        }
        catch
        {
            // Network unavailable or release doesn't exist — tests will be skipped
            if (File.Exists(path))
                File.Delete(path);
            return null;
        }
    }

    /// <summary>
    /// Returns the local path to the MicroPython firmware if already cached, without attempting
    /// a download. Useful for offline CI environments where firmware is pre-seeded.
    /// </summary>
    public static string? GetCachedPath(string version)
    {
        var path = Path.Combine(CacheDir, $"micropython-{version}.uf2");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>
    /// Returns the local path to the CircuitPython firmware if already cached, without
    /// attempting a download.
    /// </summary>
    public static string? GetCachedCircuitPythonPath(string version)
    {
        var tag = version.StartsWith('v') ? version[1..] : version;
        var path = Path.Combine(CacheDir, $"circuitpython-{tag}.uf2");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>
    /// Returns the local path to the wireless RPI_PICO_W MicroPython UF2 for <paramref name="version"/>
    /// (bundles the CYW43 WLAN/BT firmware the Pico-W tests need), downloading it from the pinned
    /// <see cref="FirmwareManifest.PicoWUrl"/> if not cached. Returns <c>null</c> when the version has no
    /// known URL or the download fails (offline). Throws on a SHA-256 mismatch.
    /// </summary>
    public static async Task<string?> GetMicroPythonPicoWAsync(string version)
    {
        Directory.CreateDirectory(CacheDir);

        var key = $"micropython-picow-{version}";
        var path = Path.Combine(CacheDir, $"{key}.uf2");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return VerifyOrThrow(path, key);

        var url = FirmwareManifest.PicoWUrl(version);
        if (url is null) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RP2040Sharp-IntegrationTests/1.0");
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return VerifyOrThrow(path, key);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (File.Exists(path)) File.Delete(path);
            return null;
        }
    }

    /// <summary>
    /// Verifies a cached file against its <see cref="FirmwareManifest"/> pin. Unpinned images pass through
    /// unchecked (forward-compatibility). A mismatch deletes the bad copy and throws — it must be loud, not
    /// a silent skip, because the file is not the build the tests were written against.
    /// </summary>
    private static string VerifyOrThrow(string path, string cacheKey)
    {
        var pin = FirmwareManifest.PinFor(cacheKey);
        if (pin is null) return path; // not pinned — download unverified

        var actual = Sha256OfFile(path);
        if (!actual.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException(
                $"Firmware upstream changed for '{cacheKey}': expected SHA-256 {pin.Sha256} but got " +
                $"{actual}. Re-pin the hash/size in FirmwareManifest after reviewing the new build " +
                "(deleted the bad cache copy).");
        }
        return path;
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

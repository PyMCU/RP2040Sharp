using System.Security.Cryptography;

namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// Downloads and caches a third-party source file the suite runs on the guest, pinned to an exact
/// commit and verified by SHA-256 — the same contract <see cref="FirmwareCache"/> applies to the UF2
/// images, and for the same two reasons: the repository does not carry code it did not write, and a
/// test always runs against the exact bytes it was written for.
///
/// <para>Returns null when the file cannot be fetched (offline), so callers skip cleanly. Throws when
/// the bytes do not match the pin: that means upstream moved, which must be visible rather than
/// silently absorbed.</para>
/// </summary>
public static class PinnedAsset
{
    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "rp2040sharp-asset-cache");

    /// <summary>
    /// umqtt.simple — the MQTT client from micropython-lib (MIT), pinned to the commit below.
    /// </summary>
    public static Task<string?> UmqttSimpleAsync() => GetAsync(
        name: "umqtt_simple.py",
        url: "https://raw.githubusercontent.com/micropython/micropython-lib/" +
             "5e49b1bd41d312d9d2a8e4f19d7bd6a918896abc/micropython/umqtt.simple/umqtt/simple.py",
        sha256: "3cfd101ef774e0c6f9543425650ddc36168a7bbf4b8b8b71667e27023dfd951f");

    /// <summary>Text of a pinned asset, or null when it cannot be fetched.</summary>
    public static async Task<string?> GetAsync(string name, string url, string sha256)
    {
        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, name);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RP2040Sharp-IntegrationTests/1.0");
                await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;   // offline — caller skips
            }
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException(
                $"Pinned asset '{name}' changed upstream: expected SHA-256 {sha256} but got {actual} " +
                $"from {url}. Review the new content and re-pin.");
        }
        return await File.ReadAllTextAsync(path);
    }
}

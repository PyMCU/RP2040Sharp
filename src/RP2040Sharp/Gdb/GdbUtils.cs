// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
using System.Text;

namespace RP2040.Gdb;

/// <summary>
/// Helpers for the GDB Remote Serial Protocol: hex encoding/decoding, checksums and
/// packet framing. Ported from rp2040js (src/gdb/gdb-utils.ts).
/// </summary>
public static class GdbUtils
{
    public static string EncodeHexByte(byte value)
    {
        Span<char> chars = stackalloc char[2];
        chars[0] = HexDigit(value >> 4);
        chars[1] = HexDigit(value & 0xF);
        return new string(chars);
    }

    public static string EncodeHexBuf(ReadOnlySpan<byte> buf)
    {
        var sb = new StringBuilder(buf.Length * 2);
        foreach (var b in buf)
        {
            sb.Append(HexDigit(b >> 4));
            sb.Append(HexDigit(b & 0xF));
        }
        return sb.ToString();
    }

    /// <summary>Encode a 32-bit value as 8 hex chars in little-endian byte order.</summary>
    public static string EncodeHexUint32(uint value)
    {
        Span<byte> bytes =
        [
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF),
        ];
        return EncodeHexBuf(bytes);
    }

    public static byte[] DecodeHexBuf(string encoded)
    {
        var result = new byte[encoded.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)((HexValue(encoded[i * 2]) << 4) | HexValue(encoded[i * 2 + 1]));
        return result;
    }

    /// <summary>Decode 8 little-endian hex chars into a 32-bit value.</summary>
    public static uint DecodeHexUint32(string encoded)
    {
        var buf = DecodeHexBuf(encoded);
        uint value = 0;
        for (var i = 0; i < buf.Length && i < 4; i++)
            value |= (uint)buf[i] << (i * 8);
        return value;
    }

    public static string GdbChecksum(string text)
    {
        var sum = 0;
        foreach (var c in text)
            sum += c;
        return EncodeHexByte((byte)(sum & 0xFF));
    }

    public static string GdbMessage(string value) => $"${value}#{GdbChecksum(value)}";

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}

// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RP2040.Core;

/// <summary>
/// Strict/pedantic mode — a sink for the emulator's <b>silent gaps</b>: the places it would otherwise
/// return 0, no-op, or fall back to a benign default for something it does not actually model
/// (an unmapped peripheral, a reserved PIO instruction encoding, an unhandled register, …).
///
/// <para>A passing test proves an observed behaviour over an exercised path; it cannot prove the emulator
/// is complete. The dangerous failures are the <i>silent</i> ones — the "we never noticed the PIO bug"
/// class — where an unmodelled path quietly returns a plausible value instead of complaining. This turns
/// every such spot into a recorded event, so the unverified surface becomes a visible, measurable list.</para>
///
/// <para>Off by default (zero cost). Enable with <c>EMU_STRICT=1</c>; <c>EMU_STRICT_THROW=1</c> throws on the
/// first gap; an aggregated report is written to <c>EMU_STRICT_OUT</c> (default <c>/tmp/emu_strict_rp2040.txt</c>)
/// on process exit. Hook new gap sites by calling <see cref="Note"/> / <see cref="NoteRet{T}"/> at each
/// "default / return 0 / no-op" arm.</para>
/// </summary>
public static class EmuStrict
{
    public static bool Enabled;
    public static bool ThrowOnGap;

    private static readonly object Lock = new();
    private static readonly Dictionary<string, long> Counts = new();
    private static readonly Dictionary<string, string> Sample = new();
    private static long _sinceFlush;

    static EmuStrict()
    {
        Enabled = Environment.GetEnvironmentVariable("EMU_STRICT") == "1";
        ThrowOnGap = Environment.GetEnvironmentVariable("EMU_STRICT_THROW") == "1";
        if (Enabled)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }

    /// <summary>Record that the emulator hit an unmodelled/default path. <paramref name="category"/> is the
    /// gap class (e.g. "pio.wait.src-reserved"); <paramref name="what"/> discriminates instances within it
    /// (e.g. "src=3" or "0x40010000"). No-op unless strict mode is enabled.</summary>
    public static void Note(string category, string what)
    {
        if (!Enabled) return;
        var key = category + "  " + what;
        bool flush;
        lock (Lock)
        {
            bool isNew = !Counts.ContainsKey(key);
            Counts.TryGetValue(key, out var c);
            Counts[key] = c + 1;
            Sample.TryAdd(key, what);
            // Flush so the report is on disk regardless of how the test host exits (vstest kills it without a
            // graceful ProcessExit). Always flush on a newly-seen gap so the *list* stays complete; otherwise
            // throttle to every 256 hits so a hot-loop gap can't thrash the file.
            flush = isNew || (++_sinceFlush & 0xFF) == 0;
        }
        if (flush) Dump();
        if (ThrowOnGap)
            throw new NotImplementedException($"[emu-strict] {category}: {what}");
    }

    /// <summary>Convenience for switch-expression default arms: records the gap and returns the fallback,
    /// so an arm like <c>_ =&gt; 0</c> becomes <c>_ =&gt; EmuStrict.NoteRet("...", $"src={s}", 0u)</c>.</summary>
    public static T NoteRet<T>(string category, string what, T fallback)
    {
        Note(category, what);
        return fallback;
    }

    public static void Reset()
    {
        lock (Lock) { Counts.Clear(); Sample.Clear(); }
    }

    /// <summary>Write the aggregated gap report (one line per distinct gap, busiest first).</summary>
    public static void Dump(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("EMU_STRICT_OUT") ?? "/tmp/emu_strict_rp2040.txt";
        lock (Lock)
        {
            using var w = new StreamWriter(path, append: false);
            w.WriteLine($"# emu-strict gap report ({DateTime.Now:u})");
            w.WriteLine($"# {Counts.Count} distinct silent-gap sites, {Counts.Values.Sum()} total hits");
            w.WriteLine($"# {"hits",10}  category  /  example");
            foreach (var kv in Counts.OrderByDescending(k => k.Value))
                w.WriteLine($"{kv.Value,12}  {kv.Key}");
        }
    }
}

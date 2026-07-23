// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

namespace RP2040Sharp.GdbHost;

/// <summary>
/// Minimal PIO instruction decoder, enough to make an SM's current instruction readable in a
/// register dump. Follows the encoding in the RP2040 datasheet §3.4.
///
/// Delay/side-set share bits 12:8 and their split depends on SIDESET_COUNT, which lives in
/// PINCTRL. Rather than guess, this renders the raw field as <c>[ds=N]</c> when non-zero.
/// </summary>
internal static class PioDisassembler
{
    public static string Decode(ushort instr)
    {
        var op = (instr >> 13) & 0x7;
        var ds = (instr >> 8) & 0x1F;      // delay and/or side-set, split per PINCTRL
        var suffix = ds != 0 ? $" [ds={ds}]" : "";

        var body = op switch
        {
            0 => Jmp(instr),
            1 => Wait(instr),
            2 => In(instr),
            3 => Out(instr),
            4 => (instr & 0x80) != 0 ? Pull(instr) : Push(instr),
            5 => Mov(instr),
            6 => Irq(instr),
            7 => Set(instr),
            _ => "???",
        };

        return body + suffix;
    }

    private static string Jmp(ushort i)
    {
        var cond = (i >> 5) & 0x7;
        var addr = i & 0x1F;
        var c = cond switch
        {
            0 => "",
            1 => "!x, ",
            2 => "x--, ",
            3 => "!y, ",
            4 => "y--, ",
            5 => "x!=y, ",
            6 => "pin, ",
            7 => "!osre, ",
            _ => "",
        };
        return $"jmp {c}{addr}";
    }

    private static string Wait(ushort i)
    {
        var pol = (i >> 7) & 1;
        var src = (i >> 5) & 0x3;
        var idx = i & 0x1F;
        var s = src switch { 0 => "gpio", 1 => "pin", 2 => "irq", _ => "?" };
        return $"wait {pol} {s} {idx}";
    }

    private static string In(ushort i)
    {
        var src = (i >> 5) & 0x7;
        var cnt = i & 0x1F;
        var s = src switch
        {
            0 => "pins", 1 => "x", 2 => "y", 3 => "null",
            6 => "isr", 7 => "osr", _ => "?",
        };
        return $"in {s}, {(cnt == 0 ? 32 : cnt)}";
    }

    private static string Out(ushort i)
    {
        var dst = (i >> 5) & 0x7;
        var cnt = i & 0x1F;
        var d = dst switch
        {
            0 => "pins", 1 => "x", 2 => "y", 3 => "null",
            4 => "pindirs", 5 => "pc", 6 => "isr", 7 => "exec", _ => "?",
        };
        return $"out {d}, {(cnt == 0 ? 32 : cnt)}";
    }

    private static string Push(ushort i)
    {
        var ifFull = (i >> 6) & 1;
        var block = (i >> 5) & 1;
        return $"push{(ifFull != 0 ? " iffull" : "")}{(block != 0 ? " block" : " noblock")}";
    }

    private static string Pull(ushort i)
    {
        var ifEmpty = (i >> 6) & 1;
        var block = (i >> 5) & 1;
        return $"pull{(ifEmpty != 0 ? " ifempty" : "")}{(block != 0 ? " block" : " noblock")}";
    }

    private static string Mov(ushort i)
    {
        var dst = (i >> 5) & 0x7;
        var op = (i >> 3) & 0x3;
        var src = i & 0x7;
        var d = dst switch
        {
            0 => "pins", 1 => "x", 2 => "y", 4 => "exec",
            5 => "pc", 6 => "isr", 7 => "osr", _ => "?",
        };
        var s = src switch
        {
            0 => "pins", 1 => "x", 2 => "y", 3 => "null",
            5 => "status", 6 => "isr", 7 => "osr", _ => "?",
        };
        var o = op switch { 1 => "~", 2 => "::", _ => "" };
        return $"mov {d}, {o}{s}";
    }

    private static string Irq(ushort i)
    {
        var clr = (i >> 6) & 1;
        var wait = (i >> 5) & 1;
        var idx = i & 0x1F;
        var mode = clr != 0 ? "clear " : wait != 0 ? "wait " : "";
        return $"irq {mode}{idx}";
    }

    private static string Set(ushort i)
    {
        var dst = (i >> 5) & 0x7;
        var data = i & 0x1F;
        var d = dst switch { 0 => "pins", 1 => "x", 2 => "y", 4 => "pindirs", _ => "?" };
        return $"set {d}, {data}";
    }
}

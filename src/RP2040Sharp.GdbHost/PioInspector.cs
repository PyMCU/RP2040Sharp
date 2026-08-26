// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
using System.Text;
using RP2040.Peripherals.Pio;

namespace RP2040Sharp.GdbHost;

/// <summary>
/// Renders a PIO block's memory-mapped registers as a human-readable dump.
///
/// Everything is read through <see cref="PioPeripheral.ReadWord"/> — the same path the
/// firmware takes — so what you see is what the CPU would see, including the read-back-zero
/// behaviour while the block is held in reset. Offsets mirror the RP2040 datasheet §3.7.
/// </summary>
internal static class PioInspector
{
    private const uint CTRL   = 0x000;
    private const uint FSTAT  = 0x004;
    private const uint FDEBUG = 0x008;
    private const uint FLEVEL = 0x00C;
    private const uint IRQ    = 0x030;

    private const uint INSTR_MEM_BASE = 0x048;
    private const uint SM_BASE        = 0x0C8;
    private const uint SM_STRIDE      = 0x018;

    // Offsets within one state machine's register block.
    private const uint SM_CLKDIV    = 0x00;
    private const uint SM_EXECCTRL  = 0x04;
    private const uint SM_SHIFTCTRL = 0x08;
    private const uint SM_ADDR      = 0x0C;
    private const uint SM_INSTR     = 0x10;
    private const uint SM_PINCTRL   = 0x14;

    public static string Dump(PioPeripheral pio, int block, bool includeInstrMem)
    {
        var sb = new StringBuilder();
        var baseAddr = block == 0 ? 0x50200000u : 0x50300000u;

        sb.AppendLine($"PIO{block} @ 0x{baseAddr:X8}{(pio.InReset ? "   [HELD IN RESET — all registers read 0]" : "")}");

        var ctrl   = pio.ReadWord(CTRL);
        var fstat  = pio.ReadWord(FSTAT);
        var fdebug = pio.ReadWord(FDEBUG);
        var flevel = pio.ReadWord(FLEVEL);
        var irq    = pio.ReadWord(IRQ);

        sb.AppendLine($"  CTRL   0x{ctrl:X8}  SM_ENABLE={Nibble(ctrl, 0)} SM_RESTART={Nibble(ctrl, 4)} CLKDIV_RESTART={Nibble(ctrl, 8)}");
        sb.AppendLine($"  FSTAT  0x{fstat:X8}  RXFULL={Nibble(fstat, 0)} RXEMPTY={Nibble(fstat, 8)} TXFULL={Nibble(fstat, 16)} TXEMPTY={Nibble(fstat, 24)}");
        sb.AppendLine($"  FDEBUG 0x{fdebug:X8}  RXSTALL={Nibble(fdebug, 0)} RXUNDER={Nibble(fdebug, 8)} TXOVER={Nibble(fdebug, 16)} TXSTALL={Nibble(fdebug, 24)}");
        sb.AppendLine($"  FLEVEL 0x{flevel:X8}  " + string.Join("  ", Enumerable.Range(0, 4)
            .Select(sm => $"SM{sm}:TX={(flevel >> (sm * 8)) & 0xF},RX={(flevel >> (sm * 8 + 4)) & 0xF}")));
        sb.AppendLine($"  IRQ    0x{irq:X8}  flags={Convert.ToString(irq & 0xFF, 2).PadLeft(8, '0')}");

        for (var sm = 0; sm < 4; sm++)
        {
            var b = SM_BASE + (uint)sm * SM_STRIDE;
            var clkdiv    = pio.ReadWord(b + SM_CLKDIV);
            var execctrl  = pio.ReadWord(b + SM_EXECCTRL);
            var shiftctrl = pio.ReadWord(b + SM_SHIFTCTRL);
            var addr      = pio.ReadWord(b + SM_ADDR);
            var instr     = pio.ReadWord(b + SM_INSTR);
            var pinctrl   = pio.ReadWord(b + SM_PINCTRL);

            var enabled = (ctrl & (1u << sm)) != 0;
            sb.AppendLine();
            sb.AppendLine($"  SM{sm} {(enabled ? "ENABLED " : "disabled")}  PC={addr:D2}  INSTR=0x{instr:X4}  {PioDisassembler.Decode((ushort)instr)}");
            sb.AppendLine($"    CLKDIV    0x{clkdiv:X8}  int={clkdiv >> 16} frac={(clkdiv >> 8) & 0xFF}");
            sb.AppendLine($"    EXECCTRL  0x{execctrl:X8}  wrap={(execctrl >> 7) & 0x1F}..{(execctrl >> 12) & 0x1F} jmp_pin={(execctrl >> 24) & 0x1F} " +
                          $"side_en={(execctrl >> 30) & 1} side_pindir={(execctrl >> 29) & 1} status_sel={(execctrl >> 4) & 1} status_n={execctrl & 0xF}");
            sb.AppendLine($"    SHIFTCTRL 0x{shiftctrl:X8}  autopush={Bit(shiftctrl, 16)} autopull={Bit(shiftctrl, 17)} " +
                          $"in_shiftdir={(Bit(shiftctrl, 18) == 1 ? "right" : "left")} out_shiftdir={(Bit(shiftctrl, 19) == 1 ? "right" : "left")} " +
                          $"push_thresh={Thresh(shiftctrl, 20)} pull_thresh={Thresh(shiftctrl, 25)} join_tx={Bit(shiftctrl, 31)} join_rx={Bit(shiftctrl, 30)}");
            sb.AppendLine($"    PINCTRL   0x{pinctrl:X8}  out_base={pinctrl & 0x1F} out_count={(pinctrl >> 20) & 0x3F} " +
                          $"set_base={(pinctrl >> 5) & 0x1F} set_count={(pinctrl >> 26) & 0x7} " +
                          $"in_base={(pinctrl >> 15) & 0x1F} sideset_base={(pinctrl >> 10) & 0x1F} sideset_count={(pinctrl >> 29) & 0x7}");
        }

        if (includeInstrMem)
        {
            sb.AppendLine();
            sb.AppendLine("  INSTR_MEM:");
            for (var i = 0; i < 32; i++)
            {
                var w = (ushort)pio.ReadWord(INSTR_MEM_BASE + (uint)i * 4);
                sb.AppendLine($"    [{i,2}] 0x{w:X4}  {PioDisassembler.Decode(w)}");
            }
        }

        return sb.ToString();
    }

    private static uint Nibble(uint v, int shift) => (v >> shift) & 0xF;
    private static uint Bit(uint v, int shift) => (v >> shift) & 1;

    // Thresholds encode 32 as 0.
    private static uint Thresh(uint v, int shift)
    {
        var t = (v >> shift) & 0x1F;
        return t == 0 ? 32 : t;
    }
}

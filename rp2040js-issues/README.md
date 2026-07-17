# Candidate upstream issues for rp2040js

Drafts for issues to file against [rp2040js](https://github.com/wokwi/rp2040js) (Uri Shaked),
the project RP2040Sharp / RP2350Sharp were ported from.

## Method (and a caveat)

These were sourced from the C# ports' "fix" commits and `rp2040js`-referencing comments, **then each
candidate was verified against the actual rp2040js 1.3.2 source** before being written up. That
verification step matters: most apparent "divergences" between the C# port and rp2040js turned out
**not** to be upstream bugs. They were either:

- bugs the C# port introduced itself and later fixed (rp2040js was correct all along), or
- internal representation/convention differences that are functionally equivalent
  (e.g. the port counts OSR bits *remaining* where rp2040js counts bits *shifted* — `OsrCount > 0`
  and `outputShiftCount < pullThreshold` express the same `!OSRE` condition).

Examples that were investigated and **dismissed** as upstream bugs (rp2040js is correct):
JMP PIN (reads `inputValue`), JMP `!OSRE`, IN/OUT shift-by-32 (handled via the `bitCount === 0`
special case — JS bit-ops are mod-32 too, but the encoding never passes a literal 32), autopull
timing (already checked at the start of OUT), autopush stall (ISR value is preserved via
`wait(..., inputShiftReg)` and re-pushed on wake), PUSH/PULL no-block, exception-return
`eventRegistered`, `IC_COMP_PARAM_1` (returns 0), PWM phase-correct start direction
(`countingUp = true` default + `TimerMode.ZigZag`).

## Confirmed issues

| File | Component | Confidence |
|---|---|---|
| `adc-rrobin-ainsel-mask.md` | ADC `activeChannel` setter masks with `CS_AINSEL_SHIFT` not `CS_AINSEL_MASK` | **High** — one-line typo, root-caused in source |

## Finding more, properly

The reliable way to surface genuine upstream bugs is **differential testing**, not diffing the port's
comments: run rp2040js and a reference (QEMU `mps2-an385` Cortex-M0, or real Pico 1 silicon) on the
same firmware/instruction stream and compare register/memory state — the same technique used to
validate RP2350Sharp bit-for-bit against QEMU. That campaign is the recommended next step if more
upstream issues are wanted; the comment-mining approach has a high false-positive rate (≈1 real bug
in ~20 candidates here).

# ADC: `activeChannel` setter masks with `CS_AINSEL_SHIFT` instead of `CS_AINSEL_MASK` (breaks round-robin / corrupts AINSEL)

**Component:** `src/peripherals/adc.ts`
**Version checked:** 1.3.2 (also present on current `main` at time of writing)

## Summary

The private `activeChannel` setter writes the channel number into `CS.AINSEL` using the wrong
constant: it ANDs the channel with `CS_AINSEL_SHIFT` (the bit *position*, `12`) instead of
`CS_AINSEL_MASK` (the field *mask*, `0x7`). This corrupts the stored channel for most values, so
round-robin conversion advances to the wrong channel (or gets stuck), and any internal update of
`AINSEL` reads back a wrong channel.

## Location / root cause

```ts
// src/peripherals/adc.ts
const CS_AINSEL_MASK = 0x7;     // line 20
const CS_AINSEL_SHIFT = 12;     // line 21

private set activeChannel(channel: number) {
  this.cs &= ~(CS_AINSEL_MASK << CS_AINSEL_SHIFT);
  this.cs |= (channel & CS_AINSEL_SHIFT) << CS_AINSEL_SHIFT;   // line 151  ← BUG
}
```

`channel & CS_AINSEL_SHIFT` is `channel & 12` (`0b1100`), not `channel & 0x7`. For the valid
channel range 0–4 this yields:

| channel in | `channel & 12` (stored) |
|---|---|
| 0 | 0 |
| 1 | 0 |
| 2 | 0 |
| 3 | 0 |
| 4 | 4 |

So setting the active channel to 1, 2 or 3 silently stores **0**.

## Impact

The round-robin sampler computes the next channel correctly but then writes it through this
setter:

```ts
// src/peripherals/adc.ts ~line 213
const round = (this.cs >> CS_RROBIN_SHIFT) & CS_RROBIN_MASK;
if (round) {
  let channel = this.activeChannel + 1;
  while (!(round & (1 << channel))) {
    channel = (channel + 1) % this.numChannels;
  }
  this.activeChannel = channel;   // <-- corrupted by the buggy setter
}
```

Because the setter drops the low channel bits, `AINSEL` never lands on channels 1–3, so:
- ADC round-robin does not visit channels 1, 2, 3 as configured.
- `CS.AINSEL` read back by firmware after an internal update is wrong.
- The conversion result comes from the wrong input.

(The matching getter on line 146 is correct — it uses `CS_AINSEL_MASK` — which is why the typo is
easy to miss.)

## Suggested fix

```ts
private set activeChannel(channel: number) {
  this.cs &= ~(CS_AINSEL_MASK << CS_AINSEL_SHIFT);
  this.cs |= (channel & CS_AINSEL_MASK) << CS_AINSEL_SHIFT;   // mask, not shift
}
```

## How it was found

Surfaced while building a C# RP2040 emulator (an independent re-implementation): an ADC round-robin
test visited only channel 0 and channel 4. Tracing the divergence to the `AINSEL` field led to the
`& CS_AINSEL_SHIFT` typo in the upstream setter, confirmed against the 1.3.2 source above.

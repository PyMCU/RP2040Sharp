"""
Functional check: late producer with autopull.

SM0 runs OUT PINS,8 with autopull (pull_thresh=8): with an empty TX FIFO it immediately
stalls waiting for data.  The byte is written via sm0.put() ONLY AFTER SM0 is already
parked on the autopull stall.  The OUT must drive the pins once the data arrives.

We read the 8 output pins back directly to reconstruct the byte that PIO drove (the pins
hold their last driven value while SM0 re-stalls on the next autopull).

Expected output: "late: 0x3c"
"""
import rp2
from machine import Pin
import time

@rp2.asm_pio(out_init=[rp2.PIO.OUT_LOW] * 8,
             out_shiftdir=rp2.PIO.SHIFT_RIGHT,
             autopull=True, pull_thresh=8)
def out8():
    out(pins, 8)

sm0 = rp2.StateMachine(0, out8, freq=2_000_000, out_base=Pin(0))
sm0.active(1)

# Let SM0 reach the autopull stall (TX FIFO is empty) before producing any data.
time.sleep_ms(5)

# Late producer: feed the byte only now, after SM0 is already parked on the stalled OUT.
sm0.put(0x3C)
time.sleep_ms(5)

# Reconstruct the byte driven onto pins 0..7.
val = 0
for i in range(8):
    val |= Pin(i, Pin.IN).value() << i
print("late:", hex(val))   # expected: 0x3c

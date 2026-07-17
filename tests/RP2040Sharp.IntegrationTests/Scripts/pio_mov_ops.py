"""
Verify the PIO MOV operators (bitwise invert and bit-reverse) against real MicroPython.

  PULL                 — load a word from the TX FIFO into the OSR
  MOV ISR, ~OSR / rev  — apply the operator on the way into the ISR
  PUSH                 — return the result via the RX FIFO

Expected output:
  inv: 0xffff0000      (~0x0000ffff)
  rev: 0x80000000      (bit-reverse of 0x00000001)
"""
import rp2
import time

@rp2.asm_pio()
def invert_word():
    pull(block)
    mov(isr, invert(osr))
    push(block)

@rp2.asm_pio()
def reverse_word():
    pull(block)
    mov(isr, reverse(osr))
    push(block)

sm = rp2.StateMachine(0, invert_word)
sm.active(1)
sm.put(0x0000_FFFF)
time.sleep_ms(2)
print("inv:", hex(sm.get()))    # expected: 0xffff0000
sm.active(0)

sm2 = rp2.StateMachine(1, reverse_word)
sm2.active(1)
sm2.put(0x0000_0001)
time.sleep_ms(2)
print("rev:", hex(sm2.get()))   # expected: 0x80000000
sm2.active(0)

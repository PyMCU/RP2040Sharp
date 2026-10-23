import glob, os, re, sys
import xml.etree.ElementTree as ET

svd, hdrdir, out, sdk = sys.argv[1:5]
root = ET.parse(svd).getroot()
version = root.findtext('version', '').strip()

def text(e, k, d=None):
    x = e.find(k)
    return x.text.strip() if x is not None and x.text else d

H = {}
for f in glob.glob(os.path.join(hdrdir, '*.h')):
    for m in re.finditer(r'#define (\w+)_(BITS|RESET|ACCESS)\s+(.*)', open(f).read()):
        H.setdefault(m.group(1), {})[m.group(2)] = m.group(3).strip().strip('"')
PREFIX = {'PLL_SYS': 'PLL', 'PLL_USB': 'PLL', 'UART0': 'UART', 'UART1': 'UART', 'SPI0': 'SPI', 'SPI1': 'SPI',
          'I2C0': 'I2C', 'I2C1': 'I2C', 'PIO0': 'PIO', 'PIO1': 'PIO', 'XIP_SSI': 'SSI', 'XIP_CTRL': 'XIP',
          'PPB': 'M0PLUS', 'USBCTRL_REGS': 'USB', 'USBCTRL_DPRAM': 'USB_DEVICE_DPRAM'}

def header(p, r, f):
    pre = PREFIX.get(p, p)
    h = H.get(f'{pre}_{r}_{f}')
    if h is None:
        c = [k for k in H if k.endswith(f'_{r}_{f}')]
        h = H[c[0]] if len(c) == 1 else None
    if h is None and f == r:
        h = H.get(f'{pre}_{r}')
    return h or {}

periphs = {text(p, 'name'): p for p in root.iter('peripheral')}
lines = []
for name, p in periphs.items():
    src = periphs[p.get('derivedFrom')] if p.get('derivedFrom') else p
    base = int(text(p, 'baseAddress'), 0)
    for r in src.iter('register'):
        rn = text(r, 'name')
        cov = ro = rw = wc = und = 0
        side = r.find('readAction') is not None
        for f in r.iter('field'):
            br = text(f, 'bitRange')
            if br:
                msb, lsb = map(int, br.strip('[]').split(':'))
            else:
                lsb = int(text(f, 'bitOffset')); msb = lsb + int(text(f, 'bitWidth')) - 1
            mask = ((1 << (msb - lsb + 1)) - 1) << lsb
            h = header(name, rn, text(f, 'name'))
            cov |= mask
            side |= f.find('readAction') is not None
            if h.get('RESET') == '-': und |= mask
            if text(f, 'modifiedWriteValues') == 'oneToClear' or h.get('ACCESS') == 'WC': wc |= mask
            elif text(f, 'access', text(r, 'access', 'read-write')) == 'read-only': ro |= mask
            elif h.get('ACCESS') == 'RW': rw |= mask
        lines.append(f'{name}.{rn} {base + int(text(r, "addressOffset"), 0):08X} {int(text(r, "resetValue", "0"), 0):08X} '
                     f'{cov:08X} {ro:08X} {rw:08X} {wc:08X} {und:08X} {int(side)}')

with open(out, 'w') as o:
    o.write(f'# Derived from src/rp2040/hardware_regs/RP2040.svd (version {version}) and hardware_regs/include of '
            f'pico-sdk {sdk}.\n')
    o.write('# Copyright (c) 2020-2024 Raspberry Pi (Trading) Ltd. BSD-3-Clause; see LICENSE-pico-sdk.txt.\n')
    o.write('# register address reset fields read-only read-write write-1-to-clear undefined-reset read-side-effect\n')
    o.write('\n'.join(lines) + '\n')

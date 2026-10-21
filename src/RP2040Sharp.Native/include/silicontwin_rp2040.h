/*
 * silicontwin_rp2040.h — C ABI for the Silicon Twin RP2040 core.
 *
 * A register-accurate RP2040 (Raspberry Pi Pico, Pico W) that a host simulator drives in lock-step:
 * the host owns time, advances the chip by a number of instructions, reads the pads, and drives
 * inputs and analog voltages back in. Both Cortex-M0+ cores run; core 1 once firmware launches it.
 * With ST_RP2040_CYW43 the CYW43439 Wi-Fi/BT chip of the Pico W is attached to its pads, which
 * MicroPython and CircuitPython for the Pico W need to boot. With the pin-level buses (ST_RP2040_BUS_*,
 * the default of st_rp2040_create) the hardware UART, SPI and I2C blocks drive and sample the pads.
 *
 * Same conventions as silicontwin_rp2350.h (see docs/native-abi.md for the differences).
 *
 * Threading: a handle is single-threaded. Callbacks fire synchronously from inside
 * st_rp2040_run() / st_rp2040_run_until_edge(), on the caller's thread. Creating and destroying
 * chips is safe from any thread.
 *
 * Error model (the library never terminates the host process on bad input or an internal error):
 *   - A handle is an opaque token, not a pointer. NULL, never-issued, destroyed or double-destroyed
 *     handles are detected and rejected: queries return 0, setters do nothing, st_rp2040_status
 *     returns ST_RP2040_STATUS_INVALID_HANDLE. st_rp2040_destroy on them is a no-op.
 *   - Invalid arguments (NULL buffers, huge lengths, gpio / channel / flag out of range, a UF2 for
 *     another chip) are rejected without touching the chip; the reason is in st_rp2040_last_error.
 *   - Any internal exception (a bug in the emulated core) puts the chip in a sticky FAULTED state:
 *     no more emulation, every later call is a no-op returning a safe default (st_rp2040_run returns
 *     the last cycle count). st_rp2040_status then returns ST_RP2040_STATUS_FAULTED and st_rp2040_last_error
 *     says what happened (exception type, message, guest PC). Destroy the chip to clean up.
 *   - Not covered: stack overflow and out-of-memory kills cannot be caught by the runtime.
 *   Check st_rp2040_status after every st_rp2040_run / st_rp2040_run_until_edge.
 */
#ifndef SILICONTWIN_RP2040_H
#define SILICONTWIN_RP2040_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct st_rp2040 st_rp2040;

/* Type and constant names carry the chip (st_rp2040_*, ST_RP2040_*) so that one translation unit can
 * include this header and silicontwin_rp2350.h together. Values and callback signatures are the same
 * as there, so one host callback serves both chips. */
typedef enum {
    ST_RP2040_SERIAL_UART0   = 0,
    ST_RP2040_SERIAL_UART1   = 1,
    ST_RP2040_SERIAL_USB_CDC = 2  /* an emulated USB host enumerates the device's CDC ACM port (the MicroPython REPL) */
} st_rp2040_serial_channel;

/* Flags of st_rp2040_create_ex, OR-ed. The values are those of silicontwin_rp2350.h (ST_BUS_*, ST_RP2350_CYW43), so a
 * host passes the same bits to both chips.
 *
 * Pin-level buses: with a block listed, once firmware routes its function to pads (IO_BANK0 FUNCSEL SPI / UART / I2C)
 * the block generates the real waveform on those pads and reads them back, so a host that only sees pads gets
 * machine.SPI / machine.I2C / machine.UART working, not only SoftI2C / SoftSPI and PIO. SPI and I2C are masters; I2C
 * lines are open-drain (they need a pull-up, external or the pad's). A block whose pads firmware never routes keeps
 * its internal behaviour (UART TX bytes still reach the serial callback, routed or not). */
enum {
    ST_RP2040_NONE     = 0,
    ST_RP2040_BUS_I2C  = 1,
    ST_RP2040_BUS_SPI  = 2,
    ST_RP2040_BUS_UART = 4,
    ST_RP2040_BUS_ALL  = 7,
    /* Pico W: attach the CYW43439 to GPIO23 (WL_ON), 24 (DATA / IRQ), 25 (CS) and 29 (CLK), driven by
     * the firmware's PIO gSPI. Without it a Pico W image waits for the chip forever. (0x100 since the
     * pin-level buses; it was 1.) */
    ST_RP2040_CYW43    = 0x100
};

/* A pad's effective output changed. level/oe are 0 or 1; cycle is the core-0 cycle of the change
 * (convert with st_rp2040_cycle_to_ps). A change made by a CPU instruction (SIO, IO_BANK0 or PADS
 * register write, on either core) carries the cycle of that instruction, whatever the slice size; one
 * made by a pin-level UART / SPI / I2C engine the cycle of its edge; one made by another peripheral (PIO,
 * PWM counter, DMA) the end of the slice in which it happened. */
typedef void (*st_rp2040_pin_cb)(void *user, int gpio, int level, int output_enable, uint64_t cycle);

/* Bytes the chip transmitted on a serial channel. */
typedef void (*st_rp2040_serial_cb)(void *user, int channel, const uint8_t *data, size_t len);

/* Result of st_rp2040_status. */
enum {
    ST_RP2040_STATUS_OK             = 0,
    ST_RP2040_STATUS_FAULTED        = 1, /* the chip hit an internal error and is inert (sticky) */
    ST_RP2040_STATUS_INVALID_HANDLE = 2  /* NULL, unknown, destroyed or garbage handle */
};

const char *st_rp2040_version(void);
uint32_t    st_rp2040_clock_hz(void); /* clk_sys: 125 MHz (the core does not model clock changes) */

/* Returns NULL on failure (flash_bytes not 0 and not a power of two in 4096..16 MiB, unknown flags,
 * out of memory...); st_rp2040_last_error(NULL) then describes why. flash_bytes = 0 selects 2 MiB
 * (Pico, Pico W). st_rp2040_create(f) is st_rp2040_create_ex(f, ST_RP2040_BUS_ALL): this ABI only
 * exposes pads, so a block that never reaches a pad would be unreachable for the host. Pass
 * ST_RP2040_NONE for transaction-level blocks (no pad moves; UART TX bytes reach the serial callback). */
st_rp2040 *st_rp2040_create(uint32_t flash_bytes);
st_rp2040 *st_rp2040_create_ex(uint32_t flash_bytes, uint32_t flags);
/* Releases the chip. NULL, unknown or already destroyed handles are ignored. Destroying a chip from
 * inside one of its own callbacks is allowed: it is released when the running call returns. */
void       st_rp2040_destroy(st_rp2040 *chip);

/* 0 = OK, 1 = FAULTED, 2 = INVALID_HANDLE (ST_RP2040_STATUS_*). Cheap: call it after every run. */
int st_rp2040_status(st_rp2040 *chip);

/* Short single-line ASCII text of the last error on this chip: the fault that made it inert, or else
 * the last rejected argument (a rejection never overwrites a fault message). "" if there was none.
 * Never NULL. For chip == NULL it returns the reason the last st_rp2040_create failed; for an invalid
 * handle, a fixed text. The pointer is valid until the next call on this chip or its destruction. */
const char *st_rp2040_last_error(st_rp2040 *chip);

/* Load a UF2 image into flash and point core 0 at it (as the bootrom would). Returns the number of
 * blocks loaded, or -1 (invalid handle, faulted chip, NULL data, len above 256 MiB, an image that does
 * not fit the flash, or a UF2 for another chip: any block whose family id is set and is not RP2040
 * 0xE48BFF56, e.g. an RP2350 image, rejects the whole file). Malformed blocks, blocks outside flash
 * (SRAM-only images are not supported) and "not main flash" blocks are skipped, so empty or corrupt
 * input returns 0 and leaves the chip untouched. len is trusted to describe the buffer. */
int st_rp2040_load_uf2(st_rp2040 *chip, const uint8_t *data, size_t len);

/* Execute `instructions` per core, then report pad changes. Returns core-0 cycles (the last known
 * count if the chip is faulted or the handle invalid: check st_rp2040_status). Peripherals advance
 * every 512 instructions at most. Calling run from inside a callback is rejected. */
uint64_t st_rp2040_run(st_rp2040 *chip, uint32_t instructions);
uint64_t st_rp2040_cycles(st_rp2040 *chip);

/* Like st_rp2040_run, for hosts whose circuit answers within a bit time (I2C slaves...).
 * Runs up to `max_instructions` in slices of `guard_instructions` whenever watch and guard are both
 * nonzero, including before firmware routes the watched pads, and stops after the slice in which a
 * watched pad's level or OE changed. While both cores sleep (WFI/WFE) a slice is twice the guard.
 * Slices never exceed 512 instructions (the peripheral quantum). Peripherals advance at the end of
 * every slice, before the pads are checked. With pin-level buses and a guard above 1 a slice of core 0
 * also ends before the first instruction at or past the next edge a pin-level engine makes on a watched
 * pad (st_rp2040_cycles_until_edge), and right after an instruction that moved a watched pad: the call
 * returns where guard 1 would have after each such edge (event-bounded guard), so a circuit's answer
 * (an SPI MISO bit, an I2C ACK) is in place before the engine samples it. Pad changes are reported
 * through the pin callback, in cycle order, before returning. watch = 0 or guard_instructions = 0 makes
 * it identical to st_rp2040_run. Returns core-0 cycles. */
uint64_t st_rp2040_run_until_edge(st_rp2040 *chip, uint32_t max_instructions,
                                  uint32_t guard_instructions, uint32_t watch);

/* Core-0 cycles from now until the next edge a pin-level engine (SPI master, UART transmitter, I2C
 * master) will make on a pad in `watch`, if no input changes first; UINT64_MAX when none has one coming
 * (idle engines, unwatched pads, edges only firmware or an input can start, no pin-level buses) or for
 * an invalid or faulted handle. A host that waits for the circuit after an edge can wait this long at
 * most: the chip's next sample of an engine input comes with that edge. Convert with
 * st_rp2040_cycle_to_ps. */
uint64_t st_rp2040_cycles_until_edge(st_rp2040 *chip, uint32_t watch);

/* Time. The cycle counter counts core-0 clk_sys cycles at a fixed 125 MHz.
 *   clk_sys_hz     current clk_sys (125 MHz).
 *   time_ps        simulated time since creation in picoseconds.
 *   cycle_to_ps    the same for an earlier cycle count, e.g. the `cycle` of a pin callback. */
uint32_t st_rp2040_clk_sys_hz(st_rp2040 *chip);
uint64_t st_rp2040_time_ps(st_rp2040 *chip);
uint64_t st_rp2040_cycle_to_ps(st_rp2040 *chip, uint64_t cycle);

/* Guest instructions retired so far, as a conservative lower bound: slices in which a core slept
 * (WFI/WFE), was locked up or (core 1) was not launched are not counted.
 * core = 0 or 1 selects one core; any other value (e.g. -1) returns the sum of both. */
uint64_t st_rp2040_instructions(st_rp2040 *chip, int core);

/* Pads (gpio 0..29; anything else is rejected and returns 0). Returns the output level;
 * *output_enable receives OE (may be NULL; it is set to 0 on rejection). */
int  st_rp2040_pin_get(st_rp2040 *chip, int gpio, int *output_enable);
/* level 0 / 1 drives the pad from outside. level -1 releases it (nobody drives the pad): it reads
 * high if the firmware enabled its pull-up, low with the pull-down, and follows later pull changes by
 * itself. gpio outside 0..29 or a level other than -1 / 0 / 1 is rejected. The cycles already run see
 * the old level: peripheral time still pending is applied first. */
void st_rp2040_pin_set_input(st_rp2040 *chip, int gpio, int level);

/* Pad pull configuration (PADS_BANK0): bit 0 = pull-up enabled (PUE), bit 1 = pull-down enabled
 * (PDE); 0 = none. GPIO pads reset with the pull-down enabled. */
int  st_rp2040_pin_get_pull(st_rp2040 *chip, int gpio);

/* Analog: drive 0..3.3 V onto ADC channel 0..3 (GPIO26..29). Other channels are rejected, NaN counts
 * as 0 V. The temperature sensor (channel 4) reads 27 degrees C. */
void st_rp2040_adc_set_voltage(st_rp2040 *chip, int channel, double volts);

/* Pass cb = NULL to disable (also from inside a callback). */
void st_rp2040_set_pin_callback(st_rp2040 *chip, st_rp2040_pin_cb cb, void *user);
void st_rp2040_set_serial_callback(st_rp2040 *chip, st_rp2040_serial_cb cb, void *user);

/* Host -> chip bytes on the USB CDC (MicroPython REPL) or a UART RX. NULL data, len above 64 MiB or a
 * channel other than st_rp2040_serial_channel are rejected; len 0 does nothing. */
void st_rp2040_serial_write(st_rp2040 *chip, st_rp2040_serial_channel channel, const uint8_t *data, size_t len);

/* ---- Wi-Fi (Pico W, ST_RP2040_CYW43) --------------------------------------------------------------------------
 * A chip created with ST_RP2040_CYW43 speaks the CYW43439's gSPI host protocol to a virtual network (the radio firmware
 * the guest downloads is parked, never executed; no RF, no chip internals): one access point the host configures here, and a gateway at 192.168.4.1 that answers ARP and DHCP (leases from 192.168.4.2, mask /24).
 * The guest's own driver and TCP/IP stack run unmodified (MicroPython: network.WLAN(network.STA_IF).scan(),
 * .connect(ssid, key), .ifconfig()). Protocol level only: no RF. Nothing reaches the host's real network except
 * through what the host sets up: forwards into the guest (st_rp2040_wifi_forward_tcp: listeners on 127.0.0.1 only) and
 * bridges out of it (st_rp2040_wifi_bridge_tcp: a gateway port relayed to the one host:port the host names).
 * docs/wireless.md describes the model.
 * Every call below on a chip without the CYW43439 is rejected (-1, last_error says why). */

/* Join state of the station (st_rp2040_wifi_info.state). New values may be added: treat unknown ones as DOWN. */
typedef enum {
    ST_RP2040_WIFI_DOWN       = 0, /* not associated: radio off, never joined, or left */
    ST_RP2040_WIFI_SCANNING   = 1, /* not associated; the station scanned and has not tried to join since */
    ST_RP2040_WIFI_JOINED     = 2, /* associated with the AP; ST_RP2040_WIFI_LEASED in flags once DHCP gave it an address */
    ST_RP2040_WIFI_BAD_AUTH   = 3, /* the last join found the AP but the passphrase was wrong (MicroPython: STAT_WRONG_PASSWORD) */
    ST_RP2040_WIFI_NO_NETWORK = 4  /* the last join asked for an SSID no AP has (MicroPython: STAT_NO_AP_FOUND) */
} st_rp2040_wifi_state;

/* st_rp2040_wifi_info.flags */
enum {
    ST_RP2040_WIFI_POWERED = 1, /* WL_ON (GPIO23) is high: the firmware powered the radio */
    ST_RP2040_WIFI_LEASED  = 2  /* ipv4 holds the address the gateway's DHCP server gave the station */
};

/* Link status. Set `size` to sizeof(st_rp2040_wifi_info) before calling: the library fills that many bytes at most and
 * sets `size` to the bytes it filled, so a host built against an older, shorter struct keeps working with a newer
 * library and the other way round. Fields are only ever added at the end; a reserved field may later get a meaning
 * whose value is 0 in every earlier version. 208 bytes in this version (112 before the port forwarding fields, 160
 * before the outbound bridges), the same on x86 and x64. */
typedef struct st_rp2040_wifi_info {
    uint32_t size;        /* in: bytes the caller provides (at least 8); out: bytes filled */
    int32_t  state;       /* st_rp2040_wifi_state */
    uint32_t flags;       /* ST_RP2040_WIFI_POWERED | ST_RP2040_WIFI_LEASED */
    int32_t  rssi;        /* dBm of the joined AP, 0 when not joined */
    uint8_t  ipv4[4];     /* the DHCP lease (ST_RP2040_WIFI_LEASED), else 0.0.0.0 */
    uint8_t  gateway[4];  /* 192.168.4.1 */
    uint8_t  netmask[4];  /* 255.255.255.0 */
    uint8_t  mac[6];      /* the CYW43439's station MAC (unique per chip in a process) */
    uint8_t  reserved0[2];
    char     ssid[33];    /* the joined SSID, or the one of the last failed join; NUL-terminated UTF-8 */
    uint8_t  reserved1[3];
    uint64_t tx_bytes;    /* Ethernet bytes the guest transmitted (since create) */
    uint64_t rx_bytes;    /* Ethernet bytes delivered to the guest */
    uint64_t tx_frames;
    uint64_t rx_frames;
    uint32_t changes;     /* status changes so far (each st_rp2040_wifi_event counts one) */
    uint32_t reserved2;
    /* ---- 112: port forwarding (st_rp2040_wifi_forward_tcp); a library older than it fills `size` = 112 ---- */
    uint32_t tcp_forwards;   /* host ports listened on */
    uint32_t tcp_open;       /* forwarded connections accepted and not finished */
    uint64_t tcp_accepted;   /* connections accepted from host clients since create */
    uint64_t tcp_failed;     /* of them, refused: nothing listened on the guest port in time, or too many at once */
    uint64_t tcp_to_guest;   /* payload bytes from the host clients into the guest */
    uint64_t tcp_from_guest; /* payload bytes from the guest's servers to the host clients */
    uint16_t tcp_host_port;  /* the connection of the last ST_RP2040_WIFI_EV_TCP_* event: forward's host port, */
    uint16_t tcp_guest_port; /*   its guest port, */
    uint16_t tcp_peer_port;  /*   and the host client's own port (127.0.0.1:tcp_peer_port) */
    uint16_t reserved3;
    /* ---- 160: outbound bridges (st_rp2040_wifi_bridge_tcp); a library older than them fills `size` = 160 ---- */
    uint32_t bridges;             /* gateway ports bridged */
    uint32_t bridge_open;         /* bridged connections the guest opened and that have not finished */
    uint64_t bridge_connected;    /* guest connections relayed to their host since create */
    uint64_t bridge_failed;       /* of the guest's connections, given up: host refused, unreachable, unknown name, too many */
    uint64_t bridge_to_host;      /* payload bytes from the guest to the hosts */
    uint64_t bridge_to_guest;     /* payload bytes from the hosts to the guest */
    uint16_t bridge_gateway_port; /* the connection of the last ST_RP2040_WIFI_EV_BRIDGE_* event: the gateway port the guest dialled, */
    uint16_t bridge_host_port;    /*   the host's port it is relayed to, */
    uint16_t bridge_guest_port;   /*   and the guest's own port (192.168.4.2:bridge_guest_port) */
    uint16_t bridge_error;        /* why the last ST_RP2040_WIFI_EV_BRIDGE_FAILED failed: ST_RP2040_WIFI_BRIDGE_ERR_* (0: none yet) */
} st_rp2040_wifi_info;

/* st_rp2040_wifi_info.bridge_error. New values may be added: treat unknown ones as UNREACHABLE. */
enum {
    ST_RP2040_WIFI_BRIDGE_ERR_NONE        = 0,
    ST_RP2040_WIFI_BRIDGE_ERR_REFUSED     = 1, /* the host answered with a reset: nothing listens on host:port */
    ST_RP2040_WIFI_BRIDGE_ERR_UNREACHABLE = 2, /* no answer within 10 s of host time, or no route */
    ST_RP2040_WIFI_BRIDGE_ERR_NO_HOST     = 3, /* the bridge's host name does not resolve on this PC */
    ST_RP2040_WIFI_BRIDGE_ERR_FULL        = 4  /* 64 bridged connections are open already */
};

/* Events of the Wi-Fi callback. New values may be added: ignore unknown ones. */
typedef enum {
    ST_RP2040_WIFI_EV_SCAN       = 1, /* the guest scanned (it finds the configured AP, if any) */
    ST_RP2040_WIFI_EV_JOIN       = 2, /* associated with the AP (DHCP follows) */
    ST_RP2040_WIFI_EV_LEASE      = 3, /* the gateway leased status->ipv4 to the station */
    ST_RP2040_WIFI_EV_LEAVE      = 4, /* the association ended: disconnect(), active(False) or WL_ON low */
    ST_RP2040_WIFI_EV_BAD_AUTH   = 5, /* a join with the wrong passphrase */
    ST_RP2040_WIFI_EV_NO_NETWORK = 6, /* a join for an SSID no AP has */
    ST_RP2040_WIFI_EV_TCP_OPEN    = 7, /* a forwarded connection reached the guest's server (tcp_*_port say which) */
    ST_RP2040_WIFI_EV_TCP_CLOSE   = 8, /* a forwarded connection ended (closed by either side, or reset) */
    ST_RP2040_WIFI_EV_TCP_REFUSED = 9, /* a forwarded connection was given up: no server on the guest port within 10 s simulated */
    ST_RP2040_WIFI_EV_BRIDGE_OPEN   = 10, /* a guest connection to a bridged gateway port reached its host (bridge_*_port say which) */
    ST_RP2040_WIFI_EV_BRIDGE_CLOSE  = 11, /* a bridged connection ended (closed by either side, or reset) */
    ST_RP2040_WIFI_EV_BRIDGE_FAILED = 12  /* a guest connection to a bridged port was reset: the host could not be reached (bridge_error) */
} st_rp2040_wifi_event;

/* A Wi-Fi status change. Called when st_rp2040_run / _run_until_edge / _wifi_poll has finished its work, on the same
 * thread, once per event in the order they happened (after the pin callbacks of that call), never from the middle
 * of the emulation. `cycle` is the core-0 cycle at which it happened (st_rp2040_cycle_to_ps converts it); `status`
 * is the status right after it, valid during the call only. Running the chip from inside is rejected, as from the
 * other callbacks; reading it (st_rp2040_wifi_status...) and destroying it are allowed. Events raised while no
 * callback is set are not kept. */
typedef void (*st_rp2040_wifi_cb)(void *user, int event, uint64_t cycle, const st_rp2040_wifi_info *status);

/* Configure the AP the chip sees: `ssid` 1..32 bytes of UTF-8; `passphrase` NULL or "" for an open network, else
 * WPA2-PSK with 8..63 printable ASCII characters (or 64 hex digits). channel 1..14 (0 = 6), rssi -120..-1 dBm
 * (0 = -50). It replaces the previous AP; ssid NULL or "" removes it (scans then find nothing and joins fail with
 * ST_RP2040_WIFI_NO_NETWORK). Call it at any time: before loading or running the firmware (the usual place), or later (an
 * existing association is kept; the next scan or join sees the new AP). Returns 0, or -1 on a bad handle, a faulted
 * chip, a chip without the CYW43439 or a bad argument (st_rp2040_last_error says which). */
int  st_rp2040_wifi_set_ap(st_rp2040 *chip, const char *ssid, const char *passphrase, int channel, int rssi);
/* Fill *status (see st_rp2040_wifi_info for `size`). Returns 0, or -1 (bad handle, faulted chip, no CYW43439, NULL, size < 8). */
int  st_rp2040_wifi_status(st_rp2040 *chip, st_rp2040_wifi_info *status);
/* cb = NULL disables (also from inside a callback). */
void st_rp2040_set_wifi_callback(st_rp2040 *chip, st_rp2040_wifi_cb cb, void *user);

/* Port forwarding: listen on 127.0.0.1:host_port (loopback only: nothing outside the PC reaches it) and relay every
 * connection accepted there to the station's guest_port (192.168.4.2), the way a NAT router's port forward does: a
 * browser on http://127.0.0.1:8080 reaches a MicroPython / Microdot server on port 80. The guest sees the connections
 * come from the gateway, 192.168.4.1. Connections made before the guest listens wait for it (SYN retried) up to 10 s
 * of simulated time, then are reset (ST_RP2040_WIFI_EV_TCP_REFUSED). host_port 0 picks a free port. guest_port 0
 * removes the forward of host_port (its connections are reset). At most 16 forwards per chip; st_rp2040_destroy
 * closes them. Returns the port listened on (1..65535), 0 for a removal, or -1 (bad handle, faulted chip, no
 * CYW43439, a port out of range, already forwarded, or in use by another program: st_rp2040_last_error says which). */
int  st_rp2040_wifi_forward_tcp(st_rp2040 *chip, int host_port, int guest_port);
/* Move what the forwarded host sockets delivered (connections, bytes, closes) into the guest now, and run the
 * forwarding timers; then deliver the Wi-Fi events raised. The socket I/O itself runs on background threads; this
 * and the run calls are the only places the guest side is touched, always on the caller's thread. The run calls do
 * it by themselves at most once per millisecond of host time (never on a chip without forwards or bridges); a host
 * that runs the chip in short calls (Proteus) calls this once per step for the lowest latency. The bridges
 * (_wifi_bridge_tcp) are pumped here too. Not from inside a callback. Returns the connections open, forwarded and
 * bridged, or -1. */
int  st_rp2040_wifi_poll(st_rp2040 *chip);

/* Outbound bridge: a guest connecting to the gateway 192.168.4.1:gateway_port is relayed to host:port on the PC's real
 * network (an MQTT broker on 127.0.0.1:1883, say), the way a NAT router relays a station's connection. The guest reaches
 * only what the host configures here: a gateway port is relayed to its one host:port, nothing the guest sends picks the
 * destination, and every other address stays unreachable. The guest's SYN is answered once the host connection is up;
 * when it cannot be (refused, unreachable, an unknown name, 64 bridged connections open) the guest gets a reset, so its
 * connect() fails at once, and ST_RP2040_WIFI_EV_BRIDGE_FAILED says why (bridge_error). `host` is a name or an IPv4 / IPv6
 * literal of at most 253 bytes, resolved by the PC at each connection; port 1..65535. gateway_port 1..65535, each once
 * per chip (port 0 removes its bridge and resets its connections; host is then ignored and may be NULL). At most 16
 * bridges per chip; they are pumped with the forwards (st_rp2040_wifi_poll and the run calls); st_rp2040_destroy closes them.
 * Returns 0, or -1 (bad handle, faulted chip, no CYW43439, a port out of range, a bad host, already bridged, not
 * bridged, too many: st_rp2040_last_error says which). */
int  st_rp2040_wifi_bridge_tcp(st_rp2040 *chip, int gateway_port, const char *host, int port);
/* A name for the gateway's DNS server (192.168.4.1, which the DHCP lease gives the station): the guest's lookups of
 * `name` answer `ipv4` (dotted quad), e.g. ("broker.local", "192.168.4.1") lets the firmware dial a bridge by name.
 * Every other name is NXDOMAIN, as before. `name` is 1..253 bytes of letters, digits, '-', '_' and '.', labels of
 * 1..63, compared without case; a trailing '.' is dropped. ipv4 NULL or "" removes the name. At most 64 names per
 * chip; adding a name again replaces its address. Returns 0, or -1 (bad handle, faulted chip, no CYW43439, a bad name
 * or address, a name not there to remove, too many: st_rp2040_last_error says which). */
int  st_rp2040_wifi_dns_add(st_rp2040 *chip, const char *name, const char *ipv4);

/* The Wi-Fi types and values are those of silicontwin_rp2350.h: st_rp2040_wifi_info is its st_wifi_status (the name
 * differs because st_rp2040_wifi_status is the function), same layout, same ST_*WIFI_* numbers, so a host that drives
 * both chips can share one handler. */

/* ---- Files onto CIRCUITPY (CircuitPython) ---------------------------------------------------------------------------
 * As in silicontwin_rp2350.h (st_rp2350_fs_*; docs/native-abi.md): what a PC does when it copies files onto the CIRCUITPY
 * drive, which CircuitPython keeps read-only to its own code while USB is connected. The FAT12/FAT16 volume is searched
 * from the end of the image st_rp2040_load_uf2 loaded (rounded up to 4 KiB): the first erase sector holding a consistent
 * FAT boot sector. It exists once CircuitPython has formatted CIRCUITPY on its first boot (on the 2 MiB Pico: 1 MiB at
 * flash offset 0x100000). Queue files with st_rp2040_fs_put, then st_rp2040_fs_commit(chip, 1) writes them and reboots
 * the chip. Before the first commit on a fresh flash, run until st_rp2040_fs_status returns 1 with ST_RP2040_FS_BOOT_OUT
 * and the flash counters have stopped moving. */

enum {
    ST_RP2040_FS_BOOT_OUT = 1  /* st_rp2040_fs_info.flags: the root holds boot_out.txt */
};

/* Growable as st_rp2040_wifi_info: set `size` to sizeof(st_rp2040_fs_info). 48 bytes, the layout of st_fs_status. */
typedef struct st_rp2040_fs_info {
    uint32_t size;             /* in: bytes the caller provides (at least 8); out: bytes filled */
    uint32_t fat_bits;         /* 12 or 16; 0 = no volume found (not formatted yet) */
    uint32_t volume_offset;    /* flash offset of its boot sector */
    uint32_t volume_bytes;
    uint32_t cluster_bytes;
    uint32_t free_bytes;
    uint32_t staged_files;     /* files st_rp2040_fs_put has queued for the next commit */
    uint32_t flags;            /* ST_RP2040_FS_BOOT_OUT */
    uint64_t flash_erases;     /* erase commands the firmware sent the flash since create (SSI or bootrom) */
    uint64_t flash_programmed; /* bytes the firmware programmed into flash since create */
} st_rp2040_fs_info;

/* 1 = a volume was found, 0 = none (yet), -1 = bad handle, faulted chip, NULL, size < 8. */
int st_rp2040_fs_status(st_rp2040 *chip, st_rp2040_fs_info *info);
/* Queue `path` (UTF-8, '/'-separated, directories created as needed; 255 characters per name, 256 per path) with `len`
 * bytes of `data` (NULL allowed when len is 0). A path queued again replaces the earlier one. 0, or -1 (st_rp2040_last_error). */
int st_rp2040_fs_put(st_rp2040 *chip, const char *path, const uint8_t *data, size_t len);
/* Write the queued files, all or nothing, then reboot the chip as a watchdog reset does when `reset` is nonzero. On a
 * failure (no volume yet, volume or root directory full, a path through a file) nothing is written, there is no reset
 * and -1 is returned. The queue is emptied either way. 0 on success. Not from inside a callback. */
int st_rp2040_fs_commit(st_rp2040 *chip, int reset);

#ifdef ST_ENABLE_TEST_HOOKS
/* TEST ONLY: exists only in a library built with -p:StTestHooks=true, never in packages.
 * Makes the core throw so the fault path can be exercised: mode 1 = exception inside the next run,
 * 2 = NullReferenceException inside the next run, 3 = exception in the pad scan after the next run,
 * 4 = exception raised right here. Returns 0, or -1 for a bad handle / mode. */
int st_rp2040_test_inject_fault(st_rp2040 *chip, int mode);
#endif

#ifdef __cplusplus
}
#endif

#endif /* SILICONTWIN_RP2040_H */

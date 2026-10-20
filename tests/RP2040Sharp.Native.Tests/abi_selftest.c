/*
 * abi_selftest.c — the silicontwin_rp2040 C ABI from plain C99 (MSVC-compatible), against the NativeAOT library
 * (dotnet publish src/RP2040Sharp.Native -c Release -r <rid> -p:StNativeLib=true).
 *
 * Bad handles, NULL / out-of-range arguments, UF2 images of another chip, callbacks that clear themselves or
 * destroy the chip. With a MicroPython RPI_PICO image it also boots the REPL over the USB CDC, runs a line and
 * measures the idle REPL under st_rp2040_run_until_edge at guards 1 and 75 (all pads watched).
 *
 * With a MicroPython RPI_PICO_W image as well it joins the virtual AP (WPA2) through the Wi-Fi exports and prints the
 * boot-to-connected time.
 *
 * usage: abi_selftest [micropython-rpi_pico.uf2 [micropython-rpi_pico_w.uf2]]      exit 0 = all checks passed
 */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "silicontwin_rp2040.h"

static int st_checks, st_failed;

#define CHECK(cond, what) do { st_checks++; if (!(cond)) { st_failed++; printf("  FAIL  %s  (line %d)\n", what, __LINE__); } \
                               else printf("  pass  %s\n", what); } while (0)

static unsigned char *read_file(const char *path, size_t *len) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size < 0) { fclose(f); return NULL; }
    *len = (size_t)size;
    unsigned char *buf = malloc(*len ? *len : 1);
    if (buf && fread(buf, 1, *len, f) != *len) { free(buf); buf = NULL; }
    fclose(f);
    return buf;
}

static void uf2_block(uint8_t *b, uint32_t addr, uint32_t family) {
    uint32_t w[8] = { 0x0A324655u, 0x9E5D5157u, 0x2000u, addr, 256, 0, 1, family };
    memset(b, 0, 512);
    for (int i = 0; i < 8; i++) {
        b[i * 4] = (uint8_t)w[i]; b[i * 4 + 1] = (uint8_t)(w[i] >> 8);
        b[i * 4 + 2] = (uint8_t)(w[i] >> 16); b[i * 4 + 3] = (uint8_t)(w[i] >> 24);
    }
    b[508] = 0x30; b[509] = 0x6F; b[510] = 0xB1; b[511] = 0x0A;
}

static void hammer_dead_handle(st_rp2040 *dead, const char *label) {
    uint8_t buf[16] = {0};
    int oe = 7;
    char what[96];
    st_rp2040_destroy(dead);
    snprintf(what, sizeof what, "%s: status is INVALID_HANDLE", label);
    CHECK(st_rp2040_status(dead) == ST_RP2040_STATUS_INVALID_HANDLE, what);
    snprintf(what, sizeof what, "%s: last_error never NULL", label);
    CHECK(st_rp2040_last_error(dead) != NULL, what);
    snprintf(what, sizeof what, "%s: run and queries return 0", label);
    CHECK(st_rp2040_run(dead, 1000) == 0 && st_rp2040_run_until_edge(dead, 1000, 10, 0xFFFFFFFFu) == 0 &&
          st_rp2040_cycles(dead) == 0 && st_rp2040_time_ps(dead) == 0 && st_rp2040_cycle_to_ps(dead, 5) == 0 &&
          st_rp2040_instructions(dead, -1) == 0 && st_rp2040_pin_get(dead, 3, &oe) == 0 && oe == 0 &&
          st_rp2040_pin_get_pull(dead, 3) == 0, what);
    snprintf(what, sizeof what, "%s: load_uf2 = -1", label);
    CHECK(st_rp2040_load_uf2(dead, buf, sizeof buf) == -1, what);
    st_rp2040_pin_set_input(dead, 3, 1);
    st_rp2040_adc_set_voltage(dead, 0, 1.0);
    st_rp2040_set_pin_callback(dead, NULL, NULL);
    st_rp2040_set_serial_callback(dead, NULL, NULL);
    st_rp2040_serial_write(dead, ST_RP2040_SERIAL_USB_CDC, buf, sizeof buf);
    {
        st_rp2040_wifi_info ws;
        memset(&ws, 0, sizeof ws);
        ws.size = sizeof ws;
        snprintf(what, sizeof what, "%s: wifi_set_ap / wifi_status = -1", label);
        CHECK(st_rp2040_wifi_set_ap(dead, "x", NULL, 0, 0) == -1 && st_rp2040_wifi_status(dead, &ws) == -1, what);
        st_rp2040_set_wifi_callback(dead, NULL, NULL);
    }
}

/* ---- host side of the callbacks ---- */
typedef struct { char text[65536]; size_t len; long pins; st_rp2040 *chip; int destroy_in_pin_cb; } host_t;

static void on_serial(void *user, int channel, const uint8_t *data, size_t len) {
    host_t *h = user;
    if (channel != ST_RP2040_SERIAL_USB_CDC) return;
    for (size_t i = 0; i < len; i++) {
        if (h->len + 1 >= sizeof h->text) memmove(h->text, h->text + sizeof h->text / 2, h->len -= sizeof h->text / 2);
        h->text[h->len++] = (char)data[i];
    }
    h->text[h->len] = 0;
}

static void on_pin(void *user, int gpio, int level, int oe, uint64_t cycle) {
    host_t *h = user;
    (void)gpio; (void)level; (void)oe; (void)cycle;
    h->pins++;
    if (h->destroy_in_pin_cb) st_rp2040_destroy(h->chip);
}

static double now_s(void) { return (double)clock() / CLOCKS_PER_SEC; }

static int run_until(st_rp2040 *c, host_t *h, const char *needle, size_t from, uint64_t max_cycles, uint32_t guard) {
    uint64_t end = st_rp2040_cycles(c) + max_cycles;
    while (st_rp2040_cycles(c) < end) {
        st_rp2040_run_until_edge(c, 1000000, guard, 0x3FFFFFFFu);
        if (st_rp2040_status(c) != ST_RP2040_STATUS_OK) return 0;
        if (h->len > from && strstr(h->text + from, needle)) return 1;
    }
    return 0;
}

static int contains(const char *s, const char *needle) { return s && strstr(s, needle) != NULL; }

/* ---- Wi-Fi (ST_RP2040_CYW43) ---------------------------------------------------------------------------------- */

static int g_wifi_events, g_wifi_lease, g_wifi_mode;
static st_rp2040 *g_wifi_chip;
static char g_wifi_cdc[8192];
static size_t g_wifi_cdc_len;
static st_rp2040_wifi_info g_wifi_last;

static void wifi_cdc(void *user, int channel, const uint8_t *d, size_t len) {
    (void)user;
    if (channel != ST_RP2040_SERIAL_USB_CDC) return;
    for (size_t i = 0; i < len && g_wifi_cdc_len + 1 < sizeof g_wifi_cdc; i++) g_wifi_cdc[g_wifi_cdc_len++] = (char)d[i];
    g_wifi_cdc[g_wifi_cdc_len] = 0;
}

static void wifi_event(void *user, int event, uint64_t cycle, const st_rp2040_wifi_info *s) {
    (void)user; (void)cycle;
    g_wifi_events++;
    g_wifi_last = *s;
    if (event == ST_RP2040_WIFI_EV_LEASE) g_wifi_lease++;
    if (g_wifi_mode == 1) st_rp2040_destroy(g_wifi_chip);                    /* destroyed from inside: no more events */
    if (g_wifi_mode == 2) st_rp2040_run(g_wifi_chip, 10);                    /* rejected */
}

static void wifi_arguments(void) {
    st_rp2040_wifi_info s;
    st_rp2040 *plain = st_rp2040_create_ex(0, ST_RP2040_BUS_ALL);
    st_rp2040 *w = st_rp2040_create_ex(0, ST_RP2040_BUS_ALL | ST_RP2040_CYW43);
    st_rp2040_wifi_cb cb = wifi_event;
    CHECK(sizeof(st_rp2040_wifi_info) == 112 && offsetof(st_rp2040_wifi_info, ipv4) == 16 && offsetof(st_rp2040_wifi_info, mac) == 28 &&
          offsetof(st_rp2040_wifi_info, ssid) == 36 && offsetof(st_rp2040_wifi_info, tx_bytes) == 72 &&
          offsetof(st_rp2040_wifi_info, changes) == 104, "st_rp2040_wifi_info layout: 112 bytes, documented offsets");
    memset(&s, 0, sizeof s);
    s.size = sizeof s;
    CHECK(st_rp2040_wifi_set_ap(plain, "Proteus", NULL, 0, 0) == -1 && contains(st_rp2040_last_error(plain), "CYW43439"),
          "plain Pico: wifi_set_ap -> -1 + reason");
    CHECK(st_rp2040_wifi_status(plain, &s) == -1, "plain Pico: wifi_status -> -1");
    st_rp2040_set_wifi_callback(plain, NULL, NULL);
    CHECK(st_rp2040_status(plain) == ST_RP2040_STATUS_OK, "plain Pico still OK");

    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", NULL, 0, 0) == 0, "open AP");
    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", "", 0, 0) == 0, "\"\" passphrase = open");
    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", "12345678", 13, -30) == 0, "WPA2 AP, channel 13, -30 dBm");
    CHECK(st_rp2040_wifi_set_ap(w, NULL, NULL, 0, 0) == 0, "NULL ssid removes the AP");
    CHECK(st_rp2040_wifi_set_ap(w, "123456789012345678901234567890123", NULL, 0, 0) == -1 && contains(st_rp2040_last_error(w), "32"),
          "33-byte ssid -> -1 + reason");
    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", "1234567", 0, 0) == -1, "7-character passphrase -> -1");
    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", NULL, 15, 0) == -1, "channel 15 -> -1");
    CHECK(st_rp2040_wifi_set_ap(w, "Proteus", NULL, 0, 3) == -1, "rssi +3 -> -1");
    CHECK(st_rp2040_wifi_status(w, NULL) == -1, "wifi_status(NULL) -> -1");
    s.size = 7;
    CHECK(st_rp2040_wifi_status(w, &s) == -1, "size 7 -> -1");
    s.size = sizeof s;
    CHECK(st_rp2040_wifi_status(w, &s) == 0 && s.size == sizeof s && s.state == ST_RP2040_WIFI_DOWN && s.flags == 0 &&
          s.gateway[0] == 192 && s.gateway[3] == 1 && s.netmask[0] == 255 && s.ipv4[0] == 0, "fresh Pico W: DOWN, gateway 192.168.4.1");
    memset(&s, 0xAB, sizeof s);
    s.size = 16;
    CHECK(st_rp2040_wifi_status(w, &s) == 0 && s.size == 16 && s.rssi == 0 && s.ipv4[0] == 0xAB, "size 16: 16 bytes filled, the rest untouched");
    st_rp2040_set_wifi_callback(w, cb, NULL);
    st_rp2040_set_wifi_callback(w, NULL, NULL);
    CHECK(st_rp2040_status(w) == ST_RP2040_STATUS_OK, "Pico W still OK after all of that");
    st_rp2040_destroy(plain);
    st_rp2040_destroy(w);
}

/* MicroPython (Pico W) types a scan + connect over the CDC, run as the Proteus model runs it (guard 75). */
static void wifi_join(const unsigned char *uf2, size_t len, int mode, const char *pass) {
    static const char *script =
        "exec(\"import network,time\\nw=network.WLAN(network.STA_IF)\\nw.active(True)\\nprint('SCAN', len(w.scan()))\\n"
        "w.connect('Proteus', '%s')\\nfor _ in range(400):\\n if w.isconnected() or w.status() < 0: break\\n time.sleep_ms(20)\\n"
        "print('CONN', w.isconnected(), w.status())\")\r";
    char line[512], what[160];
    const uint32_t watch = 0x3FFFFFFFu & ~((1u << 23) | (1u << 24) | (1u << 25) | (1u << 29));
    clock_t t0 = clock();
    int sent = 0;
    g_wifi_events = g_wifi_lease = 0; g_wifi_mode = mode; g_wifi_cdc_len = 0; g_wifi_cdc[0] = 0;
    g_wifi_chip = st_rp2040_create_ex(0, ST_RP2040_BUS_ALL | ST_RP2040_CYW43);
    st_rp2040_set_serial_callback(g_wifi_chip, wifi_cdc, NULL);
    st_rp2040_set_wifi_callback(g_wifi_chip, wifi_event, NULL);
    CHECK(st_rp2040_wifi_set_ap(g_wifi_chip, "Proteus", "proteus-pass", 0, 0) == 0, "WPA2 AP set before the firmware");
    CHECK(st_rp2040_load_uf2(g_wifi_chip, uf2, len) > 1000, "Pico W image loads");
    snprintf(line, sizeof line, script, pass);
    for (long i = 0; i < 400000 && st_rp2040_status(g_wifi_chip) == ST_RP2040_STATUS_OK; i++) {
        st_rp2040_run_until_edge(g_wifi_chip, 30000, 75, watch);
        if (!sent && contains(g_wifi_cdc, ">>> ")) {
            st_rp2040_serial_write(g_wifi_chip, ST_RP2040_SERIAL_USB_CDC, (const uint8_t *)line, strlen(line));
            sent = 1;
        }
        if (mode == 0 && (contains(g_wifi_cdc, "CONN True") || contains(g_wifi_cdc, "CONN False"))) break;
        if (mode != 0 && g_wifi_events > 0) {
            for (int k = 0; k < 50; k++) st_rp2040_run_until_edge(g_wifi_chip, 30000, 75, watch);
            break;
        }
    }
    if (mode == 0) {
        st_rp2040_wifi_info s;
        double wall = (double)(clock() - t0) / CLOCKS_PER_SEC;
        memset(&s, 0, sizeof s);
        s.size = sizeof s;
        if (pass[0] == 'p') {
            CHECK(g_wifi_lease == 1 && contains(g_wifi_cdc, "SCAN 1") && contains(g_wifi_cdc, "CONN True 3"), "MicroPython scan + connect: CONN True 3");
            CHECK(st_rp2040_wifi_status(g_wifi_chip, &s) == 0 && s.state == ST_RP2040_WIFI_JOINED && (s.flags & ST_RP2040_WIFI_LEASED) &&
                  s.ipv4[0] == 192 && s.ipv4[3] == 2 && strcmp(s.ssid, "Proteus") == 0 && s.tx_bytes > 0 && s.rx_bytes > 0,
                  "wifi_status: JOINED, 192.168.4.2, traffic counted");
            snprintf(what, sizeof what, "boot to connected in %.2f s wall (%.3f s simulated), guard 75", wall,
                     (double)st_rp2040_time_ps(g_wifi_chip) * 1e-12);
            CHECK(1, what);
            printf("  %s\n", what);
        } else {
            CHECK(contains(g_wifi_cdc, "CONN False -3") && st_rp2040_wifi_status(g_wifi_chip, &s) == 0 && s.state == ST_RP2040_WIFI_BAD_AUTH &&
                  g_wifi_last.state == ST_RP2040_WIFI_BAD_AUTH && g_wifi_lease == 0, "wrong passphrase: CONN False -3, BAD_AUTH");
        }
        st_rp2040_destroy(g_wifi_chip);
    } else if (mode == 1) {
        CHECK(g_wifi_events == 1 && st_rp2040_status(g_wifi_chip) == ST_RP2040_STATUS_INVALID_HANDLE, "destroyed from inside the Wi-Fi callback: one event, then gone");
    } else {
        CHECK(g_wifi_events > 0 && st_rp2040_status(g_wifi_chip) == ST_RP2040_STATUS_OK && contains(st_rp2040_last_error(g_wifi_chip), "re-entrantly"),
              "run from inside the Wi-Fi callback: rejected, chip OK");
        st_rp2040_destroy(g_wifi_chip);
    }
}

int main(int argc, char **argv) {
    const char *firmware_w = argc > 2 ? argv[2] : NULL;
    printf("%s\n", st_rp2040_version());

    printf("[create]\n");
    CHECK(st_rp2040_create(1234) == NULL && strstr(st_rp2040_last_error(NULL), "flash_bytes"), "bad flash size rejected");
    CHECK(st_rp2040_create_ex(0, 0x80) == NULL && strstr(st_rp2040_last_error(NULL), "unknown flags"), "unknown flag rejected");
    CHECK(ST_RP2040_BUS_I2C == 1 && ST_RP2040_BUS_SPI == 2 && ST_RP2040_BUS_UART == 4 && ST_RP2040_BUS_ALL == 7 &&
          ST_RP2040_CYW43 == 0x100, "flag values are those of silicontwin_rp2350.h");
    {
        st_rp2040 *b = st_rp2040_create_ex(0, ST_RP2040_BUS_ALL | ST_RP2040_CYW43);
        CHECK(b != NULL && st_rp2040_cycles_until_edge(b, 0xFFFFFFFFu) == UINT64_MAX,
              "pin-level buses with the CYW43: created, nothing routed, no engine edge coming");
        st_rp2040_destroy(b);
    }
    st_rp2040 *c = st_rp2040_create_ex(0, ST_RP2040_NONE);
    CHECK(c != NULL && st_rp2040_status(c) == ST_RP2040_STATUS_OK, "create");

    printf("[arguments]\n");
    int oe = 7;
    CHECK(st_rp2040_pin_get(c, 30, &oe) == 0 && oe == 0 && strstr(st_rp2040_last_error(c), "out of range"), "gpio 30 rejected");
    st_rp2040_pin_set_input(c, 3, 5);
    CHECK(strstr(st_rp2040_last_error(c), "level 5") != NULL, "level 5 rejected");
    st_rp2040_serial_write(c, ST_RP2040_SERIAL_USB_CDC, NULL, 4);
    CHECK(strstr(st_rp2040_last_error(c), "NULL") != NULL, "NULL serial data rejected");
    CHECK(st_rp2040_load_uf2(c, NULL, 0) == -1, "NULL UF2 rejected");
    CHECK(st_rp2040_cycles_until_edge(c, 0xFFFFFFFFu) == UINT64_MAX, "no pin-level engine edges");
    CHECK(st_rp2040_cycle_to_ps(c, 125000000u) == 1000000000000ull, "cycle_to_ps at 125 MHz");

    printf("[uf2]\n");
    uint8_t block[512];
    uf2_block(block, 0x10000000u, 0xE48BFF59u);
    CHECK(st_rp2040_load_uf2(c, block, sizeof block) == -1 && strstr(st_rp2040_last_error(c), "not RP2040"), "RP2350 image rejected");
    uf2_block(block, 0x10000000u, 0xE48BFF56u);
    CHECK(st_rp2040_load_uf2(c, block, sizeof block) == 1, "RP2040 block loaded");
    CHECK(st_rp2040_status(c) == ST_RP2040_STATUS_OK, "rejections never fault");
    st_rp2040_destroy(c);
    hammer_dead_handle(c, "destroyed");
    hammer_dead_handle(NULL, "NULL");
    hammer_dead_handle((st_rp2040 *)(intptr_t)0x5eed, "garbage");

    puts("[wifi]");
    wifi_arguments();

    if (argc < 2) { printf("%d checks, %d failed (no firmware: REPL scenarios skipped)\n", st_checks, st_failed); return st_failed != 0; }

    size_t len;
    unsigned char *uf2 = read_file(argv[1], &len);
    CHECK(uf2 != NULL, "firmware readable");
    if (!uf2) return 1;

    printf("[micropython]\n");
    static host_t host;
    c = st_rp2040_create(0);
    host.chip = c;
    st_rp2040_set_serial_callback(c, on_serial, &host);
    st_rp2040_set_pin_callback(c, on_pin, &host);
    CHECK(st_rp2040_load_uf2(c, uf2, len) > 1000, "MicroPython loaded");
    for (int i = 0; i < 2000 && !strstr(host.text, ">>> "); i++) st_rp2040_run(c, 1000000);
    CHECK(strstr(host.text, ">>> ") != NULL, "REPL prompt over USB CDC");
    size_t from = host.len;
    const char *line = "print('ok', 6*7)\r";
    st_rp2040_serial_write(c, ST_RP2040_SERIAL_USB_CDC, (const uint8_t *)line, strlen(line));
    CHECK(run_until(c, &host, "ok 42", from, 125000000u, 75), "line executed");

    for (int g = 0; g < 2; g++) {
        uint32_t guard = g == 0 ? 1 : 75;
        uint64_t start = st_rp2040_cycles(c), sim = 125000000u / 5;
        double t0 = now_s();
        while (st_rp2040_cycles(c) - start < sim) st_rp2040_run_until_edge(c, 1000000, guard, 0x3FFFFFFFu);
        double wall = now_s() - t0;
        double cyc = (double)(st_rp2040_cycles(c) - start);
        printf("  perf  idle REPL, guard %u: %.0f ms simulated in %.0f ms wall = %.3fx real time (%.1f Mcycles/s)\n",
               guard, cyc / 125000.0, wall * 1000, cyc / 125e6 / (wall > 0 ? wall : 1e-9), cyc / (wall > 0 ? wall : 1e-9) / 1e6);
    }

    printf("[callbacks]\n");
    from = host.len;
    line = "from machine import Pin; p=Pin(15,Pin.OUT); p.toggle(); print('t')\r";
    host.destroy_in_pin_cb = 1;
    st_rp2040_serial_write(c, ST_RP2040_SERIAL_USB_CDC, (const uint8_t *)line, strlen(line));
    for (int i = 0; i < 200 && st_rp2040_status(c) == ST_RP2040_STATUS_OK; i++) st_rp2040_run_until_edge(c, 1000000, 75, 1u << 15);
    CHECK(host.pins > 0 && st_rp2040_status(c) == ST_RP2040_STATUS_INVALID_HANDLE, "destroy from inside the pin callback");
    free(uf2);

    puts("[wifi firmware]");
    if (firmware_w) {
        size_t wlen = 0;
        unsigned char *wfw = read_file(firmware_w, &wlen);
        if (!wfw) { printf("  FAIL  cannot read %s\n", firmware_w); st_checks++; st_failed++; }
        else {
            wifi_join(wfw, wlen, 0, "proteus-pass");
            wifi_join(wfw, wlen, 0, "wrong-pass");
            wifi_join(wfw, wlen, 1, "proteus-pass");
            wifi_join(wfw, wlen, 2, "proteus-pass");
            free(wfw);
        }
    } else puts("wifi (firmware): SKIPPED (pass a Pico W .uf2 as the second argument)");

    printf("%d checks, %d failed\n", st_checks, st_failed);
    return st_failed != 0;
}

/*
 * abi_selftest.c — the silicontwin_rp2040 C ABI from plain C99 (MSVC-compatible), against the NativeAOT library
 * (dotnet publish src/RP2040Sharp.Native -c Release -r <rid> -p:StNativeLib=true).
 *
 * Bad handles, NULL / out-of-range arguments, UF2 images of another chip, callbacks that clear themselves or
 * destroy the chip. With a MicroPython RPI_PICO image it also boots the REPL over the USB CDC, runs a line and
 * measures the idle REPL under st_rp2040_run_until_edge at guards 1 and 75 (all pads watched).
 *
 * usage: abi_selftest [micropython-rpi_pico.uf2]      exit 0 = all checks passed
 */
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

int main(int argc, char **argv) {
    printf("%s\n", st_rp2040_version());

    printf("[create]\n");
    CHECK(st_rp2040_create(1234) == NULL && strstr(st_rp2040_last_error(NULL), "flash_bytes"), "bad flash size rejected");
    CHECK(st_rp2040_create_ex(0, 0x80) == NULL && strstr(st_rp2040_last_error(NULL), "unknown flags"), "unknown flag rejected");
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

    printf("%d checks, %d failed\n", st_checks, st_failed);
    return st_failed != 0;
}

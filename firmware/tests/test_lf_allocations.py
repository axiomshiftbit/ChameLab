"""Fault-inject allocations in the real LF codecs and readers (ASan/UBSan)."""
from pathlib import Path
import subprocess
import tempfile


FIRMWARE = Path(__file__).resolve().parents[1]
SRC = FIRMWARE / "application/src"


def function(source, name):
    start = source.index("bool " + name + "(")
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    with tempfile.TemporaryDirectory(prefix="chamelab-lf-alloc-") as directory:
        temp = Path(directory)
        (temp / "nrf_pwm.h").write_text("""
#pragma once
#include <stdint.h>
typedef struct { uint16_t channel_0, channel_1, channel_2, counter_top; } nrf_pwm_values_wave_form_t;
typedef struct { union { const nrf_pwm_values_wave_form_t *p_wave_form; } values; uint16_t length; uint32_t repeats, end_delay; } nrf_pwm_sequence_t;
#define NRF_PWM_VALUES_LENGTH(a) (sizeof(a) / sizeof(uint16_t))
""")
        (temp / "nordic_common.h").write_text("""
#pragma once
#define ARRAY_SIZE(a) (sizeof(a) / sizeof((a)[0]))
#define IS_SET(x,b) (((x) >> (b)) & 1)
#define SET_BIT(x,b) ((x) |= 1UL << (b))
#define CLR_BIT(x,b) ((x) &= ~(1UL << (b)))
""")
        (temp / "nrf_log.h").write_text("#pragma once\n#define NRF_LOG_MODULE_REGISTER()\n#define NRF_LOG_INFO(...) ((void)0)\n#define NRF_LOG_DEBUG(...) ((void)0)\n")
        for name in ("nrf_log_ctrl.h", "nrf_log_default_backends.h"):
            (temp / name).write_text("#pragma once\n")
        (temp / "app_util_platform.h").write_text("#define CRITICAL_REGION_ENTER() {\n#define CRITICAL_REGION_EXIT() }\n")
        protocols = ["em410x_64", "em410x_32", "em410x_16", "em410x_electra", "hidprox", "viking", "jablotron", "idteck", "ioprox", "pyramid", "pac"]
        readers = ["em410x", "hidprox", "viking", "jablotron", "ioprox", "pyramid", "pac"]
        harness = r"""
#include <assert.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include "protocols.h"
#include "wiegand.h"
#include "circular_buffer.h"
static int budget, live, failed, radio;
void *__real_malloc(size_t);
void __real_free(void *);
void *__wrap_malloc(size_t n) {
    if (budget == 0) { failed++; return NULL; }
    if (budget > 0) budget--;
    void *p = __real_malloc(n);
    assert(p); live++; return p;
}
void __wrap_free(void *p) { if (p) live--; __real_free(p); }
void *__wrap_calloc(size_t n, size_t size) {
    void *p = __wrap_malloc(n * size);
    if (p) memset(p, 0, n * size);
    return p;
}
static circular_buffer cb;
typedef struct { uint32_t time; } autotimer;
static autotimer timer;
static autotimer *bsp_obtain_timer(int unused) { return &timer; }
static void bsp_return_timer(autotimer *unused) {}
#define NO_TIMEOUT_1MS(t, ms) (false)
#define EM410X_BUFFER_SIZE 128
#define EM410X_ELECTRA_WAIT_MS 160
#define HIDPROX_BUFFER_SIZE 6144
#define IOPROX_BUFFER_SIZE 6144
#define PYRAMID_BUFFER_SIZE 6144
#define PAC_BUFFER_SIZE 6144
#define VIKING_BUFFER_SIZE 128
#define JABLOTRON_BUFFER_SIZE 128
#define CIRCULAR_BUFFER_SIZE 512
static void start_lf_125khz_radio(void) { radio++; }
static void stop_lf_125khz_radio(void) {}
static void bsp_delay_ms(int unused) {}
static void bsp_wdt_feed(void) {}
"""
        harness += "\n".join(f"extern const protocol {p};" for p in protocols)
        harness += "\nstatic const protocol *em410x_protocols[] = {&em410x_64, &em410x_32, &em410x_16, &em410x_electra};\nstatic const size_t em410x_protocols_size = 4;\n"
        for name in readers + ["saadc"]:
            harness += f"static void init_{name}_hw(void) {{ radio++; }}\nstatic void uninit_{name}_hw(void) {{}}\n"
        for name in readers:
            source = (SRC / f"rfid/reader/lf/lf_{name}_data.c").read_text()
            harness += function(source, name + "_read") + "\n"
        harness += function((SRC / "rfid/reader/lf/lf_reader_generic.c").read_text(), "raw_read_to_buffer")
        harness += "\nstatic const protocol *all[] = {" + ",".join("&" + p for p in protocols) + "};\n"
        harness += r"""
int main(int argc, char **argv) {
    int mode = atoi(argv[1]);
    if (mode == 0) {
        for (size_t i = 0; i < sizeof(all)/sizeof(all[0]); i++) {
            for (int fail_at = 0; fail_at < 4; fail_at++) {
                budget = fail_at; failed = 0;
                void *codec = all[i]->alloc();
                if (failed) assert(codec == NULL);
                if (codec) {
                    all[i]->decoder.start(codec, 0);
                    all[i]->decoder.feed(codec, 0);
                    all[i]->free(codec);
                }
                assert(live == 0);
            }
        }
    } else if (mode == 1) {
        budget = 0;
        assert(wiegand_card_alloc() == NULL);
        for (unsigned format = 0; format <= 255; format++)
            for (unsigned bits = 0; bits <= 64; bits++) {
                assert(unpack(format, bits, 0, 0) == NULL);
                assert(unpack(format, bits, UINT64_MAX, UINT64_MAX) == NULL);
            }
    } else {
        for (int reader = 0; reader < 8; reader++)
            for (int fail_at = 0; fail_at < 12; fail_at++) {
                uint8_t data[32] = {0}; size_t length = 99;
                budget = fail_at; failed = radio = 0;
                bool ok = false;
                switch (reader) {
                    case 0: ok = em410x_read(data, 0); break;
                    case 1: ok = hidprox_read(data, 0, 0); break;
                    case 2: ok = viking_read(data, 0); break;
                    case 3: ok = jablotron_read(data, 0); break;
                    case 4: ok = ioprox_read(data, 0, 0); break;
                    case 5: ok = pyramid_read(data, 0); break;
                    case 6: ok = pac_read(data, 0); break;
                    case 7: ok = raw_read_to_buffer(data, sizeof(data), 0, &length); break;
                }
                if (failed) { assert(!ok); assert(radio == 0); }
                assert(live == 0);
            }
    }
    puts("LF allocation failures: PASS");
}
"""
        (temp / "test.c").write_text(harness)
        includes = [temp, SRC / "rfid", SRC / "rfid/nfctag", SRC / "rfid/nfctag/lf", SRC / "rfid/nfctag/lf/protocols", SRC / "rfid/nfctag/lf/utils"]
        sources = [SRC / f"rfid/nfctag/lf/protocols/{name}.c" for name in ["em410x", "hidprox", "viking", "jablotron", "idteck", "ioprox", "pyramid", "pac", "wiegand"]]
        sources += [SRC / f"rfid/nfctag/lf/utils/{name}.c" for name in ["manchester", "diphase", "fskdemod", "circular_buffer", "psk1"]]
        sources += [SRC / "rfid/hex_utils.c", SRC / "rfid/parity.c"]
        binary = temp / "test"
        subprocess.run(["gcc", "-std=gnu11", "-g", "-O1", "-fno-pie", "-no-pie", "-fsanitize=address,undefined", "-fno-sanitize-recover=all", *[f"-I{p}" for p in includes], str(temp / "test.c"), *map(str, sources), "-Wl,--wrap=malloc,--wrap=free,--wrap=calloc", "-lm", "-o", str(binary)], check=True)
        results = [subprocess.run([str(binary), str(mode)]).returncode for mode in range(3)]
        assert results == [0, 0, 0], results


if __name__ == "__main__":
    main()

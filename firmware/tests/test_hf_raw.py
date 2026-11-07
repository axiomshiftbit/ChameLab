"""Exercise the real raw-HF command handler with a mocked RF driver."""
from pathlib import Path
import subprocess
import tempfile


def main():
    source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
    start = source.index("static data_frame_tx_t *cmd_processor_hf14a_raw(")
    end = source.index("\nstatic ", start + 1)
    handler = source[start:end]
    harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#define PACKED __attribute__((packed))
#define DEF_FIFO_LENGTH 64
#define DEF_CRC_LENGTH 2
#define U8ARR_BIT_LEN(a) (sizeof(a) * 8)
#define U16NTOHS(n) __builtin_bswap16(n)
#define NRF_LOG_INFO(...) ((void)0)
#define STATUS_PAR_ERR 0x60
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static int calls;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t len, uint8_t *data) {
    reply.status = status;
    return &reply;
}
static uint8_t pcd_14a_reader_raw_cmd(bool open, bool wait, bool crc, bool select, bool keep, bool check,
        uint16_t timeout, uint16_t bits, uint8_t *tx, uint8_t *rx, uint16_t *length, uint16_t capacity) {
    calls++;
    for (unsigned i = 0; i < (bits + 7u) / 8u; i++) assert(tx[i] == 0xA5);
    if (crc) { tx[bits / 8] = 0x55; tx[bits / 8 + 1] = 0xAA; }
    return 0;
}
'''
    tests = r'''
int main(void) {
    uint8_t data[80], saved[80];
    unsigned cases = 0;
    for (unsigned available = 0; available <= 65; available++) {
        for (unsigned bits = 0; bits <= 520; bits++) {
            for (unsigned crc = 0; crc <= 1; crc++) {
                memset(data, 0xA5, sizeof(data));
                data[0] = crc ? 0x20 : 0;
                data[1] = 0; data[2] = 100;
                data[3] = bits >> 8; data[4] = bits;
                memcpy(saved, data, sizeof(data));
                calls = 0;
                bool valid = bits <= 512 && (bits + 7) / 8 <= available
                    && (!crc || (bits > 0 && bits % 8 == 0 && bits <= 496));
                data_frame_tx_t *r = cmd_processor_hf14a_raw(2010, 0, 5 + available, data);
                assert(calls == (int)valid);
                assert(r->status == (valid ? 0 : STATUS_PAR_ERR));
                assert(memcmp(saved, data, sizeof(data)) == 0);
                cases++;
            }
        }
    }
    for (unsigned length = 0; length < 5; length++) {
        calls = 0;
        assert(cmd_processor_hf14a_raw(2010, 0, length, data)->status == STATUS_PAR_ERR);
        assert(calls == 0);
    }
    printf("Raw HF bounds and CRC buffer: %u cases passed\n", cases + 5);
}
'''
    with tempfile.TemporaryDirectory(prefix="chamelab-hf-") as directory:
        path = Path(directory)
        (path / "test.c").write_text(harness + handler + tests)
        subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                        "-fsanitize=address,undefined", "-fno-omit-frame-pointer",
                        str(path / "test.c"), "-o", str(path / "test")], check=True)
        subprocess.run([str(path / "test")], check=True)


if __name__ == "__main__":
    main()

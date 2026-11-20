"""3037 wire compatibility: scan, legacy EM verification, invalid packet rejection."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / 'application/src/app_cmd.c').read_text()
start = source.index('static data_frame_tx_t *cmd_processor_lf_3037(')
end = source.index('\nstatic ', start + 1)
harness = r'''
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#define STATUS_PAR_ERR 0x60
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static int scans, checks;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t n, uint8_t *data) {
    reply.status = status; return &reply;
}
static data_frame_tx_t *cmd_processor_lf_auto_scan(uint16_t cmd, uint16_t status, uint16_t n, uint8_t *data) {
    assert(cmd == 3037 && n == 0); scans++; return data_frame_make(cmd, 0x40, 0, 0);
}
static data_frame_tx_t *cmd_processor_em4x05_check_password(uint16_t cmd, uint16_t status, uint16_t n, uint8_t *data) {
    assert(cmd == 3037 && n == 4); checks++; return data_frame_make(cmd, 0x42, 0, 0);
}
'''
tests = r'''
int main(void) {
    uint8_t data[32] = {0};
    for (int length = 0; length < 32; length++) {
        scans = checks = 0;
        data_frame_tx_t *r = cmd_processor_lf_3037(3037, 0, length, data);
        assert(scans == (length == 0)); assert(checks == (length == 4));
        if (length != 0 && length != 4) assert(r->status == STATUS_PAR_ERR);
    }
    puts("LF/EM 3037 compatibility and malformed packets passed");
}
'''
with tempfile.TemporaryDirectory(prefix='chamelab-lf-compat-') as directory:
    path = Path(directory)
    (path / 'test.c').write_text(harness + source[start:end] + tests)
    subprocess.run(['cc', '-std=c99', '-Wall', '-Wextra', '-Werror', '-Wno-unused-parameter',
                    '-fsanitize=address,undefined', str(path / 'test.c'), '-o', str(path / 'test')], check=True)
    subprocess.run([str(path / 'test')], check=True)

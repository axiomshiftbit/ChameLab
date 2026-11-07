"""Optional LF scan hints must not dereference an empty command payload."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
def handler(name):
    start = source.index("static data_frame_tx_t *cmd_processor_" + name + "_scan(")
    return source[start:source.index("\n}", start) + 2]
harness = r'''
#include <assert.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#define STATUS_PAR_ERR 0x60
#define STATUS_LF_TAG_OK 0x40
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static int calls, hint;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t length, uint8_t *data) {
    (void)cmd; (void)length; (void)data; reply.status = status; return &reply;
}
static uint16_t scan_hidprox(uint8_t *data, uint8_t format) { (void)data; calls++; hint = format; return STATUS_LF_TAG_OK; }
static uint16_t scan_ioprox(uint8_t *data, uint8_t format) { return scan_hidprox(data, format); }
'''
tests = r'''
int main(void) {
    data_frame_tx_t *(*handlers[])(uint16_t,uint16_t,uint16_t,uint8_t *) = {cmd_processor_hidprox_scan,cmd_processor_ioprox_scan};
    for (unsigned i=0; i<2; i++) {
        uint8_t data[2] = {26, 99};
        calls=0; assert(handlers[i](0,0,0,NULL)->status == STATUS_LF_TAG_OK); assert(calls==1 && hint==0);
        calls=0; assert(handlers[i](0,0,0,data)->status == STATUS_LF_TAG_OK); assert(calls==1 && hint==0);
        calls=0; assert(handlers[i](0,0,1,data)->status == STATUS_LF_TAG_OK); assert(calls==1 && hint==26);
        calls=0; assert(handlers[i](0,0,2,data)->status == STATUS_PAR_ERR); assert(calls==0);
    }
    puts("LF scan payload bounds: 8 checks passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-lf-scan-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + handler("hidprox") + handler("ioprox") + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter", "-fsanitize=address,undefined",
                    str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True, timeout=5)

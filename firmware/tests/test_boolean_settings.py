"""Real settings handlers reject short/long packets and all non-boolean values."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
names = ["set_ble_pairing_enable", "mf1_set_gen1a_mode", "mf1_set_gen2_mode",
         "mf1_set_block_anti_coll_mode", "mf0_ntag_set_uid_mode"]

def handler(name):
    start = source.index("static data_frame_tx_t *cmd_processor_" + name + "(")
    return source[start:source.index("\n}", start) + 2]

harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#define STATUS_PAR_ERR 0x60
#define STATUS_SUCCESS 0x68
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static int calls, value;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t length, uint8_t *data) {
    (void)cmd; (void)length; (void)data; reply.status = status; return &reply;
}
static void set(uint8_t v) { calls++; value=v; }
#define settings_set_ble_pairing_enable set
#define nfc_tag_mf1_set_gen1a_magic_mode set
#define nfc_tag_mf1_set_gen2_magic_mode set
#define nfc_tag_mf1_set_use_mf1_coll_res set
static bool nfc_tag_mf0_ntag_set_uid_mode(bool v) { set(v); return true; }
'''
tests = r'''
int main(void) {
    for (unsigned i=0; i<sizeof(handlers)/sizeof(handlers[0]); i++) {
        calls=0;
        assert(handlers[i](0,0,0,NULL)->status == STATUS_PAR_ERR);
        assert(calls==0);
        for (unsigned length=0; length<=2; length++)
        for (unsigned v=0; v<=255; v++) {
            uint8_t data[2]={(uint8_t)v,0};
            bool valid=length==1 && v<=1;
            calls=0;
            assert(handlers[i](0,0,length,data)->status == (valid ? STATUS_SUCCESS : STATUS_PAR_ERR));
            assert(calls == (valid ? 1 : 0));
            if (valid) assert(value==(int)v);
        }
    }
    puts("Boolean settings bounds: 3845 cases passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-settings-") as directory:
    path = Path(directory)
    declarations = "\nstatic data_frame_tx_t *(*handlers[])(uint16_t,uint16_t,uint16_t,uint8_t *) = {" + ",".join("cmd_processor_" + n for n in names) + "};\n"
    (path / "test.c").write_text(harness + "\n".join(handler(n) for n in names) + declarations + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter", "-fsanitize=address,undefined",
                    str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True, timeout=5)

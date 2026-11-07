"""Counter tearing and authentication failure flags must remain independent."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
def handler(name):
    start = source.index("static data_frame_tx_t *cmd_processor_" + name + "(")
    return source[start:source.index("\n}", start) + 2]

harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdio.h>
#define STATUS_PAR_ERR 1
#define STATUS_INVALID_SLOT_TYPE 2
#define STATUS_SUCCESS 0
#define MF0_NTAG_AUTHLIM_OFF_IN_CTR 3
#define MF0_NTAG_AUTHLIM_MASK_IN_CTR 0x0F
#define MF0_NTAG_TEARING_MASK_IN_AUTHLIM 0x80
static uint8_t counter[4];
typedef struct { uint16_t status, length; uint8_t data[4]; } data_frame_tx_t;
static data_frame_tx_t reply;
static uint8_t *nfc_tag_mf0_ntag_get_counter_data_by_index(uint8_t index) { return index==0 ? counter : NULL; }
static data_frame_tx_t *data_frame_make(uint16_t cmd,uint16_t status,uint16_t length,uint8_t *data) {
    (void)cmd; reply.status=status; reply.length=length;
    assert(length<=4); if(length) memcpy(reply.data,data,length); return &reply;
}
'''
tests = r'''
int main(void) {
    assert(cmd_processor_mf0_ntag_get_counter_data(0,0,0,NULL)->status==STATUS_PAR_ERR);
    assert(cmd_processor_mf0_ntag_set_counter_data(0,0,0,NULL)->status==STATUS_PAR_ERR);
    uint8_t index=0;
    for(unsigned flags=0;flags<256;flags++) {
        counter[3]=flags;
        assert(cmd_processor_mf0_ntag_get_counter_data(0,0,1,&index)->status==0);
        assert(reply.length==4 && reply.data[3]==((flags&0x80)?0:0xBD));
        assert(counter[3]==flags); /* reading does not clear either flag */
        assert(cmd_processor_mf0_ntag_reset_auth_cnt(0,0,0,NULL)->status==0);
        assert(reply.length==1 && reply.data[0]==(flags&0x0F));
        assert(counter[3]==(flags&0xF0));
        counter[3]=flags;
        uint8_t update[]={0x80,1,2,3};
        assert(cmd_processor_mf0_ntag_set_counter_data(0,0,4,update)->status==0);
        assert(counter[0]==1 && counter[1]==2 && counter[2]==3 && counter[3]==(flags&0x7F));
    }
    index=1;
    assert(cmd_processor_mf0_ntag_get_counter_data(0,0,1,&index)->status==STATUS_INVALID_SLOT_TYPE);
    puts("Ultralight counter flags: all 256 combinations passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-counter-") as directory:
    path = Path(directory)
    names = ["mf0_ntag_get_counter_data", "mf0_ntag_set_counter_data", "mf0_ntag_reset_auth_cnt"]
    (path / "test.c").write_text(harness + "\n".join(handler(n) for n in names) + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                    "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True, timeout=5)

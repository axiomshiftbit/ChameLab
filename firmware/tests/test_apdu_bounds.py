"""Exercise the real APDU handlers with synthetic frames and ASan/UBSan."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
source = (root / "application/src/app_cmd.c").read_text()

def function(name, text=source):
    start = text.index(name + "(")
    start = text.rfind("\n", 0, start) + 1
    return text[start:text.index("\n}", start) + 2]

emulator = (root / "application/src/rfid/nfctag/hf/nfc_14a_4.c").read_text()
add_response = function("nfc_tag_14a_4_add_static_response", emulator).replace("nfc_tag_14a_4_add_static_response", "add_response_impl")

harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <string.h>
#define STATUS_PAR_ERR 0x60
#define STATUS_SUCCESS 0x68
#define NFC_14A_4_MAX_STATIC_CMD_LEN 16
#define NFC_14A_4_MAX_LARGE_RESP_LEN 260
#define NFC_14A_4_MAX_STATIC_RESP_LEN 253
#define NFC_14A_4_MAX_STATIC_RESPONSES 12
#define NFC_14A_4_MAX_LARGE_RESPONSES 4
typedef struct { uint8_t cmd_len, cmd[16], resp_len, resp[253]; } nfc_tag_14a_4_static_response_t;
typedef struct { uint8_t cmd_len, cmd[16]; uint16_t resp_len; uint8_t resp[260]; } nfc_tag_14a_4_large_response_t;
static nfc_tag_14a_4_static_response_t m_static_resp[12];
static nfc_tag_14a_4_large_response_t m_large_resp[4];
static unsigned m_static_resp_count, m_large_resp_count;
static struct { unsigned static_resp_count; nfc_tag_14a_4_static_response_t static_resp[12]; } *m_tag_information;
static bool add_response_impl(const uint8_t *, uint8_t, const uint8_t *, uint16_t);
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static unsigned stored_len, calls;
static data_frame_tx_t *data_frame_make(uint16_t cmd, uint16_t status, uint16_t length, uint8_t *data) {
    (void)cmd; (void)length; (void)data; reply.status=status; return &reply;
}
static void nfc_tag_14a_4_clear_static_responses(void) { calls++; }
static bool nfc_tag_14a_4_add_static_response(const uint8_t *cmd, uint8_t clen, const uint8_t *resp, uint16_t rlen) {
    stored_len=rlen; calls++; return add_response_impl(cmd,clen,resp,rlen);
}
'''
tests = r'''
int main(void) {
    assert(cmd_processor_hf14a_4_static_resp(0,0,0,NULL)->status==STATUS_PAR_ERR);
    uint8_t packet[280]={1,0,0xFF,0xFF};
    assert(cmd_processor_hf14a_4_static_resp(0,0,4,packet)->status==STATUS_PAR_ERR);
    assert(calls==0);
    for(unsigned n=0;n<=65535;n++) {
        m_static_resp_count=m_large_resp_count=0;
        packet[2]=(uint8_t)(n>>8); packet[3]=(uint8_t)n; calls=0;
        unsigned length=n<=260 ? n+4 : 4;
        uint16_t status=cmd_processor_hf14a_4_static_resp(0,0,length,packet)->status;
        assert(status==(n<=260 ? STATUS_SUCCESS : STATUS_PAR_ERR));
        assert(calls==(n<=260 ? 1u : 0u));
        if(n<=260) assert(stored_len==n);
    }
    packet[0]=17; packet[18]=0; packet[19]=0;
    assert(cmd_processor_hf14a_4_static_resp(0,0,20,packet)->status==STATUS_PAR_ERR);
    m_static_resp_count=m_large_resp_count=0;
    for(unsigned i=0;i<12;i++) assert(add_response_impl(packet,1,packet,253));
    assert(!add_response_impl(packet,1,packet,253));
    for(unsigned i=0;i<4;i++) assert(add_response_impl(packet,1,packet,260));
    assert(!add_response_impl(packet,1,packet,260));
    assert(!add_response_impl(packet,17,packet,260));
    assert(!add_response_impl(packet,1,packet,261));
    puts("APDU static response: 65536 lengths passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-apdu-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + add_response + function("cmd_processor_hf14a_4_static_resp") + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                    "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True, timeout=10)

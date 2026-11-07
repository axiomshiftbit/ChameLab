"""Exercise real HF selection with absent/corrupt slot metadata."""
from pathlib import Path
import subprocess
import tempfile

src = Path(__file__).resolve().parents[1] / "application/src/rfid/nfctag/hf"
source = (src / "nfc_14a.c").read_text()
start = source.index("void nfc_tag_14a_data_process(")
function = source[start:source.index("\n}", start) + 2]
start = source.index("const uint16_t ats_fsdi_table[]")
table = source[start:source.index("\n};", start) + 3]
header = (src / "nfc_14a.h").read_text().replace('#include "tag_emulation.h"', '')
prefix = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#include <assert.h>
#include <string.h>
'''
harness = r'''
static struct { struct { uint32_t AMOUNT; } RXD; } radio;
#define NRF_NFCT (&radio)
#define NFCT_RXD_AMOUNT_RXDATABITS_Msk 7
#define NFCT_RXD_AMOUNT_RXDATABYTES_Msk 0xfff8
#define NRF_LOG_INFO(...) ((void)0)
static nfc_tag_14a_state_t m_tag_state_14a;
static nfc_tag_14a_handler_t m_tag_handler;
static nfc_tag_14a_sniff_cb_t m_sniff_cb;
static bool m_sniff_passive;
static uint8_t m_uid_incomplete_sak[] = {4,0xda,0x17};
static int transmitted, forwarded;
static nfc_tag_14a_coll_res_entity_t entity;
static nfc_tag_14a_coll_res_reference_t reference = {&entity.size, entity.atqa, entity.sak, entity.uid, &entity.ats};
static bool present;
static nfc_tag_14a_coll_res_reference_t *coll(void) { return present ? &reference : NULL; }
static void state(uint8_t *p,uint16_t bits) { forwarded++; }
uint16_t nfc_tag_14a_unwrap_frame(const uint8_t *p,size_t bits,uint8_t *out,uint8_t *par) { return bits; }
bool nfc_tag_14a_checks_crc(uint8_t *p,size_t n) { return true; }
void nfc_tag_14a_append_bcc(uint8_t *p,size_t n) { p[n]=0;for(size_t i=0;i<n;i++) p[n]^=p[i]; }
void nfc_tag_14a_tx_bytes(uint8_t *p,uint32_t n,bool crc) { assert(n<=255);transmitted++; }
void nfc_tag_14a_tx_nbit(uint8_t p,uint32_t n) { transmitted++; }
'''
tests = r'''
int main(int argc,char **argv) {
    int mode=atoi(argv[1]); uint8_t data[9]={0x93,0x20};
    m_tag_handler.get_coll_res=coll;m_tag_handler.cb_state=state;
    radio.RXD.AMOUNT=16;
    if(mode==0) {
        m_tag_state_14a=NFC_TAG_STATE_14A_READY;
        nfc_tag_14a_data_process(data);
        assert(m_tag_state_14a==NFC_TAG_STATE_14A_IDLE && transmitted==0);
    } else if(mode==1) {
        m_tag_state_14a=NFC_TAG_STATE_14A_ACTIVE;
        data[0]=0xe0; radio.RXD.AMOUNT=32;
        nfc_tag_14a_data_process(data);
        assert(m_tag_state_14a==NFC_TAG_STATE_14A_IDLE && transmitted==0);
    } else if(mode==2) {
        present=true;
        for(unsigned size=0;size<256;size++) {
            entity.size=size;transmitted=0;m_tag_state_14a=NFC_TAG_STATE_14A_READY;
            nfc_tag_14a_data_process(data);
            assert(transmitted==((size==4 || size==7 || size==10)?1:0));
        }
    } else {
        m_tag_state_14a=NFC_TAG_STATE_14A_PROPRIETARY;
        nfc_tag_14a_data_process(data);assert(forwarded==1);
    }
    puts("HF selection metadata: PASS");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-hf-selection-") as directory:
    temp = Path(directory)
    (temp / "test.c").write_text(prefix + header + table + harness + function + tests)
    binary = temp / "test"
    subprocess.run(["gcc", "-std=c99", "-O1", "-g", "-fno-pie", "-no-pie", "-fsanitize=address,undefined", "-fno-sanitize-recover=all", str(temp / "test.c"), "-o", str(binary)], check=True)
    results = [subprocess.run([str(binary), str(mode)]).returncode for mode in range(4)]
    assert results == [0]*4, results

"""Compile actual ISO-DEP/EMV handlers; RF traffic is synthetic and bounded."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
def function(name):
    start = source.index(name + "(")
    start = source.rfind("\n", 0, start) + 1
    return source[start:source.index("\n}", start) + 2]

harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#define STATUS_HF_TAG_OK 0
#define STATUS_HF_TAG_NO 1
#define STATUS_HF_ERR_STAT 2
#define STATUS_PAR_ERR 3
#define DEF_COM_TIMEOUT 100
#define NETDATA_MAX_DATA_LENGTH 4096
#define PCD_TRANSCEIVE 0x0c
#define PCD_IDLE 0
#define ComIrqReg 1
#define CommandReg 2
#define FIFOLevelReg 3
#define BitFramingReg 4
typedef struct { uint32_t time; bool busy; } autotimer;
static autotimer timers[2];
static unsigned ticks;
static autotimer *bsp_obtain_timer(uint32_t start) {
    for (unsigned i=0;i<2;i++) if(!timers[i].busy) { timers[i]=(autotimer){start,true}; return &timers[i]; }
    return NULL;
}
static void bsp_return_timer(autotimer *t) { assert(t->busy); t->busy=false; }
#define NO_TIMEOUT_1MS(t,n) ((t)->time <= (n))
static void bsp_delay_ms(unsigned n) { (void)n; }
static void bsp_wdt_feed(void) {}
static void write_register_single(unsigned r, unsigned v) { (void)r; (void)v; }
static unsigned read_register_single(unsigned r) { (void)r; return PCD_IDLE; }
#define set_register_mask write_register_single
#define clear_register_mask write_register_single
static void pcd_14a_reader_antenna_off(void) {}
static void pcd_14a_reader_antenna_on(void) {}
static void pcd_14a_reader_reset(void) {}
static unsigned timeout;
static void pcd_14a_reader_timeout_set(unsigned v) { timeout=v; }
typedef struct { uint8_t uid_len,uid[10],atqa[2],sak,ats_len,ats[254]; } picc_14a_tag_t;
static uint8_t pcd_14a_reader_scan_auto(picc_14a_tag_t *t) { memset(t,0,sizeof(*t)); t->uid_len=4; return 0; }
typedef struct { uint16_t status,length; uint8_t data[4096]; } data_frame_tx_t;
static data_frame_tx_t reply;
static data_frame_tx_t *data_frame_make(uint16_t cmd,uint16_t status,uint16_t length,uint8_t *data) {
    (void)cmd; assert(length<=sizeof(reply.data)); reply.status=status; reply.length=length;
    if(length) memcpy(reply.data,data,length);
    return &reply;
}
static void crc_14a_calculate(uint8_t *data,uint16_t size,uint8_t *out) {
    uint16_t crc=0x6363;
    for(unsigned i=0;i<size;i++) { uint8_t c=data[i]^(uint8_t)crc; c^=c<<4; crc=(crc>>8)^((uint16_t)c<<8)^((uint16_t)c<<3)^(c>>4); }
    out[0]=(uint8_t)crc; out[1]=crc>>8;
}
static void crc_14a_append(uint8_t *data,uint16_t size) { crc_14a_calculate(data,size,data+size); }
static struct { uint8_t data[270]; uint16_t bits; } queue[80];
static unsigned queued,received,fail_at;
static uint8_t sent[80][64], sent_len[80];
static uint8_t pcd_14a_reader_bytes_transfer(unsigned mode,uint8_t *tx,unsigned size,uint8_t *rx,uint16_t *bits,unsigned capacity) {
    (void)mode; assert(size<=64 && received<80);
    memcpy(sent[received],tx,size); sent_len[received]=(uint8_t)size;
    for(unsigned i=0;i<2;i++) if(timers[i].busy) timers[i].time+=ticks;
    if(received==fail_at || received>=queued) { received++; return STATUS_HF_TAG_NO; }
    unsigned n=queue[received].bits;
    assert(n<=capacity || n==2168); /* deliberate impossible driver length */
    memcpy(rx,queue[received].data,(n+7)/8>270?270:(n+7)/8);
    *bits=n; received++; return 0;
}
static void reset(void) { queued=received=0; fail_at=999; ticks=1; memset(timers,0,sizeof(timers)); }
static void frame(uint8_t pcb,const uint8_t *data,unsigned size) {
    assert(queued<80 && size<=267); queue[queued].data[0]=pcb;
    memcpy(queue[queued].data+1,data,size); crc_14a_append(queue[queued].data,size+1);
    queue[queued++].bits=(size+3)*8;
}
'''
tests = r'''
static bool exchange(uint16_t *length,uint8_t *block) {
    uint8_t tx[64],rx[270],chain[512],*result=NULL, apdu[]={0,0xa4}; uint16_t bits=0;
    bool ok=tcl_apdu_(apdu,sizeof(apdu),&result,length,tx,rx,chain,&bits,block);
    assert(!timers[0].busy && !timers[1].busy && timeout==DEF_COM_TIMEOUT);
    if(!ok) assert(*length==0 && result==NULL);
    return ok;
}
static void transport_tests(void) {
    uint8_t data[267]={0x90,0}; uint16_t n; uint8_t block;
    reset(); block=0; frame(2,data,2); assert(exchange(&n,&block) && n==2 && block==1);
    reset(); block=0; frame(0x22,data,256); frame(3,data,256);
    assert(exchange(&n,&block) && n==512 && block==0); assert(sent[1][0]==0xA3);
    reset(); block=0; frame(0x22,data,256); frame(3,data,257); assert(!exchange(&n,&block));
    reset(); block=0; frame(0x22,data,2); frame(3,data,2); queue[1].data[4]^=1; assert(!exchange(&n,&block));
    reset(); block=0; frame(0x22,data,2); frame(3,data,2); fail_at=1; assert(!exchange(&n,&block));
    reset(); block=0; frame(0x22,data,0); assert(!exchange(&n,&block) && received==1);
    reset(); block=0; frame(0x22,data,2); frame(2,data,2); assert(!exchange(&n,&block)); /* duplicate */
    reset(); block=0; frame(0xF2,(uint8_t[]){1},1); frame(2,data,2);
    assert(exchange(&n,&block) && n==2 && sent[1][0]==0xF2);
    reset(); block=0; for(unsigned i=0;i<8;i++) frame(0xF2,(uint8_t[]){1},1);
    assert(!exchange(&n,&block) && received==5);
    reset(); block=0; frame(0xF2,data,0); assert(!exchange(&n,&block));
    reset(); block=0; frame(0xF2,(uint8_t[]){60},1); assert(!exchange(&n,&block));
    reset(); block=0; for(unsigned i=0;i<70;i++) frame(0x22|(i&1),data,1);
    assert(!exchange(&n,&block) && received==64);
    reset(); block=0; ticks=3000; for(unsigned i=0;i<8;i++) frame(0x22|(i&1),data,1);
    assert(!exchange(&n,&block) && received==2);
    for(unsigned bits=0;bits<24;bits++) { reset(); block=0; frame(2,data,2); queue[0].bits=bits; assert(!exchange(&n,&block)); }
    reset(); block=0; frame(2,data,2); queue[0].bits=25; assert(!exchange(&n,&block));
    reset(); block=0; frame(2,data,2); queue[0].bits=2168; assert(!exchange(&n,&block));
    reset(); frame(2,data,2); assert(cmd_processor_hf14a_4_reader_apdu(0,0,1,data)->status==0);
    reset(); frame(0x22,data,2); assert(cmd_processor_hf14a_4_reader_apdu(0,0,1,data)->status==STATUS_HF_ERR_STAT && reply.length==0);
}
static void tlv_tests(void) {
    const uint8_t *value=NULL; uint16_t size=0;
    uint8_t nested[]={0x6F,0x81,7,0xA5,5,0x4F,3,1,2,3};
    assert(emv_tlv_find_(nested,sizeof(nested),0x4F,0,&value,&size)==1 && size==3 && value[2]==3);
    assert(emv_tlv_find_((uint8_t[]){0x50,3,0x4F,1,2},5,0x4F,0,&value,&size)==0);
    for(unsigned len=1;len<sizeof(nested);len++) assert(emv_tlv_find_(nested,len,0x4F,0,&value,&size)==-1);
    uint8_t pdol[44], len=0;
    assert(emv_pdol_((uint8_t[]){0x9F,2,6,0x9F,0x66,4},6,pdol,&len) && len==10 && pdol[0]==0 && pdol[6]==0xA0);
    assert(!emv_pdol_((uint8_t[]){0x9F,2,255},3,pdol,&len));
    assert(!emv_pdol_((uint8_t[]){0x9F,0x81},2,pdol,&len));
    uint32_t seed=1234; uint8_t fuzz[512];
    for(unsigned t=0;t<20000;t++) {
        unsigned n=t%512;
        for(unsigned i=0;i<n;i++) { seed=seed*1664525u+1013904223u; fuzz[i]=seed>>24; }
        int8_t found=emv_tlv_find_(fuzz,n,0x4F,0,&value,&size);
        if(found==1) assert(value>=fuzz && value+size<=fuzz+n);
        if(emv_pdol_(fuzz,n,pdol,&len)) assert(len<=44);
    }
}
static void emv_prefix(void) {
    reset(); frame(2,(uint8_t[]){0x6F,3,0x4F,1,0x42,0x90,0},7);
    frame(2,(uint8_t[]){0x6F,0,0x90,0},4); /* field reset starts a new session */
}
static unsigned pair_count(void) {
    unsigned i=10, count=0; /* synthetic tag header, including num_apdus */
    while(i<reply.length) { i+=1+reply.data[i]; assert(i+2<=reply.length); unsigned n=reply.data[i]|reply.data[i+1]<<8; i+=2+n; assert(i<=reply.length); count++; }
    assert(i==reply.length && count==reply.data[9]); return count;
}
static void emv_tests(void) {
    emv_prefix(); frame(3,(uint8_t[]){0x80,6,0,0,8,255,255,0,0x90,0},10);
    frame(2,(uint8_t[]){0x90,0},2);
    assert(cmd_processor_hf14a_4_emv_scan(0,0,0,NULL)->status==0 && pair_count()==4 && received==4);
    assert(sent[3][3]==255 && sent[3][4]==12); /* AFL after the two-byte AIP */
    emv_prefix(); frame(3,(uint8_t[]){0x77,6,0x94,4,8,1,255,0,0x90,0},10);
    for(unsigned i=0;i<40;i++) frame(2|(i&1),(uint8_t[]){0x90,0},2);
    assert(cmd_processor_hf14a_4_emv_scan(0,0,0,NULL)->status==0 && pair_count()==35 && received==35);
    emv_prefix(); frame(3,(uint8_t[]){0x77,3,0x94,8,8,0x90,0},7);
    assert(cmd_processor_hf14a_4_emv_scan(0,0,0,NULL)->status==0 && pair_count()==3 && received==3);
    reset(); uint8_t big[256]={0}; big[254]=0x90;
    frame(0x22,big,256); frame(3,big,256);
    assert(cmd_processor_hf14a_4_emv_scan(0,0,0,NULL)->status==0 && pair_count()==1 && received==2);
    emv_prefix(); frame(3,(uint8_t[]){0x80,6,0,0,8,1,32,0,0x90,0},10);
    uint8_t record[250]={0}; record[248]=0x90;
    for(unsigned i=0;i<32;i++) frame(2|(i&1),record,250);
    assert(cmd_processor_hf14a_4_emv_scan(0,0,0,NULL)->status==0 && pair_count()<35 && received<35);
    assert(!timers[0].busy && !timers[1].busy && timeout==DEF_COM_TIMEOUT);
}
int main(void) { transport_tests(); tlv_tests(); emv_tests(); puts("ISO-DEP bounds, CRC, chaining, WTX, EMV and 20000 malformed TLV/PDOL cases passed"); }
'''
names = ["tcl_apdu_", "cmd_processor_hf14a_4_reader_apdu", "emv_tlv_find_", "emv_pdol_", "emv_success_", "cmd_processor_hf14a_4_emv_scan"]
with tempfile.TemporaryDirectory(prefix="chamelab-tcl-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + "\n".join(function(n) for n in names) + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                    "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True, timeout=15)

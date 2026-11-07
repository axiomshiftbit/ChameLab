"""Exercise real slot metadata handlers with missing/corrupt records and maximum ATS."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
names = ["hf14a_set_anti_coll_data", "hf14a_get_anti_coll_data", "get_slot_tag_nick", "get_all_slot_nicks", "set_slot_tag_nick"]

def handler(name):
    start = source.index("static data_frame_tx_t *cmd_processor_" + name + "(")
    return source[start:source.index("\n}", start) + 2]

harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define STATUS_SUCCESS 0x68
#define STATUS_PAR_ERR 0x60
#define STATUS_INVALID_SLOT_TYPE 0x61
#define STATUS_FLASH_READ_FAIL 0x62
#define STATUS_FLASH_WRITE_FAIL 0x63
#define TAG_MAX_SLOT_NUM 8
#define TAG_SENSE_HF 2
#define TAG_SENSE_LF 1
typedef uint8_t nfc_tag_14a_uid_size;
typedef struct { uint8_t data[255]; uint8_t length; } nfc_14a_ats_t;
typedef struct { nfc_tag_14a_uid_size *size; uint8_t *atqa, *sak, *uid; nfc_14a_ats_t *ats; } nfc_tag_14a_coll_res_reference_t;
typedef struct { uint16_t id,key; } fds_slot_record_map_t;
typedef struct { uint16_t status,length; uint8_t data[1024]; } data_frame_tx_t;
static data_frame_tx_t reply;
static uint8_t uid_size=4, atqa[2], sak, uid[10];
static nfc_14a_ats_t ats;
static nfc_tag_14a_coll_res_reference_t info={&uid_size,atqa,&sak,uid,&ats};
static bool present=true;
static uint16_t record_size=36;
static uint8_t record_length=5;
static uint8_t written[36];
static data_frame_tx_t *data_frame_make(uint16_t cmd,uint16_t status,uint16_t length,uint8_t *data) {
    (void)cmd; reply.status=status; reply.length=length;
    if(length) memcpy(reply.data,data,length);
    return &reply;
}
static bool is_valid_uid_size(uint8_t size) { return size==4 || size==7 || size==10; }
static nfc_tag_14a_coll_res_reference_t *get_coll_res_data(bool write) { (void)write;return present?&info:NULL; }
static void get_fds_map_by_slot_sense_type_for_nick(uint8_t slot,uint8_t sense,fds_slot_record_map_t *map) {
    map->id=slot;map->key=sense;
}
static bool fds_read_sync(uint16_t id,uint16_t key,uint16_t *length,uint8_t *buffer) {
    (void)id;(void)key;assert(record_size<=*length);*length=record_size;
    memset(buffer,0x55,record_size);
    if(record_size) buffer[0]=record_length;
    return true;
}
static bool fds_write_sync(uint16_t id,uint16_t key,uint16_t length,void *buffer) {
    (void)id;(void)key;assert(length==36);memcpy(written,buffer,length);return true;
}
'''
tests = r'''
int main(int argc,char **argv) {
    assert(argc==2);int mode=atoi(argv[1]);uint8_t request[270]={4,1,2,3,4,0,0,0,0};
    if(mode==0) {
        present=false;
        assert(cmd_processor_hf14a_set_anti_coll_data(0,0,9,request)->status==STATUS_INVALID_SLOT_TYPE);
    } else if(mode==1) {
        for(unsigned size=4;size<=10;size+=3) for(unsigned len=0;len<=255;len++) {
            memset(request,0x55,sizeof(request));request[0]=size;request[size+4]=len;
            assert(cmd_processor_hf14a_set_anti_coll_data(0,0,size+5+len,request)->status==STATUS_SUCCESS);
            assert(cmd_processor_hf14a_get_anti_coll_data(0,0,0,NULL)->status==STATUS_SUCCESS);
            assert(reply.length==size+5+len && memcmp(reply.data,request,reply.length)==0);
        }
    } else if(mode==2) {
        request[0]=0;request[1]=TAG_SENSE_HF;
        for(unsigned size=0;size<=36;size++) for(unsigned len=0;len<=255;len++) {
            record_size=size;record_length=len;
            bool valid=size>0 && len<=32 && len<size;
            cmd_processor_get_slot_tag_nick(0,0,2,request);
            assert(reply.status==(valid?STATUS_SUCCESS:STATUS_FLASH_READ_FAIL));
            if(valid) assert(reply.length==len);
        }
    } else if(mode==3) {
        for(unsigned size=0;size<=36;size++) for(unsigned len=0;len<=255;len++) {
            record_size=size;record_length=len;
            bool valid=size>0 && len<=32 && len<size;
            cmd_processor_get_all_slot_nicks(0,0,0,NULL);
            assert(reply.status==STATUS_SUCCESS);
            assert(reply.length==16*(1+(valid?len:0)));
            for(unsigned slot=0,pos=0;slot<16;slot++) {
                assert(reply.data[pos]==(valid?len:0));pos+=1+reply.data[pos];
                assert(pos<=reply.length);
            }
        }
    } else if(mode==4) {
        for(unsigned size=0;size<=255;size++) if(!is_valid_uid_size(size)) {
            uid_size=size;
            assert(cmd_processor_hf14a_get_anti_coll_data(0,0,0,NULL)->status==STATUS_PAR_ERR);
        }
    } else {
        request[0]=0;request[1]=TAG_SENSE_HF;request[2]='X';
        assert(cmd_processor_set_slot_tag_nick(0,0,3,request)->status==STATUS_SUCCESS);
        assert(written[0]==1 && written[1]=='X');
        for(unsigned i=2;i<sizeof(written);i++) assert(written[i]==0);
    }
    puts("PASS: slot metadata boundaries");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-slot-metadata-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + "\n".join(handler(n) for n in names) + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                    "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    results = [subprocess.run([str(path / "test"), str(i)], timeout=15).returncode for i in range(6)]
    assert results == [0]*6, results

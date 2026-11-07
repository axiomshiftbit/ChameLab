"""Failed flash writes must stay dirty, report failure and preserve the active slot."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1] / "application/src"
source = (root / "rfid/nfctag/tag_emulation.c").read_text()
commands = (root / "app_cmd.c").read_text()
def function(text,name):
    import re
    match = re.search(r'^(?:static )?[^\n;]+\b'+name+r'\([^;]*?\) \{',text,re.M)
    assert match,name
    return text[match.start():text.index('\n}',match.end())+2]
harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define NRF_LOG_INFO(...) ((void)0)
#define NRF_LOG_ERROR(...) ((void)0)
#define TAG_TYPE_UNDEFINED 0
#define FDS_EMULATION_CONFIG_FILE_ID 1
#define FDS_EMULATION_CONFIG_RECORD_KEY 1
#define STATUS_SUCCESS 0x68
#define STATUS_FLASH_WRITE_FAIL 0x63
typedef int tag_specific_type_t;
typedef int tag_sense_type_t;
typedef struct { uint16_t id,key; } fds_slot_record_map_t;
typedef struct { uint8_t *buffer; uint16_t length; uint16_t *crc; } tag_data_buffer_t;
typedef int (*tag_datas_savecb_t)(tag_specific_type_t,tag_data_buffer_t *);
static struct { uint8_t active_slot;struct {int tag_hf,tag_lf;} slots[8]; } slotConfig;
static uint16_t m_slot_config_crc, crcs[2];
static uint8_t bytes[2][4]={{1,2,3,4},{5,6,7,8}};
static tag_data_buffer_t buffers[2]={{bytes[0],4,&crcs[0]},{bytes[1],4,&crcs[1]}};
static bool fail, g_is_tag_emulating;
static int writes,loads,stops,starts;
static tag_data_buffer_t *get_buffer_by_tag_type(int type) { return &buffers[type-1]; }
static int savecb(int type,tag_data_buffer_t *b) { return b->length; }
static tag_datas_savecb_t get_data_savecb_from_tag_type(int type) { return savecb; }
static tag_sense_type_t get_sense_type_from_tag_type(int type) { return type; }
static void calc_14a_crc_lut(uint8_t *p,int n,uint8_t *out) {
    uint16_t sum=0;for(int i=0;i<n;i++) sum+=p[i];memcpy(out,&sum,2);
}
static void get_fds_map_by_slot_sense_type_for_dump(int slot,int sense,fds_slot_record_map_t *map) { map->id=10+slot;map->key=sense; }
static bool fds_write_sync(int id,int key,int n,void *p) { writes++;return !fail; }
static uint8_t tag_emulation_get_slot(void) { return slotConfig.active_slot; }
static void tag_emulation_set_slot(uint8_t slot) { slotConfig.active_slot=slot; }
static void tag_emulation_load_data(void) { loads++; }
static void tag_emulation_sense_end(void) { stops++; }
static void tag_emulation_sense_run(void) { starts++; }
typedef struct { uint16_t status; } data_frame_tx_t;
static data_frame_tx_t reply;
static data_frame_tx_t *data_frame_make(int cmd,int status,int n,void *p) { reply.status=status;return &reply; }
'''
functions='\n'.join(function(source,n) for n in ['save_data_by_tag_type','tag_emulation_save_data','tag_emulation_save_config','tag_emulation_save','tag_emulation_change_slot'])
functions+='\n'+function(commands,'cmd_processor_slot_data_config_save')
tests=r'''
int main(int argc,char **argv) {
    int mode=atoi(argv[1]);slotConfig.slots[0].tag_hf=1;slotConfig.slots[0].tag_lf=2;
    if(mode==0) {
        fail=true;save_data_by_tag_type(0,1);assert(writes==1 && crcs[0]==0);
        fail=false;save_data_by_tag_type(0,1);assert(writes==2 && crcs[0]!=0);
        save_data_by_tag_type(0,1);assert(writes==2);
    } else if(mode==1) {
        fail=true;cmd_processor_slot_data_config_save(0,0,0,NULL);
        assert(reply.status==STATUS_FLASH_WRITE_FAIL && writes==3);
        fail=false;cmd_processor_slot_data_config_save(0,0,0,NULL);
        assert(reply.status==STATUS_SUCCESS && writes==6);
        cmd_processor_slot_data_config_save(0,0,0,NULL);assert(writes==6);
    } else {
        fail=true;tag_emulation_change_slot(1,true);
        assert(slotConfig.active_slot==0 && loads==0 && stops==1 && starts==1);
        fail=false;tag_emulation_change_slot(1,true);
        assert(slotConfig.active_slot==1 && loads==1 && stops==2 && starts==2);
    }
    puts("Slot save retry: PASS");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-save-retry-") as directory:
    temp=Path(directory);(temp/'test.c').write_text(harness+functions+tests)
    binary=temp/'test'
    subprocess.run(['gcc','-std=c99','-O1','-g','-fno-pie','-no-pie','-fsanitize=address,undefined','-fno-sanitize-recover=all',str(temp/'test.c'),'-o',str(binary)],check=True)
    results=[subprocess.run([str(binary),str(mode)]).returncode for mode in range(3)]
    assert results==[0,0,0],results

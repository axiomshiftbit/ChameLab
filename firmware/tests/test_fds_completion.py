"""Exercise flash completion callbacks and empty writes from the real FDS wrapper."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/utils/fds_util.c").read_text()
def function(signature):
    start = source.index(signature)
    end = source.index("\n}", start) + 2
    return source[start:end]
state = source[source.index("static struct {"):source.index("} fds_operation_info;") + len("} fds_operation_info;")]
harness = r'''
#include <stdint.h>
#include <stdbool.h>
#include <assert.h>
#include <string.h>
#include <stdio.h>
typedef int ret_code_t;
#define NRF_SUCCESS 0
#define FDS_ERR_NO_SPACE_IN_FLASH 1
#define NRF_LOG_INFO(...) ((void)0)
#define NRF_LOG_ERROR(...) ((void)0)
#define APP_ERROR_CHECK(x) assert((x) == 0)
#define APP_ERROR_CHECK_BOOL(x) assert(x)
#define __NOP() ((void)0)
enum { FDS_EVT_INIT, FDS_EVT_WRITE, FDS_EVT_UPDATE, FDS_EVT_DEL_RECORD, FDS_EVT_DEL_FILE, FDS_EVT_GC };
typedef struct {
    int id, result;
    union {
        struct { uint32_t record_id; uint16_t file_id, record_key; bool updated; } write;
        struct { uint32_t record_id; uint16_t file_id, record_key; } del;
    };
} fds_evt_t;
'''
stubs = r'''
static int writes;
static ret_code_t fds_write_record_nogc(uint16_t id, uint16_t key, uint16_t words, void *buffer) {
    (void)id; (void)key; (void)words; (void)buffer;
    writes++;
    fds_operation_info.success = true;
    return NRF_SUCCESS;
}
static void fds_gc_sync(void) { fds_operation_info.success = true; }
'''
tests = r'''
int main(int argc, char **argv) {
    (void)argv;
    fds_operation_info.ignore_pm = true;
    if (argc == 1) {
        uint32_t data = 0;
        assert(fds_write_sync(1, 2, 0, &data));
        assert(!fds_operation_info.waiting);
        assert(writes == 0);
        assert(fds_write_sync(1, 2, 4, &data));
        assert(!fds_operation_info.waiting && writes == 1);
    } else {
        fds_evt_t event;
        memset(&event, 0xFF, sizeof(event)); // GC has no record fields in the union
        event.id = FDS_EVT_GC; event.result = NRF_SUCCESS;
        fds_evt_handler(&event);
        assert(fds_operation_info.success);
        fds_operation_info.success = false;
        event.id = FDS_EVT_WRITE;
        event.write.file_id = 0xC001; event.write.record_key = 0xC001;
        fds_evt_handler(&event);
        assert(!fds_operation_info.success);
        event.write.file_id = fds_operation_info.id = 1;
        event.write.record_key = fds_operation_info.key = 2;
        fds_evt_handler(&event);
        assert(fds_operation_info.success);
    }
    puts("FDS completion checks passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-fds-") as directory:
    path = Path(directory)
    code = harness + state + stubs + function("bool fds_write_sync(") + function("static bool is_peer_manager_record(") + function("static void fds_evt_handler(") + tests
    (path / "test.c").write_text(code)
    subprocess.run(["cc", "-std=c99", "-O2", "-Wall", "-Wextra", "-Werror", "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    results = [subprocess.run([str(path / "test"), *args], timeout=5).returncode for args in [[], ["gc"]]]
    assert results == [0, 0], results

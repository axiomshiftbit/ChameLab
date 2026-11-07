"""Check the real NUS sender with stalled/disconnected notification queues."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/ble_main.c").read_text()
start = source.index("void nus_data_response(")
sender = source[start:source.index("\n}", start) + 2]
harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
typedef int ret_code_t;
#define NRF_SUCCESS 0
#define NRF_ERROR_BUSY 1
#define NRF_ERROR_RESOURCES 2
#define NRF_ERROR_INVALID_STATE 3
#define NRF_ERROR_NOT_FOUND 4
#define NRF_LOG_INFO(...) ((void)0)
#define NRF_LOG_DEBUG(...) ((void)0)
#define NRF_LOG_WARNING(...) ((void)0)
#define APP_ERROR_CHECK(x) assert((x) == 0)
#define APP_TIMER_TICKS(x) (x)
#define MIN(a,b) ((a)<(b)?(a):(b))
static int m_nus, m_conn_handle, mode, calls, sent;
static uint16_t m_ble_nus_max_data_len = 20;
static bool g_is_ble_connected = true;
static uint32_t ticks;
static uint32_t app_timer_cnt_get(void) { return ticks; }
static uint32_t app_timer_cnt_diff_compute(uint32_t a, uint32_t b) { return a-b; }
static void sd_app_evt_wait(void) { ticks += 50; }
static ret_code_t ble_nus_data_send(int *nus, uint8_t *data, uint16_t *length, int handle) {
    (void)nus; (void)data; (void)handle;
    assert(++calls < 50); // fail an unbounded loop even in the pre-fix implementation
    if (mode == 1) return NRF_ERROR_INVALID_STATE;
    if (mode == 2) return NRF_ERROR_RESOURCES;
    if (mode == 3) { *length = 0; return NRF_SUCCESS; }
    if (calls == 1) return NRF_ERROR_BUSY;
    sent += *length;
    return NRF_SUCCESS;
}
'''
tests = r'''
int main(int argc, char **argv) {
    assert(argc == 2);
    mode = atoi(argv[1]);
    uint8_t data[57] = {0};
    nus_data_response(data, sizeof(data));
    if (mode == 0) assert(sent == 57);
    if (mode == 1 || mode == 3) assert(calls == 1 && sent == 0);
    if (mode == 2) assert(ticks >= 1000 && sent == 0);
    puts("BLE reply termination check passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-ble-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + sender + tests)
    subprocess.run(["cc", "-std=c99", "-O2", "-Wall", "-Wextra", "-Werror", "-Wno-unused-function",
                    "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    results = [subprocess.run([str(path / "test"), str(mode)], timeout=5).returncode for mode in range(4)]
    assert results == [0] * 4, results

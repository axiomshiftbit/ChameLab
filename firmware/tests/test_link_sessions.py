"""Exercise real frame parsing and link callbacks across BLE/USB reconnects."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1] / "application/src"
ble = (root / "ble_main.c").read_text()
usb = (root / "usb_main.c").read_text()
commands = (root / "app_cmd.c").read_text()
frames = (root / "utils/dataframe.c").read_text()
net = (root / "utils/netdata.h").read_text()


def function(source, signature):
    start = source.index(signature)
    return source[start:source.index("\n}", start) + 2]


def event(name):
    body = ble.split("case " + name + ":", 1)[1].split("\n            break;", 1)[0]
    return "static void " + name + "(ble_evt_t const *p_ble_evt) {\nret_code_t err_code = 0; (void)err_code;\n" + body + "\n}\n"


prefix = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define PACKED __attribute__((packed))
#define U16HTONS(x) __builtin_bswap16(x)
#define U16NTOHS(x) __builtin_bswap16(x)
#define NETDATA_MAX_DATA_LENGTH 4096
#define NRF_LOG_INFO(...) ((void)0)
#define NRF_LOG_DEBUG(...) ((void)0)
#define NRF_LOG_WARNING(...) ((void)0)
#define NRF_LOG_ERROR(...) ((void)0)
#define CRITICAL_REGION_ENTER() do {} while (0)
#define CRITICAL_REGION_EXIT() do {} while (0)
'''
types = (root / "utils/dataframe.h").read_text()
types = types[types.index("// Data frame process callback"):types.index("#endif")]
net_types = net[net.index("typedef struct"):net.index("// Command-specific")]
frame_code = frames[frames.index("static netdata_frame_raw_t"):]

boundary = r'''
typedef int ret_code_t;
#define APP_ERROR_CHECK(x) assert((x) == 0)
#define BLE_CONN_HANDLE_INVALID 0xffff
#define BLE_GATT_ATT_MTU_DEFAULT 23
#define BLE_RADIO_CONNECTED 1
#define BLE_RADIO_DISCONNECTED 2
#define BLE_NUS_EVT_RX_DATA 1
typedef struct { struct { struct { uint16_t conn_handle; } gap_evt; } evt; } ble_evt_t;
typedef struct {
    int type;
    struct { struct { uint8_t *p_data; uint16_t length; } rx_data; } params;
} ble_nus_evt_t;
static uint16_t m_conn_handle = BLE_CONN_HANDLE_INVALID;
static uint16_t m_ble_nus_max_data_len = 244;
static bool g_is_ble_connected, g_usb_port_opened;
static int m_qwr, radio_event, commands_seen, ble_replies, usb_replies, mode;
static bool is_usb_working(void) { return g_usb_port_opened; }
static bool is_nus_working(void) { return g_is_ble_connected; }
static void sleep_timer_stop(void) {}
static void sleep_timer_start(int timeout) { (void)timeout; }
static int settings_get_sleep_timeout(void) { return 1000; }
static int nrf_ble_qwr_conn_handle_assign(int *qwr, uint16_t handle) { (void)qwr; (void)handle; return 0; }
static void ble_radio_apply(int event) { radio_event = event; }
static void ble_usb_cdc_set_open(bool open) { (void)open; }
static void usb_cdc_write(uint8_t *data, uint16_t len) { (void)data; (void)len; usb_replies++; }
static void nus_data_response(uint8_t *data, uint16_t len) { (void)data; (void)len; ble_replies++; }
'''

tests = r'''
static ble_evt_t link = {.evt.gap_evt.conn_handle = 0}; // SoftDevice reuses handles.
static uint8_t wire[64];
static uint16_t wire_size;
static void reconnect(void) {
    BLE_GAP_EVT_DISCONNECTED(&link);
    BLE_GAP_EVT_CONNECTED(&link);
}
static void command(uint16_t cmd, uint16_t status, uint16_t len, uint8_t *data) {
    (void)status;
    commands_seen++;
    assert(cmd == 123 && len == 4 && data[0] == 1 && data[3] == 4);
    if (mode == 3 || mode == 7) {
        if (mode == 3) reconnect();
        else usb_port_set_open(true);
        data_frame_receive(wire, wire_size); // cannot overwrite an executing command
        assert(data[0] == 1 && data[3] == 4);
    }
    auto_response_data(data_frame_make(cmd, 0, len, data));
}
int main(int argc, char **argv) {
    assert(argc == 2);
    mode = atoi(argv[1]);
    uint8_t payload[] = {1,2,3,4};
    data_frame_tx_t *packet = data_frame_make(123, 0, sizeof(payload), payload);
    wire_size = packet->length;
    memcpy(wire, packet->buffer, wire_size);
    on_data_frame_complete(command);
    BLE_GAP_EVT_CONNECTED(&link);
    if (mode == 0) {
        assert(m_ble_nus_max_data_len == 20);
        m_ble_nus_max_data_len = 244;
        reconnect();
        assert(m_ble_nus_max_data_len == 20);
    } else if (mode == 1) {
        data_frame_receive(wire, 5);
        reconnect();
        data_frame_receive(wire, wire_size);
        data_frame_process();
        assert(commands_seen == 1 && ble_replies == 1);
    } else if (mode == 2) {
        data_frame_receive(wire, wire_size);
        reconnect();
        data_frame_process();
        assert(commands_seen == 0 && ble_replies == 0);
        data_frame_receive(wire, wire_size);
        data_frame_process();
        assert(commands_seen == 1 && ble_replies == 1);
    } else if (mode == 3 || mode == 7) {
        data_frame_receive(wire, wire_size);
        data_frame_process();
        assert(commands_seen == 1 && ble_replies == 0 && usb_replies == 0);
        mode = 1;
        data_frame_receive(wire, wire_size);
        data_frame_process();
        assert(commands_seen == 2 && ble_replies + usb_replies == 1);
    } else if (mode == 4) {
        data_frame_receive(wire, 5);
        usb_port_set_open(true);
        assert(m_data_rx_position == 0);
        data_frame_receive(wire, 5);
        ble_nus_evt_t stale = {.type = BLE_NUS_EVT_RX_DATA, .params.rx_data = {wire, wire_size}};
        nus_data_handler(&stale);
        assert(m_data_rx_position == 5);
        BLE_GAP_EVT_DISCONNECTED(&link); // late BLE event must not erase USB's frame
        data_frame_receive(wire + 5, wire_size - 5);
        data_frame_process();
        assert(commands_seen == 1 && usb_replies == 1 && ble_replies == 0);
    } else if (mode == 5) {
        usb_port_set_open(true);
        data_frame_receive(wire, 5);
        usb_port_set_open(false);
        BLE_GAP_EVT_CONNECTED(&link);
        data_frame_receive(wire, wire_size);
        data_frame_process();
        assert(commands_seen == 1 && ble_replies == 1 && usb_replies == 0);
    } else if (mode == 6) {
        usb_port_set_open(true);
        data_frame_receive(wire, wire_size);
        usb_port_set_open(false);
        BLE_GAP_EVT_CONNECTED(&link);
        data_frame_process();
        assert(commands_seen == 0 && ble_replies == 0 && usb_replies == 0);
    }
    puts("PASS: link session boundary");
}
'''

with tempfile.TemporaryDirectory(prefix="chamelab-links-") as directory:
    path = Path(directory)
    source = prefix + types + net_types + frame_code + boundary
    source += event("BLE_GAP_EVT_CONNECTED") + event("BLE_GAP_EVT_DISCONNECTED")
    source += function(usb, "static void usb_port_set_open(")
    source += function(ble, "static void nus_data_handler(")
    source += function(commands, "static void auto_response_data(") + tests
    (path / "test.c").write_text(source)
    subprocess.run(["cc", "-std=c99", "-O2", "-Wall", "-Wextra", "-Werror", "-Wno-unused-function",
                    "-Wno-unused-parameter", "-fsanitize=address,undefined", str(path / "test.c"), "-o", str(path / "test")], check=True)
    results = [subprocess.run([str(path / "test"), str(mode)], timeout=5).returncode for mode in range(8)]
    assert results == [0] * 8, results

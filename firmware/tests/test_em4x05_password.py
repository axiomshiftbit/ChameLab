"""Read-only password handler: protocol validation and no false success on an unprotected tag."""
from pathlib import Path
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / "application/src/app_cmd.c").read_text()
start = source.index("static data_frame_tx_t *cmd_processor_em4x05_check_password(")
end = source.index("\nstatic ", start + 1)
harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#define STATUS_PAR_ERR 0x60
#define STATUS_LF_TAG_OK 0x40
#define STATUS_LF_TAG_NO_FOUND 0x41
#define STATUS_LF_TAG_LOGIN_REQUIRED 0x42
#define STATUS_NOT_IMPLEMENTED 0x69
#define EM4X05_BLOCK_UID 1
#define EM4X05_BLOCK_CONFIG 4
#define EM4X05_CFG_READ_LOGIN (1u << 18)
#define U32HTONL(v) __builtin_bswap32(v)
typedef struct { uint16_t status, length; uint8_t data[4]; } data_frame_tx_t;
static data_frame_tx_t reply;
static bool reads[5];
static uint32_t values[5];
static int next_read, starts, stops, logins;
static uint32_t login_value;
static data_frame_tx_t *data_frame_make(uint16_t command, uint16_t status, uint16_t length, uint8_t *data) {
    reply.status = status; reply.length = length;
    if (length) { assert(length == 4); memcpy(reply.data, data, length); }
    return &reply;
}
static uint32_t bytes_to_num(uint8_t *data, int length) {
    uint32_t value = 0; for (int i = 0; i < length; i++) value = (value << 8) | data[i]; return value;
}
static void start_lf_125khz_radio(void) { starts++; }
static void stop_lf_125khz_radio(void) { stops++; }
static void bsp_delay_ms(int delay) {}
static bool em4x05_read_word(uint8_t block, uint32_t *word, uint32_t timeout) {
    assert(next_read < 5); *word = values[next_read]; return reads[next_read++];
}
static void em4x05_send_login(uint32_t password) { logins++; login_value = password; }
static void reset(void) {
    next_read = starts = stops = logins = 0; memset(reads, 0, sizeof(reads)); memset(values, 0, sizeof(values));
    values[0] = values[3] = 0x01020304;
}
'''
tests = r'''
int main(void) {
    uint8_t password[4] = {0,0,0,0};
    for (int size = 0; size <= 9; size++) {
        if (size == 4) continue;
        reset(); assert(cmd_processor_em4x05_check_password(3037,0,size,password)->status == STATUS_PAR_ERR);
        assert(starts == 0 && logins == 0);
    }
    reset(); assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_LF_TAG_NO_FOUND);
    assert(starts == 1 && stops == 1 && logins == 0);
    reset(); reads[0] = reads[1] = true;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_NOT_IMPLEMENTED);
    assert(logins == 0 && stops == 1);
    reset(); reads[0] = reads[2] = true; values[2] = EM4X05_CFG_READ_LOGIN;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_LF_TAG_OK);
    assert(logins == 1 && login_value == 0 && reply.length == 4 && stops == 1);
    assert(reply.data[0] == 1 && reply.data[3] == 4);
    reset(); reads[0] = reads[2] = true; values[2] = 0;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_NOT_IMPLEMENTED);
    reset(); reads[0] = reads[3] = true;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_LF_TAG_LOGIN_REQUIRED);
    assert(reply.length == 0 && stops == 1);
    reset(); reads[0] = true;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_LF_TAG_NO_FOUND);
    reset(); reads[0] = reads[3] = true; values[3] = 0x05060708;
    assert(cmd_processor_em4x05_check_password(3037,0,4,password)->status == STATUS_LF_TAG_NO_FOUND);
    puts("EM4x05 password checks: validation, zero password, protected/unprotected reads and lost tag passed");
}
'''
with tempfile.TemporaryDirectory(prefix="chamelab-em-password-") as directory:
    path = Path(directory)
    (path / "test.c").write_text(harness + source[start:end] + tests)
    subprocess.run(["cc", "-std=c99", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                    "-fsanitize=address,undefined", "-fno-omit-frame-pointer", str(path / "test.c"), "-o", str(path / "test")], check=True)
    subprocess.run([str(path / "test")], check=True)

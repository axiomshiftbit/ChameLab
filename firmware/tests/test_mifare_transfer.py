"""Exercise the real emulation TRANSFER handler, including manufacturer-block protection."""
from pathlib import Path
import re
import subprocess
import tempfile


def main():
    base = Path(__file__).resolve().parents[1] / "application/src/rfid/nfctag/hf"
    source = (base / "nfc_mf1.c").read_text()
    header = (base / "nfc_mf1.h").read_text()
    names = ["CMD_TRANSFER", "ACC_BLOCK_READ", "ACC_BLOCK_WRITE", "ACC_BLOCK_INCREMENT", "ACC_BLOCK_DECREMENT"]
    definitions = "\n".join(re.search(r"^#define " + name + r"\s+[^\n]+", source, re.M)[0] for name in names)
    for name in ["NORMAL", "DENIED", "DECEIVE", "SHADOW"]:
        value = re.search(r"NFC_TAG_MF1_WRITE_" + name + r"\s*=\s*(\d+)u", header)[1]
        definitions += f"\n#define NFC_TAG_MF1_WRITE_{name} {value}\n"
    start = source.index("static const uint8_t abDataAccessConditions")
    table = source[start:source.index("\n};", start) + 3]
    start = source.index("case CMD_TRANSFER: {")
    handler = source[start:source.index("case CMD_AUTH_A:", start)]
    harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#define ACK_VALUE 0x0A
#define NAK_INVALID_OPERATION_TBIV 0x04
#define MEM_BYTES_PER_BLOCK 16
static struct {
    struct { uint8_t mode_block_write; bool mode_gen2_magic; } config;
    uint8_t memory[256][16];
} tag;
static typeof(tag) *m_tag_information = &tag;
static uint8_t CurrentAddress, AuthenticatedSector, KeyInUse, acl, response;
static uint8_t m_data_block_buffer[16];
static unsigned resets;
static uint8_t blockToSector(uint8_t block) { return block < 128 ? block / 4 : 32 + (block - 128) / 16; }
static bool isSectorTrailer(uint8_t block) { return block < 128 ? block % 4 == 3 : block % 16 == 15; }
static uint8_t GetAccessCondition(uint8_t block) { (void)block; return acl; }
static void mf1_response_4bit_auto_encrypt(uint8_t status) { response = status; }
static void nfc_tag_mf1_reset_handler(void) { resets++; }
'''
    tests = r'''
int main(void) {
    unsigned cases = 0;
    /* Decoder index is C1 + 2*C2 + 4*C3; NXP table 8 permits TRANSFER at 000/110/001. */
    for (unsigned block = 0; block < 256; block++)
    for (unsigned key = 0; key < 2; key++)
    for (unsigned access = 0; access < 8; access++)
    for (unsigned magic = 0; magic < 2; magic++)
    for (unsigned mode = 0; mode < 4; mode++)
    for (unsigned correct_sector = 0; correct_sector < 2; correct_sector++) {
        memset(&tag, 0, sizeof(tag));
        memset(m_data_block_buffer, 0xA5, sizeof(m_data_block_buffer));
        tag.config.mode_gen2_magic = magic;
        tag.config.mode_block_write = mode;
        AuthenticatedSector = correct_sector ? blockToSector(block) : 40;
        KeyInUse = key; acl = access; response = 0xff; resets = 0;
        bool allowed = correct_sector && !isSectorTrailer(block) && (block != 0 || magic)
            && (access == 0 || access == 3 || access == 4) && mode != NFC_TAG_MF1_WRITE_DENIED;
        transfer(block);
        if (response != (allowed ? ACK_VALUE : NAK_INVALID_OPERATION_TBIV)) {
            fprintf(stderr, "TRANSFER mismatch: block=%u magic=%u mode=%u access=%u key=%u\n",
                    block, magic, mode, access, key);
            return 1;
        }
        assert(resets == (allowed ? 0u : 1u));
        for (unsigned b = 0; b < 256; b++)
        for (unsigned i = 0; i < 16; i++)
            assert(tag.memory[b][i] == (allowed && mode != NFC_TAG_MF1_WRITE_DECEIVE && b == block ? 0xA5 : 0));
        cases++;
    }
    printf("MIFARE TRANSFER permissions and write modes: %u cases passed\n", cases);
}
'''
    with tempfile.TemporaryDirectory(prefix="chamelab-transfer-") as directory:
        path = Path(directory)
        function = "\nstatic void transfer(uint8_t block) { uint8_t p_data[2] = {CMD_TRANSFER, block}; switch (p_data[0]) {\n" + handler + "\n} }\n"
        (path / "test.c").write_text(harness + definitions + table + function + tests)
        subprocess.run(["cc", "-std=gnu99", "-O1", "-Wall", "-Wextra", "-Werror",
                        "-fsanitize=address,undefined", "-fno-omit-frame-pointer",
                        str(path / "test.c"), "-o", str(path / "test")], check=True)
        subprocess.run([str(path / "test")], check=True)


if __name__ == "__main__":
    main()

"""Check the firmware's real Classic ACL decoder against NXP's 1K/4K geometry."""
from pathlib import Path
import subprocess
import tempfile


def main():
    source = (Path(__file__).resolve().parents[1] / "application/src/rfid/nfctag/hf/nfc_mf1.c").read_text()
    start = source.index("uint8_t GetAccessCondition(")
    end = source.index("\nbool CheckValueIntegrity", start)
    decoder = source[start:end]
    harness = r'''
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#define BYTE_SWAP(x) (((x) >> 4) | ((x) << 4))
#define NO_ACCESS 0x07
static struct { uint8_t acs[4]; } trailer;
static const typeof(trailer) *m_tag_trailer_info = &trailer;
'''
    tests = r'''
int main(void) {
    unsigned cases = 0;
    /* NXP MF1S70YYX_V1 tables 6/7/8: 4 groups of 3 access bits. */
    for (unsigned config = 0; config < 4096; config++) {
        unsigned c1 = 0, c2 = 0, c3 = 0;
        unsigned acl[4];
        for (unsigned group = 0; group < 4; group++) {
            acl[group] = (config >> (3 * group)) & 7;
            c1 |= (acl[group] & 1) << group;
            c2 |= ((acl[group] >> 1) & 1) << group;
            c3 |= ((acl[group] >> 2) & 1) << group;
        }
        trailer.acs[0] = ((~c2 & 15) << 4) | (~c1 & 15);
        trailer.acs[1] = (c1 << 4) | (~c3 & 15);
        trailer.acs[2] = (c3 << 4) | c2;
        for (unsigned block = 0; block < 256; block++) {
            unsigned offset = block % 16;
            unsigned group = block < 128 ? block % 4 : offset == 15 ? 3 : offset / 5;
            unsigned actual = GetAccessCondition(block);
            if (actual != acl[group]) {
                fprintf(stderr, "ACL mismatch: configuration=%u block=%u group=%u actual=%u expected=%u\n",
                        config, block, group, actual, acl[group]);
                return 1;
            }
            cases++;
        }
        /* Any one-bit error in either redundant copy must deny access. */
        for (unsigned bit = 0; bit < 24; bit++) {
            trailer.acs[bit / 8] ^= 1 << (bit % 8);
            for (unsigned block = 0; block < 256; block++) {
                assert(GetAccessCondition(block) == NO_ACCESS);
                cases++;
            }
            trailer.acs[bit / 8] ^= 1 << (bit % 8);
        }
    }
    printf("MIFARE Classic ACL geometry and redundant bits: %u cases passed\n", cases);
}
'''
    with tempfile.TemporaryDirectory(prefix="chamelab-acl-") as directory:
        path = Path(directory)
        (path / "test.c").write_text(harness + decoder + tests)
        subprocess.run(["cc", "-std=gnu99", "-Wall", "-Wextra", "-Werror",
                        "-fsanitize=address,undefined", "-fno-omit-frame-pointer",
                        str(path / "test.c"), "-o", str(path / "test")], check=True)
        subprocess.run([str(path / "test")], check=True)


if __name__ == "__main__":
    main()

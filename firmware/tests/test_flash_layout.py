"""Keep linker, bootloader and host DFU limits outside persistent slot/settings storage."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
def define(path, name):
    text = (root / path).read_text()
    return int(re.search(r"^#define\s+" + name + r"\s+(\w+)", text, re.M)[1], 0)
def flash(path):
    text = (root / path).read_text()
    match = re.search(r"FLASH \(rx\)\s*:\s*ORIGIN = (0x[0-9A-Fa-f]+), LENGTH = (0x[0-9A-Fa-f]+)", text)
    return tuple(int(v, 16) for v in match.groups())

storage = define("firmware/application/src/sdk_config.h", "FDS_VIRTUAL_PAGES") * define("firmware/application/src/sdk_config.h", "FDS_VIRTUAL_PAGE_SIZE") * 4
reserved = define("firmware/bootloader/src/sdk_config.h", "NRF_DFU_APP_DATA_AREA_SIZE")
app_start, app_size = flash("firmware/application/application.ld")
boot_start, _ = flash("firmware/bootloader/bootloader.ld")
host = (root / "software/chamelab/ChameLab.Core/Protocol/DfuPackage.cs").read_text()
host_limit = int(re.search(r"MaxFirmwareSize = (0x[0-9A-Fa-f]+)", host)[1], 16)
assert storage == reserved, (storage, reserved)
assert app_start + app_size <= boot_start - storage
assert host_limit == app_size
print(f"Flash layout: application {app_size} bytes; persistent storage {storage} bytes protected")

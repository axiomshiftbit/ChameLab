<p align="center"><img src="website/chameleon-ultra.png" alt="ChameleonUltra" height="160"></p>

# ChameLab

[Русский](README.md) · **English**

**This is a fork of the [original m0n5ter/ChameLab project](https://github.com/m0n5ter/ChameLab),
based on [ChameleonUltra](https://github.com/RfidResearchGroup/ChameleonUltra).**
This fork is developed through **vibe coding** with AI assistants, change review and
automated tests.

ChameLab is an application for ChameleonUltra and ChameleonLite on Android, Windows,
Linux, macOS and the web. This repository also includes ChameLab firmware and a
command-line client.

[Releases](https://github.com/axiomshiftbit/ChameLab/releases) ·
[Development builds](https://github.com/axiomshiftbit/ChameLab/actions/workflows/ui-quality.yml) ·
[Changelog](CHANGELOG.md) · [Authors and licenses](AUTHORS.md)

## Version 1.5.3

App **1.5.3** ships with firmware **2.3.19-cl**. This release restores firmware updates:
on Linux the app and the CLI find a connected Chameleon and its DFU bootloader again, and
on Windows an update finds the bootloader of the composite USB device, fixing the timeout
seen in 1.5.1 and 1.5.2. Firmware guards the software timer pool against exhaustion. See
the [changelog](CHANGELOG.md) for details.

## Choose a download

`<version>` is the app version; `<firmware>` is the separate firmware version ending
in `-cl`. These are filename patterns under **Releases → Assets** in this repository.
GitHub's automatic **Source code** archives are not installation packages.

| File | Purpose and installation |
| --- | --- |
| `chamelab-<version>-android-arm64.apk` | Android 7.0 or newer, most modern phones. Open the APK on the phone and allow installation from that source. |
| `chamelab-<version>-android-arm.apk` | Android with a 32-bit ARM system. Use when arm64 is not supported. |
| `chamelab-<version>-win-x64.zip` | Windows x64. Extract the **whole** archive into its own folder and launch `ChameLab.exe`. Portable; no installer. |
| `chamelab-<version>-linux-x64.zip` | Linux x64. Extract, run `chmod +x ChameLab`, then `./ChameLab`. |
| `chamelab-<version>-osx-arm64.zip` | macOS on Apple Silicon. Extract and launch `ChameLab`; approve downloaded software through macOS when required. |
| `chamelab-<version>-osx-x64.zip` | macOS on Intel. Use the same steps as for Apple Silicon. |
| `client-<version>-windows.zip`, `client-<version>-linux.zip`, `client-<version>-macos.zip` | Command-line client, not the graphical app. Extract and run `chamelab.exe` / `./chamelab` in a terminal. The macOS CLI build targets Apple Silicon. |
| `chamelab-<version>-web.zip` | Web application files for a web server, not a phone installer. |
| `ultra-dfu-app-<firmware>.zip` | Normal firmware update for **ChameleonUltra**. Select the ZIP in the app's firmware updater. |
| `lite-dfu-app-<firmware>.zip` | Normal firmware update for **ChameleonLite**. Do not use it for Ultra. |

Desktop packages include the .NET runtime; an SDK is not needed to run them.
The fork's Android package is `io.github.axiomshiftbit.chamelab`, so the original app
can remain installed. Transfer a card library using export and import.

**Actions development builds:** download `release-artifacts-gui-android` and install
`chamelab-android-arm64-development.apk` (use `android-arm` for older 32-bit devices).
These APKs use optimized Release builds with AOT and the `.dev` package. The old
`ChameLab-arm64-debug.apk` uses the interpreter and is unsuitable for performance
evaluation. `ChameLab-ui-tests.apk` automatically
runs interface tests and is not intended for regular use. Extract the downloaded
Actions artifact archive first.

## Quick start

1. **Connect.** Attach Ultra with a data-capable USB cable. Android requires USB Host/OTG
   and USB permission, or you can use Bluetooth LE. Wake the device, select it and press
   Connect. On desktop, one click selects and a double-click connects. A charge-only
   cable cannot carry data.
2. **Cards → Add card.** First test HF/LF card detection. If nothing is found, move the
   card closer, turn it over and scan again. Read a supported card or import a saved file,
   then save it to the library.
   MIFARE Classic key recovery reads accessible blocks automatically. “Read card data” fills
   missing blocks with found and saved keys without the selected dictionaries, keeping existing
   data. “Export found keys” saves unique keys to `.dic`, a new dictionary or an existing one;
   unknown keys are excluded.
   In the MIFARE Classic editor, expand “Access rights” to see the selected sector's allowed
   operations and required A/B key, including 4K block groups. A read sector trailer is required.
   This analyzes the editor's data; it does not test possession of keys or write to the card.
   “Compare with file” lists differences from a single Classic dump of the same capacity:
   block addresses and counts of different/unknown bytes. It compares files, not a physical card.
   The HEX editor has copy/paste, undo/redo and row-selection buttons. “Navigation and search”
   supports address jumps, exact byte ranges and HEX/ASCII search. Addresses are **byte offsets**
   in decimal or prefixed with `0x`, not block numbers: Classic block 128 starts at `0x800`.
   “Show all memory” searches the entire card; picking a sector returns to that sector's view.
   Desktop shortcuts: `Ctrl+F` searches, `Ctrl+G` jumps to an address, `F3` finds the next match.
   Edits affect the working copy; writing a physical card is a separate action.
3. **Slots.** Load a saved card into a slot and activate it for emulation. Supported LF
   formats allow up to eight cards per slot; the next reader tap advances the list.
4. **Writer.** Select a library card or import a source file. Place the target blank on
   the device, scan/detect its type, and optionally inspect memory and factory keys.
   Review compatibility and write options, confirm the target, then start writing.
   Gen2 cannot be reliably identified by one universal safe query. Zeroed readable
   blocks do not prove that a card is factory-new.
5. **Results.** A red border indicates a missing card or error, green indicates successful
   detection, and amber indicates a limited check or incompatibility. Read the explanation.
   A command acknowledgment is different from a verified read-back.
6. **Journal.** Expand the journal inside Writer or use the separate menu page.
   New entries appear automatically. Command details, copy, `.log` export and clear are
   available. The protocol journal records stages, status, timing and block addresses,
   without card contents or key values.
7. **Tools.** HF/LF capture, active authentication with a known key, value blocks, Gen4,
   T55xx, raw commands, a console, dictionaries, key search and Lua plugins are available.
   For HF capture, hold Chameleon against a reader. For active authentication, place
   the card on Chameleon, which acts as the reader.
8. **Settings.** Select a language and theme; authors and licenses are listed here.
   Device settings and firmware updates are on the connected Chameleon's page.

Android and desktop share the main UI and logic. Transports, permissions and firmware
update support depend on the platform. Lite has hardware limitations; flashing Ultra
firmware cannot add Ultra hardware capabilities to Lite.

## Firmware updates

**For a normal Ultra update, choose `ultra-dfu-app-<firmware>.zip`.**
This file goes into the Chameleon device, not into Android or Windows.

1. Export important cards first. Connect the device and open its firmware section.
2. On Windows/Linux/macOS use USB. Android/iOS updates in the app use **BLE**:
   reconnect over Bluetooth first. Android USB/OTG card access does not imply USB DFU
   support. The web app does not install firmware.
3. Install the latest version offered by the app, or select the correct model's **ZIP**
   from this fork's Releases. Do not extract the DFU ZIP. Automatic checks use the latest
   stable release; development packages can be selected manually.
4. Check the model and version, confirm and wait for completion and reconnection.
   Keep power connected and the application open during the transfer.
5. Check the displayed firmware version and slots. An application-only firmware update
   preserves slots and settings; this does not replace a backup of important cards.

| Firmware files | Purpose |
| --- | --- |
| `ultra-dfu-app-…zip` / `lite-dfu-app-…zip` | Application firmware only; the normal update choice. |
| `ultra-dfu-full-…zip` / `lite-dfu-full-…zip` | Application, bootloader and SoftDevice. For recovery or a specific bootloader upgrade through a compatible DFU tool, not a regular ChameLab update. |
| `ultra-binaries-…zip` / `lite-binaries-…zip` | Separate HEX images for development and SWD programming. Not needed for normal updates. |
| `application.hex`, `bootloader.hex`, `softdevice.hex`, `fullimage.hex` | Application, bootloader, Nordic stack and combined full image respectively; not APKs or card files. Full programming can erase device data. |

Validation runs before writing and during transfer: Ultra/Lite model, package structure,
image and downloaded-package SHA-256, and each DFU object's CRC32 and offset. The
bootloader verifies the update signature. The app reports success after reconnecting to
the same device and checking its version. These checks still require stable power.
After interruption, a saved job offers recovery; a retry transfers from the beginning.
An encrypted backup of slots and settings can be created before updating.

If application firmware does not start, hold button B while connecting USB to enter
DFU. Use a package for **your device model** and a compatible Nordic DFU tool for
recovery. `firmware/flash-dfu-app.sh` and `flash-dfu-full.sh` expect a prepared developer
environment and files under `firmware/objects/`; they do not accept an arbitrary
path to a downloaded ZIP. The app updater is preferable for normal use.

## Card files and storage

- `.json`: card exports; `.bin` / `.eml`: memory dumps; `.nfc` / `.rfid`: supported Flipper formats.
  Use card import, not the firmware updater.
- `.trace`: Proxmark3 HF capture. `.bin` can also contain an LF capture: use the matching
  tool, according to the file's origin.
- `.log`: text journal; choose the destination when saving.
- The library and preferences live separately from the extracted executable:
  `LocalApplicationData/ChameLab` (`%LOCALAPPDATA%\ChameLab` on Windows), private app
  storage on Android, and site storage in the browser. Uninstalling the app or clearing
  its data can remove the local library.

## Development

[Source architecture and build instructions](software/chamelab/README.md).
The shared client uses .NET 10 / Avalonia with the Actipro theme.
`software/chamelab/ChameLab.Cli` contains the CLI, `firmware` the firmware,
`hardware` the schematics, and `website` the website and web app hosting.

```bash
dotnet run --project software/chamelab/ChameLab.Desktop -c Debug
dotnet run --project software/chamelab/ChameLab.Cli -c Release
# In the CLI: dump_help; per-command options: COMMAND --help
cd firmware
docker compose up --pull=always build-ultra
# For Lite: build-lite. Output: firmware/objects/
```

App and firmware versions are independent: see [CHANGELOG](CHANGELOG.md) and
[firmware/VERSION](firmware/VERSION). Source code is licensed under [GNU GPL v3](LICENSE);
individual dependencies retain their own terms. See [AUTHORS.md](AUTHORS.md) for the
attribution chain and Settings → Authors and licenses in the app for component licenses.
Actipro is governed by its own EULA.

<p align="center">
  <img src="website/chameleon-ultra.png" alt="ChameleonUltra" height="160">
</p>

<h1 align="center">ChameLab</h1>

<p align="center">
  Desktop, mobile and web app for ChameleonUltra and ChameleonLite,<br>
  together with an extended firmware and the command line client.
</p>

<p align="center">
  <a href="https://github.com/axiomshiftbit/ChameLab/actions/workflows/ui-quality.yml"><b>Development builds</b></a> ·
  <a href="https://github.com/axiomshiftbit/ChameLab/releases">Releases</a> ·
  <a href="CHANGELOG.md">Changelog</a> (<a href="CHANGELOG.ru.md">на русском</a>)
</p>

## Расширения текущего форка

Основные интерфейс и логика на .NET/Avalonia общие для Android и десктопных версий.
Платформенные подключения и разрешения проверяются отдельно. Android-пакет этого
форка — `io.github.axiomshiftbit.chamelab`; он устанавливается рядом с исходным
приложением, библиотека переносится через экспорт/импорт.

- **Запись** — отдельный пункт меню, выбор сохранённой карты или импорт, предварительное
  обнаружение носителя, проверка совместимости и подтверждение перед записью.
  MIFARE Classic Gen1a/Gen2/Gen3/Gen4; доступные пользовательские страницы Ultralight/NTAG;
  поддерживаемые LF-форматы. Подтверждение команды и успешное обратное чтение различаются.
  Gen2 нельзя достоверно определить одним универсальным безопасным запросом;
  нулевые доступные блоки не доказывают заводское состояние карты.
- **Журнал** — этапы, номера блоков/страниц, статусы команд, время, ошибки и отмена,
  копирование и экспорт. Ключи, UID и содержимое памяти в технический журнал не включаются.
- **Инструменты** — встроенная консоль команд, операции с value blocks, Gen4, T55xx,
  raw HF, словари разных размеров и Lua-плагины с остановкой выполнения.
- **Поиск ключей** — словарь или ограниченный диапазон, бюджет попыток, скорость,
  остановка и продолжение в текущем сеансе. Classic, NTAG/Ultralight EV1, Ultralight C,
  EM4x05; для EM4x05 нужна команда3037 новой прошивки и защита чтения LOGIN.
  Для NTAG поиск разрешён только при прочитанном AUTHLIM=0. Полный перебор48/128бит
  практически непригоден; восстановление Classic использует отдельные специализированные методы.
- **Авторы и лицензии** — автономный раздел настроек и [цепочка происхождения](AUTHORS.md).
  Actipro сохранён. Уведомления зависимостей обновляются командой
  `dotnet run --project software/chamelab/ChameLab.Tools -- generate-notices` после restore.

Lua-плагины используют формат ChameleonUltraGUI v1, локальные ресурсы, явные API устройства,
библиотеки карт/словарей/папок, диалоги и восстановление ключей. Импорт не запускает код.
Доступа к произвольным файлам, процессам и сети из Lua нет. Настройки GUI через Lua
пока включают язык, тему и акцент; специфические Flutter-настройки sidebar/autoScan/autoConnect/
confirmDelete/debug/demo явно отклоняются. Для дополнительных цветов Actipro `themeColor`
возвращает `nil`, а `accent` — настоящее имя цвета. Папки сохраняют идентификаторы
и иерархию, отображаются путями длиной до64символов; конфликтующие пути отклоняются.
Полная совместимость произвольного стороннего плагина не предполагается.

## The app

One app for Windows, macOS, Linux, Android, iOS and the browser. USB on desktop and in
the browser, Bluetooth on phones and in Chrome / Edge, TCP everywhere.

- **Slots** — all eight slots at a glance, with each card's name and LED color. Drag to
  reorder, tap to activate, edit HF and LF separately.
- **Multi-card LF** — one LF slot can hold up to eight LF cards of any emulated type, mixed
  freely, and send the next one on each tap of a reader, so you can walk through them until the
  right card opens. No other Chameleon firmware or app can do this; see
  [LF card cycling](docs/lf-card-cycle.md).
- **Read and dump** — ISO14443-A and LF scans. MIFARE Classic Mini, 1K and 4K dumps with
  your key dictionaries, edited as hex and emulated in one tap.
- **Saved cards** — a card library with search and color tags. Imports
  ChameleonUltraGUI, Proxmark3, Flipper `.nfc` / `.rfid`, MCT, `.bin` and `.eml`.
- **Cloning** — write LF IDs to T55xx (EM410x also to EM4305) and MIFARE Classic to Gen1a / Gen2 magic cards.
- **Emulator settings** — Gen1a / Gen2 modes, write modes, nonce detection for mfkey32,
  Ultralight / NTAG version, signature and counters.
- **HF sniffing** — answer a reader as the card in the active slot and record both
  sides, every frame labelled; MIFARE Classic keys come out of the reader's
  authentications (mfkey32, mfkey64). Proxmark3 `.trace` load and save.
- **LF sniffing** — record the 125 kHz signal at the antenna, see it as a zoomable
  waveform with a modulation and clock guess, and decode it as Manchester. Captures
  load and save as raw `.bin`, the same as the CLI's `lf sniff --out`.
- **Device settings** — LED animation, button actions, long-press threshold, sleep
  timeout, BLE pairing, factory reset and firmware updates over USB or Bluetooth.
- **Stop anything** — every long operation (dumps, key recovery attacks, writing magic
  cards, sniffing) has a Stop button and leaves the device in a usable state. In
  ChameleonUltraGUI many of them can only be waited out.

Key recovery includes Darkside, Nested, Static nested and mfkey32, plus the
fully managed C# Hardnested solver on desktop, Android, iOS and the web. Hardnested
needs a known key and substantial memory. Mobile and web calculations run locally;
devices with insufficient memory report an error.

The app speaks English, German, Spanish, French, Italian, Portuguese, Russian, Chinese
(Simplified), Japanese and Korean; pick the language in Settings.

Not there yet: editing slot data in place, backdoor key recovery and
QR sharing. The full list is in the
[roadmap](software/chamelab/ROADMAP.md).

The web version needs Chrome or Edge: Firefox and Safari support neither Web Serial nor
Web Bluetooth.

Sources, architecture and build steps: [software/chamelab](software/chamelab).

## The firmware

The ChameLab firmware runs on both ChameleonUltra and ChameleonLite and moves together
with the app: features are added on both sides and shipped in the same release.

- **Multi-card LF** — an LF slot holds up to eight cards of any emulated LF type
  (EM410x, Electra, HID Prox, ioProx, Viking, PAC, Jablotron, IDTECK, Pyramid), in any mix,
  and emulates the next one each time the reader's field appears — one steady card per tap,
  wrapping back to the first. The position is kept across sleep. The list is saved with the
  slot and follows it when slots are swapped. This is unique to ChameLab: neither the stock
  firmware nor other apps have it.
- **Slot LED colors** — each card in a slot can have its own LED color, so you can tell
  which card is active by looking at the device. A slot that holds both an HF and an LF
  card alternates between their two colors.
- **Fixes** — for example, saving no longer fails after many slot switches.

The full list is on the [website](https://m0n5ter.github.io/ChameLab/#firmware) and in
the [changelog](CHANGELOG.md).

### Updating

The app on desktop, Android and iOS updates the firmware for you: it offers the newest
release for your device and flashes it over USB or Bluetooth. To flash by hand:

1. Download `ultra-dfu-app-<version>.zip` or `lite-dfu-app-<version>.zip` from the
   [releases](https://github.com/m0n5ter/ChameLab/releases). The `-full` packages also
   contain the bootloader and are only needed for recovery.
2. Put the device into DFU mode: `hw dfu` in the CLI, or hold button B while plugging in
   USB.
3. Flash the package with [`firmware/flash-dfu-app.sh`](firmware/flash-dfu-app.sh),
   nRF Connect for mobile or nRF Connect for Desktop.

Slots, cards and settings are kept by an application update.

### Building

```bash
cd firmware
docker compose up --pull=always build-ultra
```

Use `build-lite` for the ChameleonLite. The packages land in `firmware/objects/`.

## The CLI

The C# command line client shares protocol and recovery engines with the app.
Build or run it with the .NET 10 SDK:

```bash
dotnet run --project software/chamelab/ChameLab.Cli -c Release
# Run several commands in the same connection/session:
dotnet run --project software/chamelab/ChameLab.Cli -c Release -- -c "hw connect" -c "hw version"
```

It covers what the app does not have yet, such as raw access to every firmware
command, for example `hw slot color`.

Use `dump_help` for all commands and `COMMAND --help` for options. Release packages
include the .NET runtime; launch `chamelab` (`chamelab.exe` on Windows).
Migration progress and verification are recorded in [docs/cli-migration.md](docs/cli-migration.md).

## Versions

Releases are tagged with the ChameLab version (`v1.0.8`), which goes up with every
release. The firmware has its own version in [`firmware/VERSION`](firmware/VERSION)
(`2.3.4-cl`) and changes only when the firmware does.

## Repository

| Folder | What's in it |
| --- | --- |
| [`software/chamelab`](software/chamelab) | The ChameLab app (Avalonia, .NET) |
| [`software/chamelab/ChameLab.Cli`](software/chamelab/ChameLab.Cli) | The C# CLI |
| [`firmware`](firmware) | Application and bootloader firmware for nRF52840 |
| [`hardware`](hardware) | Schematics and PCB files for Ultra and Lite |
| [`website`](website) | The download page and the web app host |

## License

Free software under the [GNU GPL v3](LICENSE).

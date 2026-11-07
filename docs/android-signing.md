# Подпись Android APK

Для ChameLab требуется отдельный релизный ключ. Ключи других приложений, в том числе
android-tv-date-time, не используются. Keystore и пароли создаёт и хранит владелец;
они не входят в исходники, артефакты, кэши или журналы сборки.

В Settings → Secrets and variables → Actions этого репозитория добавляются:

Это секреты GitHub Actions, а не файлы, прикрепляемые к GitHub Releases.

| Имя | Содержимое |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Keystore, закодированный в Base64 целиком |
| `ANDROID_KEYSTORE_PASSWORD` | Пароль хранилища |
| `ANDROID_KEY_ALIAS` | Alias ключа внутри хранилища |
| `ANDROID_KEY_PASSWORD` | Пароль этого ключа |

Владелец может создать отдельное JKS-хранилище через Android Studio
(Build → Generate Signed App Bundle or APK → APK → Create new) либо локально
через команду:

```sh
keytool -genkeypair -storetype JKS -keyalg RSA -keysize 4096 -validity 10000 \
  -alias chamelab-release -keystore /безопасный/путь/chamelab-release.jks
```

Пароли вводятся интерактивно; их не следует указывать в аргументах, сообщениях
или выводить для проверки. Значения секретов добавляются самим владельцем
непосредственно в GitHub. Сохраните резервную копию хранилища и паролей вне репозитория:
последующие обновления APK должны использовать тот же ключ.

- Релизная подпись используется только в сборке тега версии `v…` через Push handler.
  Если любого из четырёх секретов нет, сборка завершается ошибкой. Подмены
  релизного ключа отладочным не происходит.
- CI, pull request и ветки собираются без релизных секретов. Тестовое приложение
  имеет пакет `io.github.axiomshiftbit.chamelab.dev`, UI-тесты — `.uitests`,
  релизное — `io.github.axiomshiftbit.chamelab`.
- Временное хранилище создаётся с правами 600 вне checkout и удаляется при любом
  исходе задачи. В артефакты включаются только APK.
- Создание секретов само по себе ничего не выпускает. Push handler в форке может
  быть отключён; его включение и публикация тега — отдельные действия выпуска.
  Проверки качества не включают его автоматически и не публикуют релиз.

Проверять наличие разрешается по именам:
`ghx run axiomshiftbit secret list --repo axiomshiftbit/ChameLab`.
Чтение содержимого keystore, переменных окружения или
значений секретов для диагностики не требуется.

Пароли передаются сборщику через `env:`, поддерживаемый для APK
[официальными свойствами .NET for Android](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties#androidsigningkeypass).

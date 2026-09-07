# XrayUI — Xray client for Windows / Xray-клиент для Windows

[![Release](https://img.shields.io/github/v/release/tiredIsa/XrayUI-dev)](https://github.com/tiredIsa/XrayUI-dev/releases/latest)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-x64%20%7C%20ARM64-0078D4)](https://github.com/tiredIsa/XrayUI-dev/releases/latest)

**[Русский](#русский) · [English](#english) · [Download / Скачать](https://github.com/tiredIsa/XrayUI-dev/releases/latest)**

## Русский

**XrayUI — нативный графический клиент Xray для Windows с TUN, VLESS, VMess,
Shadowsocks и Trojan.** Интерфейс на WinUI 3, управление подписками,
правила маршрутизации и работа из системного трея.

Это независимый **форк [PhoenixNil/XrayUI-dev](https://github.com/PhoenixNil/XrayUI-dev)**,
развиваемый в `tiredIsa/XrayUI-dev`. Благодарим автора и участников исходного
проекта. У форка собственные релизы и канал обновлений.

### Что добавляет форк

- Русский интерфейс наряду с английским и китайским.
- Автоподключение в последнем успешном режиме TUN при входе в Windows.
- Обновления из этого репозитория с восстановлением активного подключения.
- Автоматическое обновление подписок с настройкой интервала и повторными попытками.

### Возможности

- VLESS, VMess, Shadowsocks, Trojan, Hysteria2, WireGuard и цепочки прокси.
- TUN и системный прокси; правила маршрутизации с GeoIP и Geosite.
- Импорт подписок, группировка и поиск серверов.
- Автозапуск, автоподключение и настройка оформления.

### Скачать и начать

1. Откройте [последний релиз](https://github.com/tiredIsa/XrayUI-dev/releases/latest).
2. Выберите **x64** для Intel/AMD или **ARM64** для Windows on ARM.
   Архив **`-wasdk`** содержит Windows App SDK runtime: используйте его,
   если обычная сборка не запускается из-за отсутствующего runtime.
3. Распакуйте **весь архив** в постоянную папку и запустите `XrayUI-dev.exe`.
   Оставьте `XrayUI.Updater.exe` рядом с приложением.
4. Добавьте подписку или сервер, выберите сервер и подключитесь.
   Для TUN требуются права администратора.

Приложение не предоставляет VPN-серверы или подписки — нужны собственные
параметры подключения. Для перехода с исходного проекта установите этот форк
вручную один раз.

### Обновления и поддержка

Новая версия отмечается на шестерёнке. Подтвердите установку: приложение скачает
архив, проверит его и перезапустится. Активное подключение восстанавливается;
во время установки оно прерывается.

[Обновления и выпуск релизов](docs/updates.md) · [Автозапуск TUN](docs/tun-autostart.md) ·
[Обновление подписок](docs/subscription-updates.md)

Сообщайте об ошибках в [Issues этого репозитория](https://github.com/tiredIsa/XrayUI-dev/issues).
Укажите версию, архитектуру Windows и шаги воспроизведения. Удалите из логов
ссылки подписок, ключи и личные данные перед публикацией.

## English

**XrayUI is a native Windows GUI client for Xray with TUN mode, VLESS, VMess,
Shadowsocks and Trojan support.** Built with WinUI 3, it provides subscription
management, routing rules and system tray operation.

This is an independent **fork of [PhoenixNil/XrayUI-dev](https://github.com/PhoenixNil/XrayUI-dev)**,
maintained in `tiredIsa/XrayUI-dev`. Credit goes to the original author and
contributors. This fork ships its own releases and update channel.

### Fork improvements

- Russian UI alongside English and Chinese.
- Restore the last successful TUN mode when connecting at Windows logon.
- In-app updates from this repository with active connection restoration.
- Scheduled subscription refreshes with configurable intervals and retries.

### Features

- VLESS, VMess, Shadowsocks, Trojan, Hysteria2, WireGuard and proxy chains.
- TUN and system proxy modes, custom GeoIP and Geosite routing rules.
- Subscription import, server grouping and search.
- Autostart, automatic connection and appearance settings.

### Download and get started

1. Open the [latest release](https://github.com/tiredIsa/XrayUI-dev/releases/latest).
2. Choose **x64** for Intel/AMD PCs or **ARM64** for Windows on ARM.
   The **`-wasdk`** archive bundles the Windows App SDK runtime; use it if the
   regular build cannot start because the runtime is missing.
3. Extract the **entire archive** into a permanent folder and run `XrayUI-dev.exe`.
   Keep `XrayUI.Updater.exe` alongside it.
4. Import a subscription or add a server, select it and connect.
   TUN mode requires administrator privileges.

The app does not provide VPN servers or subscriptions. Supply your own connection
settings. Users of the upstream project need to install this fork manually once
before receiving updates from this repository.

### Updates and support

An indicator on the settings button announces a newer version. Confirm the update
to download, verify and install it. The app restarts and restores an active
connection; connectivity is interrupted during installation.

[Subscription refresh behavior](docs/subscription-updates.md) ·
[Report a fork issue](https://github.com/tiredIsa/XrayUI-dev/issues)

Include the app version, Windows architecture and reproduction steps in reports.
Remove subscription URLs, credentials and personal information from logs.

## Build / Сборка

Requires the .NET 10 SDK, Visual Studio Windows/C++ build tools for WinUI 3 and
Native AOT, and Rust for the updater. Open `XrayUI-dev.slnx` in Visual Studio,
or publish from PowerShell:

```powershell
# x64
dotnet publish XrayUI-dev.csproj -c Release -r win-x64 -p:Platform=x64

# ARM64
dotnet publish XrayUI-dev.csproj -c Release -r win-arm64 -p:Platform=ARM64
```

Local `0.0.0-dev` builds skip update checks. Release builds use the Git tag version.
Локальные сборки `0.0.0-dev` не проверяют обновления; релиз получает номер из тега.

## License / Лицензия

[Apache License 2.0](LICENSE). Original project / Исходный проект:
[PhoenixNil/XrayUI-dev](https://github.com/PhoenixNil/XrayUI-dev).

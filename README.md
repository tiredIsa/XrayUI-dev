<img width="2172" height="724" alt="image" src="https://github.com/user-attachments/assets/ea4d4a40-76cd-48f5-abc5-ce3bc07d6f3c" />

<h1 align="center">XrayUI</h1>
A native Windows GUI client for the Xray core, built with <a style="text-decoration:none" href="https://docs.microsoft.com/windows/apps/winui">WinUI</a>. Designed to be a fast and lightweight proxy client.


## Features

- Support Shadowsocks, VMess, VLESS, Trojan, Hysteria2, WireGuard and Chain Proxy
- TUN mode
- Subscription import and update
- AI Unlock Status Detection
- Custom routing rules with geoip / geosite
- Auto-start on boot, auto-connect
- Theme and protocol color customization

## Automatic subscription updates

New subscriptions default to refreshing every 6 hours; choose Off, 1, 6, 12 or
24 hours when adding or editing a subscription. Existing subscriptions keep their
saved setting. The interval is measured from the last successful refresh.

While the app is running, overdue subscriptions refresh directly if its proxy is
off, or through its local SOCKS proxy if it is on. Failed proxy requests never
fall back to direct access. The scheduler checks on startup, every minute, when
the network becomes available, and when the proxy connects. Closing the app
pauses scheduling; overdue updates resume on the next launch.

Failed refreshes keep the previous servers. Transient errors and invalid/empty
responses retry after 1, 5, 15, 30, then 60 minutes (hourly thereafter). HTTP
401/403/404 wait the normal interval and show a link/access error. HTTP 429 honors
Retry-After, including for manual refreshes; without that header it uses the
retry delay. A restored network can retry transient failures immediately, and
connecting the proxy can retry a failed direct request, subject to rate limits.
When no network interface is available, no request or attempt is recorded.

## UI Preview
<img width="1465" height="982" alt="image" src="https://github.com/user-attachments/assets/ff288102-d874-4ecb-87dd-0a9d880cc1cf" />

## Download

Download the latest release [here](https://github.com/PhoenixNil/XrayUI-dev/releases/latest).

## Getting Started

> [!NOTE]
> Building XrayUI requires [Visual Studio](https://visualstudio.microsoft.com/vs/) and Windows 10 version 1809 or later. Publishing the application also requires the Rust toolchain. If this is your first time building a WinUI 3 application with the Windows App SDK, follow the [installation instructions](https://learn.microsoft.com/windows/apps/get-started/start-here).

Choose either Visual Studio or PowerShell to build the project.

### Option 1: Visual Studio

1. Open `XrayUI-dev.slnx` in Visual Studio.
2. Select the target platform, such as `x64` or `ARM64`.
3. Build the solution.

### Option 2: PowerShell

#### x64

```powershell
dotnet build -c Release -p:Platform=x64
dotnet publish -c Release -r win-x64 -p:Platform=x64
```

#### ARM64

```powershell
dotnet build -c Release -p:Platform=ARM64
dotnet publish -c Release -r win-arm64 -p:Platform=ARM64
```






##  Thanks


<p>
  <a href="https://linux.do">
    <img src="https://img.shields.io/badge/LinuxDo-community-1f6feb" alt="LinuxDo">
  </a>
</p>

## License

Apache License 2.0.

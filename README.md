# Unison

Windows 11 communication hub for native apps and web services. A [FreezeAnts](https://www.freezeants.com/unison/) product.

Keep Outlook, Teams, Gmail, WhatsApp, Home Assistant, and other services in one window. Native apps stay real Windows windows. Web apps run in isolated WebView2 profiles.

## Download

Windows 11, x64:

- [Latest installer](https://github.com/FreezeAnts/Unison/releases/latest)
- Product page: [freezeants.com/unison](https://www.freezeants.com/unison/)

Run the `Unison-Setup-*.exe` from that release. Windows may prompt for notification access so sidebar badges can update.

No FreezeAnts account or license key is required. Sign-in happens inside each service (Gmail, WhatsApp, and so on). Sessions stay on this PC under `%LocalAppData%\Unison\`.

## Features

- Add installed apps (Outlook, Teams, Store WhatsApp) or web services from search, presets, or a custom URL
- Drag-reorder the sidebar; optional top bar
- Unread badges from Windows toast notifications
- Per-service WebView2 profiles (cookies stay separate)
- Settings: theme, badge defaults, mute other web apps during a call
- Right-click a service to remove it

## Build from source

Prerequisites:

1. Windows 11
2. Developer Mode: Settings → System → For developers
3. Visual Studio 2022 (17.8+) or Visual Studio 2026 with .NET desktop development, WinUI / Windows application development, and Windows SDK 10.0.19041 or newer
4. .NET 8 SDK
5. Windows App Runtime matching the `Microsoft.WindowsAppSDK` package (1.6) if the unpackaged app fails to start — [runtime redistributable](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)

Do not run Visual Studio elevated.

```powershell
dotnet build Unison.sln -c Debug -p:Platform=x64
dotnet run --project Unison\Unison.csproj -c Debug -p:Platform=x64
```

Open `Unison.sln`, set the platform to **x64**, and press F5.

## Installer

Inno Setup 6 required. Each publish **bumps the patch version** in `Unison.csproj` (0.1.0 → 0.1.1) and names the setup exe to match:

```powershell
pwsh -ExecutionPolicy Bypass -File installer\build-inno.ps1
# same version:  -Bump none
# 0.2.0:         -Bump minor
# 1.0.0:         -Bump major
```

Output: `artifacts\installer\Unison-Setup-<version>.exe` (self-contained; not committed). Commit the csproj bump, then attach the exe to a GitHub Release `v<version>`.

## License

MIT. See [LICENSE](LICENSE).

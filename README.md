# Unison

Windows 11 communication hub for native apps and web services. A FreezeAnts product.

## Prerequisites

1. Windows 11
2. Developer Mode: Settings → System → For developers
3. Visual Studio 2022 (17.8+) or Visual Studio 2026 with:
   - .NET desktop development
   - Windows application development / WinUI application development
   - Windows SDK 10.0.19041 or newer
4. .NET 8 SDK (`dotnet --list-sdks`)
5. Windows App Runtime matching the `Microsoft.WindowsAppSDK` package (1.6). Install the [runtime redistributable](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) if the app fails to start.

Do not run Visual Studio elevated.

## Build and run

Open `Unison.sln` in Visual Studio, set the platform to **x64**, and press F5.

Or from a terminal:

```powershell
dotnet build Unison.sln -c Debug -p:Platform=x64
dotnet run --project Unison\Unison.csproj -c Debug -p:Platform=x64
```

## Installer

Build a Windows installer (Inno Setup 6 required):

```powershell
pwsh -ExecutionPolicy Bypass -File installer\build-inno.ps1
```

The setup exe is written to `artifacts\installer\`. It installs a self-contained Unison build under Program Files (or a per-user folder if you skip elevation).

Windows may prompt for notification access so sidebar badges can update.

## Notification badges

Sidebar badges count Windows toast notifications mapped to a service (Outlook, Teams, Discord, Slack, Gmail, WhatsApp). Selecting a service clears its badge. Messages do not steal focus.

## Web and native hosting

1. **+ Add Service** → add **Gmail** (or another Web item).
2. Select it. A WebView2 should fill the content area. Sign in; close Unison and reopen — you should still be signed in (`%LocalAppData%\Unison\WebProfiles\`).
3. Add a second web service. Each should keep its own cookies/session.
4. Switch to Outlook or Teams. The web view should hide; the native main window should appear. Switching back should show the web view again.

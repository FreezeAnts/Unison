#define AppName "Unison"
#define AppPublisher "FreezeAnts"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef WebView2Setup
  #define WebView2Setup "..\artifacts\webview2\MicrosoftEdgeWebview2Setup.exe"
#endif
#define BrandDir "graphics"

[Setup]
AppId={{A7C4E8F1-3B92-4D6A-9E11-8F2C1D0B4A77}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) {#AppPublisher}
DefaultDirName={autopf}\Unison
DefaultGroupName=Unison
DisableProgramGroupPage=yes
OutputBaseFilename=Unison-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile={#BrandDir}\wizard.bmp
WizardSmallImageFile={#BrandDir}\wizard-small.bmp
SetupIconFile={#BrandDir}\Unison.ico
UninstallDisplayIcon={app}\Unison.exe
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} by {#AppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
BeveledLabel=Created by FreezeAnts

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Unison.exe"; Comment: "Unison by FreezeAnts"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Unison.exe"; Tasks: desktopicon; Comment: "Unison by FreezeAnts"

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2..."; Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\Unison.exe"; Description: "Launch Unison"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2ClientKeyWow = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function NeedsWebView2: Boolean;
begin
  Result := not (
    RegValueExists(HKLM, WebView2ClientKey, 'pv') or
    RegValueExists(HKLM, WebView2ClientKeyWow, 'pv') or
    RegValueExists(HKCU, WebView2ClientKey, 'pv') or
    RegValueExists(HKCU, WebView2ClientKeyWow, 'pv'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if UninstallSilent then
    Exit;
  if MsgBox(
       'Remove Unison settings and saved web sessions?' + #13#10 + #13#10 +
       'This signs you out of Gmail, WhatsApp, and other web apps in Unison.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(ExpandConstant('{localappdata}\Unison'), True, True, True);
  end;
end;

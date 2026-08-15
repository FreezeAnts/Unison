#define AppName "Unison"
#define AppPublisher "FreezeAnts"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Unison.exe"; Comment: "Unison by FreezeAnts"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Unison.exe"; Tasks: desktopicon; Comment: "Unison by FreezeAnts"

[Run]
Filename: "{app}\Unison.exe"; Description: "Launch Unison"; Flags: nowait postinstall skipifsilent

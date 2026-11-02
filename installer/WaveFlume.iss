; WaveFlume installer (Inno Setup 6). Build with installer\build-release.ps1, which publishes the
; self-contained single-file exe, extracts plctag.dll, and then runs ISCC on this script.

#define AppName "WaveFlume"
#define AppVersion "0.5.0"
#define AppPublisher "IITM Coastal Hydraulics"
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{6E8B3C52-9A7D-4C1E-B0E4-2F6A5D3C7B91}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=WaveFlume-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=classic
UninstallDisplayIcon={app}\WaveFlume.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Self-contained single-file app (.NET 8 runtime, WPF, SkiaSharp and QuestPDF natives embedded).
Source: "{#PublishDir}\WaveFlume.exe"; DestDir: "{app}"; Flags: ignoreversion
; libplctag native library (EtherNet/IP). Shipped so the app never has to write into Program Files.
Source: "{#PublishDir}\plctag.dll"; DestDir: "{app}"; Flags: ignoreversion
; QuestPDF fallback font.
Source: "{#PublishDir}\LatoFont\*"; DestDir: "{app}\LatoFont"; Flags: ignoreversion recursesubdirs
; Default flume config and TagMap profiles (copied to %LOCALAPPDATA%\WaveFlume on first run).
Source: "{#PublishDir}\config\*"; DestDir: "{app}\config"; Flags: ignoreversion recursesubdirs
Source: "{#PublishDir}\sql\schema.sql"; DestDir: "{app}\sql"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WaveFlume.exe"
Name: "{group}\PLC I/O map"; Filename: "{app}\docs\PLC_IO_MAP.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WaveFlume.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WaveFlume.exe"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

; Not done by the installer on purpose:
; - Windows Firewall rules for the PLC port (502 / 9600 / 4840 / 44818 / 102): site IT decision.
; - A static IP on the flume NIC: set it on the flume PC (Settings shows the subnet hint).
; - MySQL: install separately, then Settings > MySQL Database > Create Schema.
; User data (settings, calibration, logs) lives in %LOCALAPPDATA%\WaveFlume and is kept on uninstall.

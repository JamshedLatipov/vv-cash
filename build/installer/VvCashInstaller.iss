; VvCash installer — Inno Setup 6
; Build: run build/installer/build_installer.ps1  (publishes app, then compiles this)

#define AppName "VvCash"
; Version comes from build_installer.ps1 (/DAppVersion=...), which reads it out of
; VvCash.csproj. The fallback only exists so the script still compiles if someone runs
; ISCC by hand; a real release always gets the value passed in.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppPublisher "VvCash"
#define AppExe "VvCash.exe"
#define SourcePath "..\..\publish\win-x64"

[Setup]
; AppId uniquely identifies the app for upgrades/uninstall — keep this GUID stable across releases.
AppId={{3C6986FA-A8A4-489D-94DA-9E1E7AD9E23D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputBaseFilename=VvCashInstaller
OutputDir=Output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Self-contained x64 app — install into real Program Files, refuse 32-bit Windows.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupIconFile=VvCash.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Bundle the whole published folder; skip debug symbols.
Source: "{#SourcePath}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Two entries on purpose. The first is the familiar "Run VvCash" checkbox on the last
; wizard page — postinstall makes it a checkbox, and skipifsilent means it does nothing
; during an unattended install.
;
; The second exists only for auto-update. The app downloads this installer and runs it
; with /VERYSILENT, so the entry above is skipped and the register would be left shut
; down with no way back. Check: WizardSilent is what keeps this entry out of a manual
; install — without it the cashier who installs by hand gets two copies of the app.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

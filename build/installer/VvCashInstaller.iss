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

; Two flavors are compiled from this one file, selected with /DFlavor=x86. One script
; rather than two, because the interesting parts below — the [Run] entries that keep
; auto-update from leaving a register shut down, and the exclusion of *.pdb — are the
; parts that would silently drift if they existed in two copies. Everything the flavors
; genuinely disagree about is gathered here.
#ifndef Flavor
  #define Flavor "x64"
#endif

#if Flavor == "x86"
  #define SourcePath "..\..\publish\win-x86"
  #define OutputName "VvCashInstaller-x86"
#else
  #define SourcePath "..\..\publish\win-x64"
  #define OutputName "VvCashInstaller"
#endif

[Setup]
; AppId uniquely identifies the app for upgrades/uninstall — keep this GUID stable across releases.
;
; Deliberately shared by both flavors. A machine runs one of them, never both, and the
; auto-updater only ever offers the flavor matching the running process (UpdateService
; polls a different manifest per architecture). Giving the flavors separate AppIds would
; instead make a 32-bit update install alongside the 64-bit copy rather than over it,
; leaving two registers' worth of shortcuts and two uninstall entries on one till.
AppId={{3C6986FA-A8A4-489D-94DA-9E1E7AD9E23D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputBaseFilename={#OutputName}
OutputDir=Output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Stated rather than left to the default. Inno's own default is 6.1sp1 today, but the
; 32-bit flavor exists precisely because of the Windows 7 registers, and that floor is
; not something to discover has moved after a toolchain upgrade silently locks them out.
MinVersion=6.1sp1

#if Flavor == "x86"
; No ArchitecturesAllowed: this flavor is for 32-bit Windows, and 32-bit is where every
; edition of Windows can run it. Leaving out ArchitecturesInstallIn64BitMode keeps Setup
; in 32-bit mode, so {autopf} resolves to Program Files on a 32-bit machine and to
; Program Files (x86) on a 64-bit one — in both cases the directory that matches the
; binaries being installed.
#else
; Self-contained x64 app — install into real Program Files, refuse 32-bit Windows.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

PrivilegesRequired=admin
SetupIconFile=VvCash.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Bundle the whole published folder; skip debug symbols.
;
; For the x86 flavor that folder also holds the app-local Universal CRT (ucrtbase.dll and
; the api-ms-win-*.dll set), placed there by build_installer.ps1. Windows 7 ships without
; the UCRT and every .NET Core runtime needs it, so without those files the register dies
; at startup with a missing api-ms-win-crt-*.dll and no window to say so. Shipping them
; beside the exe is Microsoft's documented app-local deployment, and it means the install
; needs no Windows Update, no KB and no separate redistributable on a machine that in
; practice can no longer reach Windows Update at all.
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

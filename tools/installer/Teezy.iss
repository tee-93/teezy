; TeezyFlow - Inno Setup script
;
; Produces dist\TeezyFlow-Setup.exe: one download, double-click, no administrator rights, no
; wizard pages. The user sees a progress bar for a second or two and then TeezyFlow is running.
;
; Build it with tools\build-installer.ps1, which publishes both architectures first and
; passes the version in. Compiling this file directly works too, provided dist\win-x64 and
; dist\win-arm64 already hold a Teezy.exe.
;
; The ~661 MB speech model is NOT in here. The app downloads it on first launch behind its
; own progress window. That keeps the download near 150 MB instead of 800 MB, at the cost of
; needing the network once - see tools\package.ps1 for the fully offline package to fall
; back on if huggingface.co turns out to be blocked.

#define AppName "TeezyFlow"
#define AppExeName "Teezy.exe"

; The product was Teezy until 1.11. Everything a person sees says TeezyFlow; the folder and
; the exe keep the old name, because the sign-in Run entry, pinned shortcuts and existing
; installs all point at that path. A new folder would mean a second copy, not an upgrade.
#define AppFolder "Teezy"
#define OldShortcut "Teezy"
#define AppPublisher "Zack Tarczynski"
#define AppUrl "https://github.com/tee-93/teezy"

; No default on purpose. The version of record is <Version> in Directory.Build.props, which
; is also what becomes the assembly version the About line reads - a default here would be a
; second place to forget, and forgetting it is exactly how About came to report 1.0.0 for four
; releases while the installer said otherwise. build-installer.ps1 passes it in.
#ifndef AppVersion
  #error AppVersion was not supplied. Build with tools\build-installer.ps1, which reads it from Directory.Build.props.
#endif

[Setup]
; Stable across every release. Changing it would make Windows treat an upgrade as a second,
; unrelated product and leave two entries in Apps & Features.
AppId={{B7C4E2A9-3F51-4D68-9A0C-6E5D8F1B27A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases

; lowest: never ask for elevation, never offer an all-users install. Everything lands under
; the user's own profile, which is what makes this installable on a managed work machine.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppFolder}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; x64compatible rather than x64os, so this also runs on ARM64 - which then gets the native
; ARM64 payload chosen below, not the emulated one.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0

; Every page is off. The ask was "download it and use it", and each page is a decision the
; user has no basis to make: the install location is fixed, there is one component, and
; starting at sign-in already lives in the app's own Settings, where the OS remains the
; source of truth. See README, "Starting at sign-in".
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes

; Teezy holds its own executable open, so an upgrade over a running copy fails with a file
; lock. Restart Manager closes it first.
CloseApplications=yes
RestartApplications=no

OutputDir=..\..\dist
OutputBaseFilename=TeezyFlow-Setup
SetupIconFile=..\..\src\Teezy.App\Teezy.ico
WizardStyle=modern
Compression=lzma2/max

; Deliberately off. Only one of the two payloads is ever extracted, and solid compression
; would force decompressing through the unused one to reach it.
SolidCompression=no

[Files]
; One executable is installed, chosen by CPU. The other is carried and discarded - the price
; of a single download that cannot be the wrong one.
Source: "..\..\dist\win-arm64\{#AppExeName}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion; Check: IsArm64
Source: "..\..\dist\win-x64\{#AppExeName}";   DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion; Check: not IsArm64

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Dictation and a voice assistant"

[InstallDelete]
; The Start menu entry from before the rename. Upgrading would otherwise leave both a Teezy
; and a TeezyFlow shortcut, pointing at the same exe.
Type: files; Name: "{autoprograms}\{#OldShortcut}.lnk"

[Run]
; No postinstall flag: with the finished page disabled there is nothing to tick, so this
; runs as an install step instead. nowait, because TeezyFlow stays resident in the tray.
Filename: "{app}\{#AppExeName}"; Flags: nowait skipifsilent

; An update from inside the app runs this installer silently, which skips the line above - so
; the app passes /update=1 when it wants to come back afterwards (Restart now), and not when
; the user chose Quit. See Updater.cs.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsRelaunchedUpdate

[UninstallDelete]
; The program folder only. History, dictionary, settings and the model live under
; %LOCALAPPDATA%\Teezy and are deliberately left behind - history is the only place dictated
; text still exists once the app it was typed into has moved on.
Type: dirifempty; Name: "{app}"

[Code]
// True only for an update the app started with Restart now. See the [Run] section.
function IsRelaunchedUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:update|0}') = '1');
end;

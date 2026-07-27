; Toy Bridge Launcher installer.
; Ships ONLY original tooling (the launcher, the toy bridge, and a fetch of the official
; Intiface Central installer) - never any game files. The launcher itself, once installed,
; is pointed at a game the person running it already owns, via its own "Browse..." button.

#define MyAppName "Toy Bridge Launcher"
#define MyAppVersion "1.0"
#define MyAppPublisher "Toy Bridge Launcher"
#define MyAppExeName "ToyLauncher.exe"

[Setup]
AppId={{6F2A9B10-4E3C-4B7A-9C2D-1F8A5E6D3B70}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ToyBridgeLauncher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=ToyBridgeLauncher-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
InfoAfterFile=after_install.txt
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "installintiface"; Description: "Install/update Intiface Central (recommended - needed to actually control toys)"; GroupDescription: "Additional tasks:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"

[Files]
Source: "stage\ToyLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
; ToyLauncher.exe is a PyInstaller onedir build (switched from onefile because onefile's runtime
; self-extraction into %TEMP% was getting its Qt platform plugin DLL quarantined by some AV/
; Defender setups, causing "no Qt platform plugin could be initialized" on first launch) - it now
; requires this whole companion folder to sit next to the exe, not just the exe by itself.
Source: "stage\_internal\*"; DestDir: "{app}\_internal"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\ButtplugBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\GamePatcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\ApkPatcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\apksigner.jar"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\HmvLive.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "stage\intiface-central-setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\Toy Bridge Launcher"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Toy Bridge Launcher"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Toy Bridge Launcher"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\intiface-central-setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; StatusMsg: "Installing Intiface Central..."; Tasks: installintiface; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Toy Bridge Launcher now"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\profiles.json"
Type: files; Name: "{app}\launcher_settings.json"
Type: files; Name: "{app}\apkpatcher.keystore"
Type: files; Name: "{app}\apkpatcher.keystore.pass"

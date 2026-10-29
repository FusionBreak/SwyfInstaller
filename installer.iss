#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#define AppName "SWYF Custom AI Installer"
#define AppPublisher "FusionBreak"
#define AppURL "https://github.com/FusionBreak/SwyfInstaller"
#define AppId "{{5894F6DF-98F3-4483-BB09-D21B72055681}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\Programs\SwyfInstaller
DefaultGroupName=SWYF Custom AI Installer
PrivilegesRequired=lowest
OutputDir=artifacts
OutputBaseFilename=SwyfInstaller-Setup-v{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\SwyfInstallerGui.exe

[Files]
Source: "SwyfInstaller.Gui\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"
Source: "SwyfInstaller\publish\SwyfInstaller.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; Bundled mod package: the app installs from {app}\package offline (see README). Skip if the checkout has no zip.
Source: "SWYF-Custom-AI-win-x64.zip"; DestDir: "{app}\package"; Flags: ignoreversion skipifsourcedoesntexist
Source: "SWYF-Custom-AI-win-x64.zip.sha256"; DestDir: "{app}\package"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\SWYF Custom AI Installer"; Filename: "{app}\SwyfInstallerGui.exe"
Name: "{group}\Uninstall SWYF Custom AI Installer"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SWYF Custom AI Installer"; Filename: "{app}\SwyfInstallerGui.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\SwyfInstallerGui.exe"; Description: "Launch SWYF Custom AI Installer"; Flags: nowait postinstall skipifsilent

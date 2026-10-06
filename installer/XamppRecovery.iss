#define AppVersion "1.0.2"
#define AppName "XAMPP MySQL Recovery Tool"
#define AppExe "XamppMySqlRecoveryTool.exe"

[Setup]
AppId={{B995C4B3-11CC-4391-870F-DCA5CFE8A25E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=vukhanh2005
AppPublisherURL=https://github.com/vukhanh2005/XamppRecovery
AppSupportURL=https://github.com/vukhanh2005/XamppRecovery/issues
DefaultDirName={localappdata}\Programs\XamppMySqlRecoveryTool
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.14393
OutputDir=..\release
OutputBaseFilename=XamppMySqlRecoveryTool-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\XamppRecoveryTool\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

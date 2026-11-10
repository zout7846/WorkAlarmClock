#ifndef AppVersion
  #define AppVersion "3.0.0"
#endif
#define AppName "WorkAlarmClock"
#define AppExe "WorkAlarmClock.exe"
#define ProjectURL "https://github.com/zout7846/WorkAlarmClock"

[Setup]
; Keep the v2 installer identity so it remains an upgrade of the same product.
#ifdef QAInstall
AppId={{AD6D3923-4725-422B-A872-3F2E60C0F8BE}
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
UsePreviousTasks=no
#else
AppId={{19828A53-6BB2-4337-8528-EC3306F3E456}
#endif
AppName={#AppName}
AppVerName={#AppName} {#AppVersion}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=zout7846
AppPublisherURL={#ProjectURL}
AppSupportURL={#ProjectURL}/issues
AppUpdatesURL={#ProjectURL}/releases/latest
DefaultDirName={localappdata}\Programs\WorkAlarmClock
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\WorkAlarmClock\Assets\iconfinder-info-4341298_120528.ico
OutputDir=..\artifacts\release
OutputBaseFilename=WorkAlarmClock-{#AppVersion}-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Include all managed/native/runtime dependencies, including ZXing barcode support.
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
#ifndef QAInstall
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
#endif

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data under %LOCALAPPDATA%\WorkAlarmClock is deliberately retained.
Type: files; Name: "{app}\*.pdb"

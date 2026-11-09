#ifndef PayloadDir
  #error PayloadDir must point to the self-contained published application.
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer"
#endif
#ifndef HhubDisplayVersion
  #define HhubDisplayVersion "0.5"
#endif
#ifndef HhubFileVersion
  #define HhubFileVersion "0.5.0.0"
#endif

[Setup]
AppId={{4F873C28-DBDD-487A-94E2-C1C1F176C830}
AppName=Hhub
AppVersion={#HhubDisplayVersion}
AppVerName=Hhub {#HhubDisplayVersion}
AppPublisher=Hhub
VersionInfoVersion={#HhubFileVersion}
VersionInfoProductVersion={#HhubFileVersion}
VersionInfoProductTextVersion={#HhubDisplayVersion}
DefaultDirName={localappdata}\Programs\Hhub
DefaultGroupName=Hhub
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Hhub-0.5-Setup-x64
SetupIconFile=..\src\Hhub.App\Assets\Hhub.ico
UninstallDisplayIcon={app}\Assets\Hhub.ico
UninstallDisplayName=Hhub {#HhubDisplayVersion}
WizardStyle=modern
ShowLanguageDialog=no
LanguageDetectionMethod=none
DisableProgramGroupPage=yes
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex=Hhub.Install.0.5

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[InstallDelete]
Type: files; Name: "{app}\bundled-plugins\*.hhub"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\Hhub-0.5.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Hhub"; Filename: "{app}\Hhub.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Hhub.ico"; AppUserModelID: "Hhub.App"
Name: "{autodesktop}\Hhub"; Filename: "{app}\Hhub.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Hhub.ico"; Tasks: desktopicon; AppUserModelID: "Hhub.App"

[Run]
Filename: "{app}\Hhub.App.exe"; Description: "启动 Hhub"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function GetHhubMutex: String;
begin
  Result := 'Local\Hhub-' + Uppercase(GetSHA256OfString(UTF8Encode(Uppercase(ExpandConstant('{localappdata}\Hhub')))));
end;

#include "HhubShutdown.iss"

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := PrepareHhubShutdown(GetHhubMutex, ExpandConstant('{app}\Hhub.App.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and CheckForMutexes(GetHhubMutex) then
    RaiseException('Hhub 又启动了。请从托盘退出后重新运行安装程序。');
end;

#include "HhubUninstall.iss"

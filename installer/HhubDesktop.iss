#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required
#endif
#ifndef HhubVersion
  #error HhubVersion is required
#endif

[Setup]
AppId={{7A154C79-D4F2-4F6F-A991-A1575EE2CDD8}
AppName=HHub
AppVersion={#HhubVersion}
AppPublisher=HHub
DefaultDirName={localappdata}\Programs\HHub
DefaultGroupName=HHub
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=HHub-{#HhubVersion}-Setup-x64
SetupIconFile=..\src\Hhub.App\Assets\Hhub.ico
UninstallDisplayIcon={app}\Assets\Hhub.ico
WizardStyle=modern dynamic
WizardSizePercent=110,105
DisableWelcomePage=no
ShowLanguageDialog=no
DisableProgramGroupPage=yes
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex=Hhub.Desktop.Install

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Messages]
WelcomeLabel1=安装 HHub {#HhubVersion}
WelcomeLabel2=安装程序会自动关闭正在运行的旧版 HHub。已安装的插件和个人设置会保留。%n%n安装完成后，你可以从开始菜单打开 HHub。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\Hhub.Tray.exe"; DestName: "HHubCloseForSetup.exe"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Delete only known files delivered by old installers. The user plugin profile is separate.
Type: files; Name: "{app}\bundled-plugins\bundle.json"
Type: files; Name: "{app}\bundled-plugins\hhub.system-monitor-0.1.2-win-x64.hhub"
Type: files; Name: "{app}\bundled-plugins\hhub.system-monitor-0.1.3-win-x64.hhub"
Type: files; Name: "{app}\bundled-plugins\hhub.device-discovery-0.1.1-win-x64.hhub"
Type: dirifempty; Name: "{app}\bundled-plugins"
Type: files; Name: "{app}\PawnIO-NOTICE.txt"

[Icons]
Name: "{autoprograms}\HHub"; Filename: "{app}\Hhub.Tray.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Hhub.ico"; AppUserModelID: "HHub.Desktop"
Name: "{autodesktop}\HHub"; Filename: "{app}\Hhub.Tray.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Hhub.ico"; AppUserModelID: "HHub.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\Hhub.Tray.exe"; Description: "启动 HHub"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  try
    ExtractTemporaryFile('HHubCloseForSetup.exe');
    if not Exec(ExpandConstant('{tmp}\HHubCloseForSetup.exe'),
      '--close-installation "' + ExtractFileDir(AddBackslash(ExpandConstant('{app}')) + 'Hhub.Tray.exe') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := '无法启动 HHub 的安装关闭程序。'
    else if ExitCode <> 0 then
      Result := '安装程序未能自动关闭上一个版本的 HHub。请重试安装。';
    if Result <> '' then Log('HHub shutdown failed: ' + IntToStr(ExitCode));
  except
    Log(GetExceptionMessage);
    Result := '无法准备 HHub 升级。';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Entry, Prefix: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Prefix := '"' + ExpandConstant('{app}\Hhub.Tray.exe') + '"';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Hhub.Rebuild', Entry)
    and (CompareText(Copy(Entry, 1, Length(Prefix)), Prefix) = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Hhub.Rebuild');
end;

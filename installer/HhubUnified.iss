#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\unified-v1\installer"
#endif
#define PreviewVersion "0.6.0-preview.1"

[Setup]
AppId={{FA108730-86A0-4F40-98F7-B04F44FBF943}
AppName=Hhub 主体预览
AppVersion={#PreviewVersion}
AppPublisher=Hhub
DefaultDirName={localappdata}\Programs\Hhub-Unified-Preview
DefaultGroupName=Hhub 主体预览
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Hhub-Unified-Preview-Setup-x64
SetupIconFile=..\src\Hhub.App\Assets\Hhub.ico
UninstallDisplayIcon={app}\Assets\Hhub.ico
WizardStyle=modern
ShowLanguageDialog=no
DisableProgramGroupPage=yes
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex=Hhub.Unified.Preview.Install

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建预览版桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,install-profile.txt,installation-version.txt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Hhub 主体预览"; Filename: "{app}\Hhub.Preview.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Hhub 主体预览"; Filename: "{app}\Hhub.Preview.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Hhub.Preview.exe"; Description: "启动 Hhub 主体预览"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{app}\install-profile.txt"
Type: files; Name: "{app}\installation-version.txt"

[Code]
var
  SelectedProfile: String;
  UninstallLease: THandle;

function CreateProfileMutex(Attributes: THandle; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';

function ReadUtf8(Path: String): String;
var Raw: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(Path, Raw) then begin
    Result := Trim(UTF8Decode(Raw));
    if (Length(Result) > 0) and (Result[1] = #$FEFF) then Delete(Result, 1, 1);
  end;
end;

function ProfileRoot: String;
begin
  if SelectedProfile = '' then begin
    SelectedProfile := ReadUtf8(ExpandConstant('{app}\install-profile.txt'));
    if SelectedProfile = '' then SelectedProfile := ExpandConstant('{param:PROFILEDIR|{localappdata}\Hhub-Unified-Preview}');
    SelectedProfile := RemoveBackslashUnlessRoot(ExpandFileName(SelectedProfile));
  end;
  Result := SelectedProfile;
end;

function GetHhubMutex: String;
begin
  Result := 'Local\Hhub-' + Uppercase(GetSHA256OfString(UTF8Encode(Uppercase(ProfileRoot))));
end;

#include "HhubShutdown.iss"

function RunMaintenance(Command, Choice: String): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\Hhub.Preview.exe'), Command + ' "' + ProfileRoot + '" ' + Choice,
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Version: String; Entry: TFindRec; HasFiles: Boolean;
begin
  Result := '';
  SelectedProfile := '';
  if (Pos('"', ProfileRoot) > 0) or (Length(ProfileRoot) <= 3) then begin
    Result := '数据目录无效。'; Exit;
  end;
  HasFiles := False;
  if FindFirst(ExpandConstant('{app}\*'), Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then HasFiles := True;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
  Version := ReadUtf8(ExpandConstant('{app}\installation-version.txt'));
  if HasFiles and (Version <> '{#PreviewVersion}') then begin
    Result := '此预览安装器仅支持全新安装和同版本修复。跨版本更新尚未开放，请选择新的空安装目录。'; Exit;
  end;
  if DirExists(ProfileRoot) and not FileExists(ProfileRoot + '\profile.json') then begin
    Result := '数据目录缺少归属记录，请选择新的数据目录，不会接管已有目录。'; Exit;
  end;
  Result := PrepareHhubShutdown(GetHhubMutex, ExpandConstant('{app}\Hhub.Preview.exe'));
  if (Result = '') and FileExists(ProfileRoot + '\profile.json') and FileExists(ExpandConstant('{app}\Hhub.Preview.exe')) then
    if not RunMaintenance('--plan-profile-cleanup', '--keep-presets') then
      Result := '无法确认现有数据兼容或插件仍被占用，未开始修复安装。';
end;

procedure CurStepChanged(Step: TSetupStep);
begin
  if (Step = ssInstall) and CheckForMutexes(GetHhubMutex) then
    RaiseException('Hhub 又启动了，请退出后重试安装。');
  if Step = ssPostInstall then begin
    if not RunMaintenance('--initialize-profile', '--keep-presets') then
      RaiseException('数据目录初始化或兼容检查失败，未启动主体。');
    if not SaveStringToFile(ExpandConstant('{app}\install-profile.txt'), UTF8Encode(ProfileRoot), False)
      or not SaveStringToFile(ExpandConstant('{app}\installation-version.txt'), UTF8Encode('{#PreviewVersion}'), False) then
      RaiseException('无法写入安装记录。');
  end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var ErrorText: String; DeleteData: Boolean; Index, ErrorCode: Integer;
begin
  if Step <> usAppMutexCheck then Exit;
  SelectedProfile := '';
  DeleteData := False;
  if UninstallSilent then begin
    for Index := 1 to ParamCount do
      if CompareText(ParamStr(Index), '/DELETEUSERDATA') = 0 then DeleteData := True;
  end else
    DeleteData := SuppressibleMsgBox('是否同时删除个人预设？插件程序、缓存和更新暂存始终会移除。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  ErrorText := PrepareHhubShutdown(GetHhubMutex, ExpandConstant('{app}\Hhub.Preview.exe'));
  if ErrorText <> '' then RaiseException(ErrorText);
  if DirExists(ProfileRoot) then begin
    if DeleteData then begin
      if not RunMaintenance('--remove-profile', '--delete-data') then RaiseException('数据清理失败，卸载已停止，请检查占用和数据目录。');
    end else if not RunMaintenance('--cleanup-profile', '--keep-presets') then
      RaiseException('预设保留或插件清理失败，卸载已停止，请检查数据格式和占用。');
  end;
  UninstallLease := CreateProfileMutex(0, False, GetHhubMutex);
  ErrorCode := DLLGetLastError;
  if (UninstallLease = 0) or (ErrorCode = 183) then RaiseException('Hhub 又启动了，卸载已停止，请退出后重试。');
end;

procedure DeinitializeUninstall;
begin
  if UninstallLease <> 0 then CloseHandle(UninstallLease);
end;

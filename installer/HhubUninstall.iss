var
  UninstallLease: THandle;
  DeleteUserData: Boolean;

function CreateUninstallMutex(Attributes: THandle; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function GetUninstallPathAttributes(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function ExistingDirectory(Path: String; RejectReparsePoint: Boolean): Boolean;
var
  Attributes: Cardinal;
  ErrorCode: Integer;
begin
  Result := False;
  Attributes := GetUninstallPathAttributes(Path);
  ErrorCode := DLLGetLastError;
  if Attributes = $FFFFFFFF then begin
    if (ErrorCode <> 2) and (ErrorCode <> 3) then
      RaiseException('无法检查卸载目录：' + Path + '。' + SysErrorMessage(ErrorCode));
    Exit;
  end;
  if (Attributes and $10) = 0 then
    RaiseException('卸载目录路径被其他文件占用：' + Path);
  if RejectReparsePoint and ((Attributes and $400) <> 0) then
    RaiseException('卸载目录指向其他位置。请确认目录归属后重试：' + Path);
  Result := True;
end;

procedure RemoveOwnedDirectory(Path: String);
begin
  if ExistingDirectory(Path, False) and (not DelTree(Path, True, True, True)) then
    RaiseException('无法清理以下目录。请关闭占用文件的程序后重试卸载：' + Path);
end;

procedure RemoveLegacyPluginBackups(Root: String);
var
  Entry: TFindRec;
  Path: String;
begin
  if not ExistingDirectory(Root, True) then Exit;
  if not FindFirst(Root + '\*', Entry) then Exit;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') and ((Entry.Attributes and $10) <> 0)
        and ((Entry.Attributes and $400) = 0) then begin
        Path := Root + '\' + Entry.Name;
        if FileExists(Path + '\plugin.json') then RemoveOwnedDirectory(Path);
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

procedure RemoveHhubStartupEntry;
var
  Key, Command: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if RegQueryStringValue(HKCU, Key, 'Hhub', Command) and
    (CompareText(Command, '"' + ExpandConstant('{app}\Hhub.App.exe') + '" --startup') = 0) then
    if not RegDeleteValue(HKCU, Key, 'Hhub') then
      RaiseException('无法删除 Hhub 的开机启动项，请重试卸载。');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ErrorText, DataRoot: String;
  ErrorCode, Index: Integer;
begin
  if CurUninstallStep = usAppMutexCheck then begin
    if UninstallSilent then begin
      for Index := 1 to ParamCount do
        if CompareText(ParamStr(Index), '/DELETEUSERDATA') = 0 then DeleteUserData := True;
    end else
      DeleteUserData := SuppressibleMsgBox(
        '卸载将移除 Hhub 和已安装的插件。是否同时删除设置、场景和已知旧版插件的数据？',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    ErrorText := PrepareHhubShutdown(GetHhubMutex, ExpandConstant('{app}\Hhub.App.exe'));
    if ErrorText <> '' then RaiseException(ErrorText);
    UninstallLease := CreateUninstallMutex(0, False, GetHhubMutex);
    ErrorCode := DLLGetLastError;
    if (UninstallLease = 0) or (ErrorCode = 183) then
      RaiseException('无法锁定 Hhub 数据目录。请退出 Hhub 后重试卸载。');
  end;
  if CurUninstallStep <> usUninstall then Exit;

  DataRoot := ExpandConstant('{localappdata}\Hhub');
  if ExistingDirectory(DataRoot, True) then begin
    if DeleteUserData then RemoveOwnedDirectory(DataRoot)
    else begin
      RemoveOwnedDirectory(DataRoot + '\plugins');
      RemoveOwnedDirectory(DataRoot + '\plugin-updates');
      RemoveOwnedDirectory(DataRoot + '\plugin-trash');
      RemoveOwnedDirectory(DataRoot + '\staging');
      RemoveOwnedDirectory(DataRoot + '\downloads');
      RemoveOwnedDirectory(DataRoot + '\ui-cache');
      RemoveLegacyPluginBackups(DataRoot + '\backups');
    end;
  end;
  if DeleteUserData then begin
    RemoveOwnedDirectory(ExpandConstant('{localappdata}\Hhub.Audio'));
    RemoveOwnedDirectory(ExpandConstant('{localappdata}\Hhub.Mouse'));
    RemoveOwnedDirectory(ExpandConstant('{localappdata}\Hhub.SystemMonitor'));
    RemoveOwnedDirectory(ExpandConstant('{localappdata}\Hhub.WindowsSettings'));
  end;
  RemoveHhubStartupEntry;
end;

procedure DeinitializeUninstall;
begin
  if UninstallLease <> 0 then CloseHandle(UninstallLease);
end;

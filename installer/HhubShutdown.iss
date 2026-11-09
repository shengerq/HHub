function OpenEvent(Access: Cardinal; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetWindowThreadProcessId(Window: HWND; var ProcessId: Cardinal): Cardinal;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function OpenProcess(Access: Cardinal; Inherit: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: Cardinal; Name: String; var Size: Cardinal): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function RmStartSession(var Session: Cardinal; Flags: Cardinal; Key: String): Cardinal;
  external 'RmStartSession@rstrtmgr.dll stdcall';
function RmRegisterResources(Session, Count: Cardinal; Files: TArrayOfString;
  ProcessCount, Processes, ServiceCount, Services: Cardinal): Cardinal;
  external 'RmRegisterResources@rstrtmgr.dll stdcall';
function RmShutdown(Session, Flags, Callback: Cardinal): Cardinal;
  external 'RmShutdown@rstrtmgr.dll stdcall';
function RmEndSession(Session: Cardinal): Cardinal;
  external 'RmEndSession@rstrtmgr.dll stdcall';

function LegacyHhubPath(InstalledExecutable: String): String;
var
  Window: HWND;
  Process: THandle;
  ProcessId, Size: Cardinal;
  Path: String;
begin
  Result := InstalledExecutable;
  Window := FindWindowByWindowName('Hhub');
  if Window = 0 then Exit;
  if GetWindowThreadProcessId(Window, ProcessId) = 0 then Exit;
  Process := OpenProcess($1000, False, ProcessId);
  if Process = 0 then Exit;
  try
    Size := 32768;
    Path := StringOfChar(#0, Size);
    if QueryFullProcessImageName(Process, 0, Path, Size) then begin
      SetLength(Path, Size);
      if CompareText(ExtractFileName(Path), 'Hhub.App.exe') = 0 then Result := Path;
    end;
  finally
    CloseHandle(Process);
  end;
end;

function CloseLegacyHhub(Executable: String): Cardinal;
var
  Session: Cardinal;
  Key: String;
  Files: TArrayOfString;
begin
  Key := StringOfChar(#0, 33);
  Result := RmStartSession(Session, 0, Key);
  if Result <> 0 then Exit;
  try
    SetArrayLength(Files, 1);
    Files[0] := Executable;
    Result := RmRegisterResources(Session, 1, Files, 0, 0, 0, 0);
    if Result = 0 then Result := RmShutdown(Session, 0, 0);
  finally
    RmEndSession(Session);
  end;
end;

function PrepareHhubShutdown(MutexName, Executable: String): String;
var
  Request: THandle;
  LegacyResult: Cardinal;
  Attempt: Integer;
  Requested: Boolean;
begin
  Result := '';
  Requested := False;
  Request := OpenEvent(2, False, MutexName + '.ExitForUpdate.v1');
  if Request <> 0 then begin
    try
      Log('Requesting Hhub shutdown for update.');
      Requested := SetEvent(Request);
    finally
      CloseHandle(Request);
    end;
    if not Requested then begin
      Result := '无法向 Hhub 发送关闭通知。请从托盘退出后重试。';
      Exit;
    end;
  end else begin
    if CheckForMutexes(MutexName) then Executable := LegacyHhubPath(Executable);
    if FileExists(Executable) then begin
      Log('Requesting legacy Hhub shutdown through Restart Manager.');
      LegacyResult := CloseLegacyHhub(Executable);
      if LegacyResult <> 0 then begin
        Log(Format('Restart Manager result: %d', [LegacyResult]));
        Result := '旧版 Hhub 未能正常关闭。请从托盘退出后重试。';
        Exit;
      end;
    end;
  end;
  if Requested then begin
    for Attempt := 1 to 300 do begin
      if not CheckForMutexes(MutexName) then Break;
      Sleep(200);
    end;
  end;
  if CheckForMutexes(MutexName) then
    Result := 'Hhub 尚未退出。请检查插件是否仍在运行，然后从托盘退出并重试。'
  else if Requested then
    Log('Hhub released the instance lease after shutdown.');
end;

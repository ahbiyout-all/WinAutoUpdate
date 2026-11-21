; ============================================================================
;  UpdateAgent LabManager - Inno Setup 6 installer (교사 PC)
;
;  설치 흐름
;    1) 실행 중인 기존 앱 강제 종료
;    2) 라이선스 고지
;    3) 설치 직전 기존 설치 자동 감지 후 무인 제거(설정 파일은 백업/복원)
;    4) 개별 파일(Unpacked Multi-File) 설치
;    5) 바탕화면 바로가기 선택
;    6) 방화벽 규칙(기본 이름/버전 이름) 등록
;    7) 앱 실행
;
;  무인 설치
;    UpdateAgent.LabManager-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;
;  AppId는 제품 고유 값이며 절대 변경하지 않는다.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.10"
#endif
#ifndef StageRoot
  #define StageRoot "..\..\artifacts\inno\stage\labmanager"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\inno"
#endif
#ifndef NoticeRtf
  #define NoticeRtf "..\UpdateAgent.Agent.Installer\payload\notice.ko.rtf"
#endif

[Setup]
AppId={{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}
AppName=UpdateAgent LabManager
AppVersion={#AppVersion}
AppVerName=UpdateAgent LabManager {#AppVersion}
AppPublisher=AhBiYout
AppPublisherURL=https://github.com/ahbiyout-all/WinAutoUpdate
AppSupportURL=https://github.com/ahbiyout-all/WinAutoUpdate/issues
AppUpdatesURL=https://github.com/ahbiyout-all/WinAutoUpdate/releases
AppCopyright=Copyright (C) AhBiYout
VersionInfoCompany=AhBiYout
VersionInfoDescription=UpdateAgent LabManager Installer
VersionInfoCopyright=Copyright (C) AhBiYout
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\UpdateAgent\LabManager
DefaultGroupName=UpdateAgent
DisableProgramGroupPage=yes
DisableWelcomePage=yes
AllowNoIcons=yes
LicenseFile={#NoticeRtf}
OutputDir={#OutputDir}
OutputBaseFilename=UpdateAgent.LabManager-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
Uninstallable=yes
UninstallDisplayName=UpdateAgent LabManager {#AppVersion}
UninstallDisplayIcon={app}\UpdateAgent.LabManager.Desktop.exe
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
DirExistsWarning=no
UsePreviousAppDir=yes
SetupLogging=yes
SetupIconFile=..\..\assets\icons\UpdateAgent.LabManager.ico
ShowLanguageDialog=no
LanguageDetectionMethod=none

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 UpdateAgent LabManager 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: checkedonce

[Files]
; 헤드리스 API 서버와 브라우저 앱 모드 런처를 같은 폴더에 설치한다.
; wwwroot(대시보드)도 서버가 제공하므로 함께 설치된다.
; 문서/심볼/압축 파일은 스테이징에서 제거된다.
Source: "{#StageRoot}\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[InstallDelete]
; Remove all stale files and directories from earlier upgrades, not just wwwroot.
; appsettings.json is backed up in PrepareToInstall and restored in ssPostInstall;
; the backup in the temp folder is not touched by these entries.
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{group}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\UpdateAgent.LabManager.Desktop.exe"
Name: "{group}\UpdateAgent LabManager 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\UpdateAgent.LabManager.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; Description: "UpdateAgent LabManager 실행"; Flags: postinstall nowait skipifsilent; Check: ShouldLaunchDashboard

[Code]
var
  OldInnoUninstall: String;
  LabManagerConfigFile: String;
  LabManagerConfigBackup: String;
  LabManagerConfigRestoreNeeded: Boolean;
  FirewallPort: String;
  LaunchSkippedForDefaultTokens: Boolean;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  FirewallGroup = 'UpdateAgent LabManager';
  SampleAdminToken = 'sample-admin-token-replace-before-pilot';
  SampleAgentToken = 'sample-agent-token-replace-before-pilot';

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Code;
end;

{ [Code] cannot capture stdout, so tasklist output is redirected to a temp file. }
function IsProcessRunning(const ImageName: String): Boolean;
var
  Cmd, OutFile, Needle: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  OutFile := AddBackslash(GetTempDir) + 'UpdateAgent.LabManager.process.check.txt';
  DeleteFile(OutFile);
  { CSV keeps the full image name; the default table view truncates it to
    25 characters, which hides UpdateAgent.LabManager.Desktop.exe and others. }
  Cmd := '/c ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq ' +
    ImageName + '" /NH /FO CSV > "' + OutFile + '" 2>&1"';
  RunHidden(ExpandConstant('{sys}\cmd.exe'), Cmd);
  if not LoadStringsFromFile(OutFile, Lines) then Exit;
  Needle := '"' + Uppercase(ImageName) + '"';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos(Needle, Uppercase(Lines[I])) > 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

{ taskkill returns before the target is guaranteed to have exited; poll until gone. }
function KillAndWait(const ImageName: String): Boolean;
var
  I: Integer;
begin
  Result := True;
  if not IsProcessRunning(ImageName) then Exit;
  Log('Terminating running app: ' + ImageName);
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM "' + ImageName + '"');
  for I := 0 to 19 do
  begin
    if not IsProcessRunning(ImageName) then Exit;
    Sleep(250);
  end;
  Result := not IsProcessRunning(ImageName);
  if not Result then
    Log('Failed to terminate running app: ' + ImageName);
end;

function TerminateLabManagerApps: Boolean;
begin
  Result := True;
  if not KillAndWait('UpdateAgent.LabManager.Desktop.exe') then Result := False;
  if not KillAndWait('UpdateAgent.LabManager.exe') then Result := False;
end;

function ExtractExePath(const Command: String): String;
var
  P: Integer;
begin
  if Copy(Command, 1, 1) = '"' then
  begin
    P := Pos('"', Copy(Command, 2, Length(Command) - 1));
    if P > 0 then
      Result := Copy(Command, 2, P - 1)
    else
      Result := RemoveQuotes(Command);
  end
  else
  begin
    P := Pos(' ', Command);
    if P > 0 then
      Result := Copy(Command, 1, P - 1)
    else
      Result := Command;
  end;
end;

function FindInnoUninstallString: String;
var
  Value: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, UninstallKey + '\{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}_is1', 'UninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKLM32, UninstallKey + '\{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}_is1', 'UninstallString', Value) then
    Result := Value;
end;

function WaitForInnoUninstallComplete: Boolean;
var
  I: Integer;
  KeyExists: Boolean;
begin
  Result := True;
  for I := 0 to 40 do
  begin
    KeyExists :=
      RegKeyExists(HKLM64, UninstallKey + '\{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}_is1') or
      RegKeyExists(HKLM32, UninstallKey + '\{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}_is1');
    if not KeyExists then Exit;
    Sleep(250);
  end;
  Result := not KeyExists;
end;

function InitializeSetup: Boolean;
begin
  { 1) 종료 → 언인스톨 → 설치 순서를 위해 실행 중인 앱을 먼저 정리한다. }
  TerminateLabManagerApps;

  { 2) 기존 설치는 선택 없이 PrepareToInstall에서 항상 제거한다. }
  OldInnoUninstall := FindInnoUninstallString;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe: String;
begin
  Result := '';

  { 언인스톨보다 먼저 모든 실행 중인 앱을 종료한다. 종료 실패 시 설치를 중단한다. }
  if not TerminateLabManagerApps then
  begin
    Result := '기존 UpdateAgent LabManager 앱을 종료하지 못해 설치를 중단합니다. ' +
      '대시보드 창과 LabManager 서버를 닫은 뒤 다시 실행해 주세요.';
    Exit;
  end;

  { 이전 제거 프로그램/InstallDelete가 appsettings.json을 지우므로 먼저 백업한다. }
  LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  LabManagerConfigBackup := AddBackslash(GetTempDir) + 'UpdateAgent.LabManager.appsettings.json.bak';
  LabManagerConfigRestoreNeeded := False;
  DeleteFile(LabManagerConfigBackup);
  if FileExists(LabManagerConfigFile) then
  begin
    Log('Backing up LabManager appsettings.json: ' + LabManagerConfigFile + ' -> ' + LabManagerConfigBackup);
    if not CopyFile(LabManagerConfigFile, LabManagerConfigBackup, False) then
    begin
      Result := '기존 LabManager appsettings.json 백업에 실패해 설치를 중단합니다: ' + LabManagerConfigBackup;
      Exit;
    end;
    LabManagerConfigRestoreNeeded := True;
  end;

  if OldInnoUninstall <> '' then
  begin
    UninstallExe := ExtractExePath(OldInnoUninstall);
    if (UninstallExe = '') or not FileExists(UninstallExe) then
    begin
      Result := '기존 UpdateAgent LabManager 제거 프로그램을 찾을 수 없어 설치를 중단합니다.';
      Exit;
    end;

    Code := -1;
    if not Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      Result := '기존 UpdateAgent LabManager 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.';
      Exit;
    end;

    if not WaitForInnoUninstallComplete then
    begin
      Result := '기존 UpdateAgent LabManager 제거가 완료되지 않아 설치를 중단합니다.';
      Exit;
    end;
  end;

  { 설정 복원은 InstallDelete/[Files]가 끝난 뒤 ssPostInstall에서 수행한다. }
end;

function ExtractPortFromUrlLine(const Line: String): String;
var
  ProtocolPos, StartPos, BreakPos, ColonPos, I: Integer;
begin
  Result := '';
  ProtocolPos := Pos('http://', Line);
  if ProtocolPos > 0 then
    StartPos := ProtocolPos + 7
  else
  begin
    ProtocolPos := Pos('https://', Line);
    if ProtocolPos = 0 then Exit;
    StartPos := ProtocolPos + 8;
  end;

  BreakPos := Length(Line) + 1;
  ColonPos := 0;
  for I := StartPos to Length(Line) do
  begin
    if (Line[I] = '/') or (Line[I] = '"') then
    begin
      BreakPos := I;
      Break;
    end;
    if Line[I] = ':' then ColonPos := I;
  end;
  if ColonPos = 0 then Exit;

  Result := Copy(Line, ColonPos + 1, BreakPos - ColonPos - 1);
  I := 1;
  while I <= Length(Result) do
  begin
    if (Result[I] < '0') or (Result[I] > '9') then
    begin
      Result := '';
      Exit;
    end;
    Inc(I);
  end;
  if (Length(Result) = 0) or (Length(Result) > 5) or
     ((Length(Result) = 5) and (Result > '65535')) then
    Result := '';
end;

function GetConfiguredFirewallPort: String;
var
  Lines: TStringList;
  I: Integer;
  Candidate: String;
begin
  Result := '5080';
  if LabManagerConfigFile = '' then
    LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  if not FileExists(LabManagerConfigFile) then Exit;

  Lines := TStringList.Create;
  try
    Lines.LoadFromFile(LabManagerConfigFile);
    for I := 0 to Lines.Count - 1 do
    begin
      if (Pos('"Urls"', Lines.Strings[I]) > 0) or
         (Pos('"ListenUrl"', Lines.Strings[I]) > 0) then
      begin
        Candidate := ExtractPortFromUrlLine(Lines.Strings[I]);
        if Candidate <> '' then
        begin
          Result := Candidate;
          Exit;
        end;
      end;
    end;
  finally
    Lines.Free;
  end;
end;

procedure AddFirewallRule(const RuleName, Description: String);
begin
  { Port-based rule aligned with scripts\allow-labmanager.ps1; Public stays blocked. }
  RunHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="' + RuleName +
    '" dir=in action=allow protocol=TCP localport=' + FirewallPort +
    ' enable=yes profile=domain,private group="' + FirewallGroup +
    '" description="' + Description + '"');
end;

procedure ApplyFirewallRules;
begin
  { 복원된 appsettings.json의 포트를 사용한다. 설정이 없으면 5080. }
  FirewallPort := GetConfiguredFirewallPort;
  RunHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  AddFirewallRule('UpdateAgent LabManager', 'UpdateAgent LabManager dashboard/API');
  AddFirewallRule('UpdateAgent LabManager {#AppVersion}', 'UpdateAgent LabManager (versioned rule)');
end;

{ Replace all occurrences of OldPat with NewPat without relying on StringReplace. }
function ReplaceAllText(const S, OldPat, NewPat: String): String;
var
  P, StartPos: Integer;
begin
  Result := S;
  StartPos := 1;
  while StartPos <= Length(Result) do
  begin
    P := Pos(OldPat, Copy(Result, StartPos, Length(Result) - StartPos + 1));
    if P = 0 then Break;
    P := P + StartPos - 1;
    Result := Copy(Result, 1, P - 1) + NewPat +
      Copy(Result, P + Length(OldPat), Length(Result) - P - Length(OldPat) + 1);
    StartPos := P + Length(NewPat);
  end;
end;

{ Upgrade path: the restored config may still contain change-me-* placeholders.
  Replace only those placeholders with the shipped sample tokens so the server
  starts immediately. Tokens the user already configured are left untouched. }
function ReplaceDefaultLabManagerTokens(const FileName: String): Boolean;
var
  Lines: TStringList;
  I: Integer;
  Changed: Boolean;
begin
  Result := False;
  Lines := TStringList.Create;
  try
    try
      Lines.LoadFromFile(FileName);
      Changed := False;
      for I := 0 to Lines.Count - 1 do
      begin
        if Pos('change-me-admin-token', Lines.Strings[I]) > 0 then
        begin
          Lines.Strings[I] := ReplaceAllText(Lines.Strings[I],
            'change-me-admin-token', SampleAdminToken);
          Changed := True;
        end;
        if Pos('change-me-agent-token', Lines.Strings[I]) > 0 then
        begin
          Lines.Strings[I] := ReplaceAllText(Lines.Strings[I],
            'change-me-agent-token', SampleAgentToken);
          Changed := True;
        end;
      end;
      if Changed then
        Lines.SaveToFile(FileName);
      Result := True;
    except
      Result := False;
    end;
  finally
    Lines.Free;
  end;
end;

function HasDefaultLabManagerToken: Boolean;
var
  Lines: TStringList;
  I: Integer;
begin
  Result := False;
  if LabManagerConfigFile = '' then
    LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  if not FileExists(LabManagerConfigFile) then Exit;

  Lines := TStringList.Create;
  try
    Lines.LoadFromFile(LabManagerConfigFile);
    for I := 0 to Lines.Count - 1 do
      if Pos('change-me-', Lowercase(Lines.Strings[I])) > 0 then
      begin
        Result := True;
        Exit;
      end;
  finally
    Lines.Free;
  end;
end;

function ShouldLaunchDashboard: Boolean;
begin
  Result := not HasDefaultLabManagerToken;
  if not Result then
  begin
    LaunchSkippedForDefaultTokens := True;
    Log('Skipping dashboard launch: appsettings.json still contains a default token.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { 언인스톨이 끝난 뒤 파일 교체 직전에도 남은 프로세스가 없음을 보장한다. }
    if not TerminateLabManagerApps then
      RaiseException('기존 UpdateAgent LabManager 앱을 종료하지 못해 설치를 중단합니다.');
  end
  else if CurStep = ssPostInstall then
  begin
    { InstallDelete/[Files]가 끝난 뒤에 설정을 복원해야 덮어써지지 않는다. }
    if LabManagerConfigRestoreNeeded then
    begin
      Log('Restoring LabManager appsettings.json: ' + LabManagerConfigBackup + ' -> ' + LabManagerConfigFile);
      ForceDirectories(ExtractFileDir(LabManagerConfigFile));
      { [Files] may have created appsettings.json with onlyifdoesntexist; overwrite with the backup. }
      if not CopyFile(LabManagerConfigBackup, LabManagerConfigFile, False) then
        RaiseException('기존 LabManager appsettings.json 복원에 실패해 설치를 중단합니다. 백업: ' + LabManagerConfigBackup);
      { 이전 버전에서 남은 기본 토큰은 새 샘플 토큰으로 교체해 서버가 바로 시작되도록 한다. }
      if not ReplaceDefaultLabManagerTokens(LabManagerConfigFile) then
        RaiseException('기존 appsettings.json의 기본 토큰을 샘플 토큰으로 교체하지 못해 설치를 중단합니다.');
      DeleteFile(LabManagerConfigBackup);
      LabManagerConfigRestoreNeeded := False;
    end;

    ApplyFirewallRules;
  end
  else if CurStep = ssDone then
  begin
    if LaunchSkippedForDefaultTokens and not WizardSilent then
      MsgBox(
        '기본 토큰(change-me-*)이 남아 있어 대시보드를 자동 실행하지 않았습니다.' + #13#10 +
        '설치 폴더의 appsettings.json에서 AdminToken과 AgentToken을 변경한 뒤 런처를 실행하세요.',
        mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    TerminateLabManagerApps;
    RunHidden(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  end;
end;

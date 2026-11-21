; ============================================================================
;  UpdateAgent Agent - Inno Setup 6 installer
;
;  설치 흐름
;    1) 실행 중인 기존 앱 강제 종료(서비스 중지 포함)
;    2) 라이선스 고지
;    3) 설치 직전 기존 설치 자동 감지 후 무인 제거(Inno/MSI, 설정 파일은 백업/복원)
;    4) 개별 파일(Unpacked Multi-File) 설치
;    5) 바탕화면 바로가기 선택
;    6) 서비스 등록 + 방화벽 규칙(기본 이름/버전 이름) 등록
;    7) 서비스 시작 및 상태 확인 실행
;
;  무인 설치
;    UpdateAgent.Agent-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;    선택: /LabManagerUrl=http://<teacher-ip>:5080 /AgentTokenFile=<token-file>
;    토큰은 명령줄/로그에 남지 않도록 파일로만 전달한다.
;
;  AppId는 제품 고유 값이며 절대 변경하지 않는다(업그레이드/제거 연결 기준).
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.10"
#endif
#ifndef StageRoot
  #define StageRoot "..\..\artifacts\inno\stage\agent"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\inno"
#endif
#ifndef NoticeRtf
  #define NoticeRtf "..\UpdateAgent.Agent.Installer\payload\notice.ko.rtf"
#endif

[Setup]
AppId={{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}
AppName=UpdateAgent Agent
AppVersion={#AppVersion}
AppVerName=UpdateAgent Agent {#AppVersion}
AppPublisher=AhBiYout
AppPublisherURL=https://github.com/ahbiyout-all/WinAutoUpdate
AppSupportURL=https://github.com/ahbiyout-all/WinAutoUpdate/issues
AppUpdatesURL=https://github.com/ahbiyout-all/WinAutoUpdate/releases
AppCopyright=Copyright (C) AhBiYout
VersionInfoCompany=AhBiYout
VersionInfoDescription=UpdateAgent Agent Installer
VersionInfoCopyright=Copyright (C) AhBiYout
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\UpdateAgent
DefaultGroupName=UpdateAgent
DisableProgramGroupPage=yes
DisableWelcomePage=yes
AllowNoIcons=yes
LicenseFile={#NoticeRtf}
OutputDir={#OutputDir}
OutputBaseFilename=UpdateAgent.Agent-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
Uninstallable=yes
UninstallDisplayName=UpdateAgent Agent {#AppVersion}
UninstallDisplayIcon={app}\Desktop\UpdateAgent.Agent.Desktop.exe
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
DirExistsWarning=no
UsePreviousAppDir=yes
SetupLogging=yes
SetupIconFile=..\..\assets\icons\UpdateAgent.Agent.ico
ShowLanguageDialog=no
LanguageDetectionMethod=none

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 UpdateAgent 상태 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: checkedonce

[Files]
; 설치 폴더는 서비스/세션 도우미/데스크톱 앱/CLI로 나뉜 개별 파일 구조다.
; 문서, 심볼, 압축/포터블 파일은 빌드 스크립트에서 스테이징 단계에 제거한다.
Source: "{#StageRoot}\Service\*"; DestDir: "{app}\Service"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\Service\appsettings.json"; DestDir: "{app}\Service"; Flags: onlyifdoesntexist
Source: "{#StageRoot}\SessionHelper\*"; DestDir: "{app}\SessionHelper"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\Desktop\*"; DestDir: "{app}\Desktop"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\Cli\*"; DestDir: "{app}\Cli"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; 덮어쓰기 설치에서 이전 버전의 잔여 파일을 남기지 않는다.
Type: filesandordirs; Name: "{app}\Service"
Type: filesandordirs; Name: "{app}\SessionHelper"
Type: filesandordirs; Name: "{app}\Desktop"
Type: filesandordirs; Name: "{app}\Cli"

[Icons]
Name: "{group}\UpdateAgent 상태 확인"; Filename: "{app}\Desktop\UpdateAgent.Agent.Desktop.exe"; WorkingDir: "{app}\Desktop"; IconFilename: "{app}\Desktop\UpdateAgent.Agent.Desktop.exe"
Name: "{autodesktop}\UpdateAgent 상태 확인"; Filename: "{app}\Desktop\UpdateAgent.Agent.Desktop.exe"; WorkingDir: "{app}\Desktop"; IconFilename: "{app}\Desktop\UpdateAgent.Agent.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Desktop\UpdateAgent.Agent.Desktop.exe"; WorkingDir: "{app}\Desktop"; Description: "UpdateAgent 상태 확인"; Flags: postinstall nowait skipifsilent

[Code]
var
  OldInnoUninstall: String;
  OldMsiProduct: String;
  AgentConfigFile: String;
  AgentConfigBackup: String;
  AgentConfigRestoreNeeded: Boolean;
  AgentConfigWriteFailed: Boolean;
  ServiceRecoveryDisabled: Boolean;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  ServiceName = 'UpdateAgentService';
  FirewallGroup = 'UpdateAgent';

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
  OutFile := AddBackslash(GetTempDir) + 'UpdateAgent.Agent.process.check.txt';
  DeleteFile(OutFile);
  { CSV keeps the full image name; the default table view truncates it to
    25 characters, which hides UpdateAgent.Agent.Desktop.exe and others. }
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

procedure DisableServiceRecovery;
var
  Sc: String;
begin
  { Stop the SCM from restarting the service while it is being killed. }
  Sc := ExpandConstant('{sys}\sc.exe');
  RunHidden(Sc, 'failure ' + ServiceName + ' reset= 0 actions= none');
  RunHidden(Sc, 'failureflag ' + ServiceName + ' 0');
  ServiceRecoveryDisabled := True;
end;

procedure RestoreServiceRecovery;
var
  Sc: String;
begin
  if not ServiceRecoveryDisabled then Exit;
  Sc := ExpandConstant('{sys}\sc.exe');
  RunHidden(Sc, 'failure ' + ServiceName +
    ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  RunHidden(Sc, 'failureflag ' + ServiceName + ' 1');
  ServiceRecoveryDisabled := False;
end;

function WaitForServiceStopped: Boolean;
var
  I, J: Integer;
  Sc, Cmd, StatusFile: String;
  Lines: TArrayOfString;
begin
  Result := True;
  Sc := ExpandConstant('{sys}\sc.exe');
  StatusFile := AddBackslash(GetTempDir) + 'UpdateAgent.Service.status.txt';
  for I := 0 to 10 do
  begin
    DeleteFile(StatusFile);
    Cmd := '/c ""' + Sc + '" query ' + ServiceName + ' > "' + StatusFile + '" 2>&1"';
    RunHidden(ExpandConstant('{sys}\cmd.exe'), Cmd);
    if not LoadStringsFromFile(StatusFile, Lines) then Exit;
    for J := 0 to GetArrayLength(Lines) - 1 do
    begin
      { Parse the numeric state: labels and state names are localized. }
      if Pos('1060', Lines[J]) > 0 then
      begin
        DeleteFile(StatusFile);
        Exit;
      end;
      if (Pos(': 1 ', Lines[J]) > 0) or (Pos('STOPPED', Uppercase(Lines[J])) > 0) then
      begin
        DeleteFile(StatusFile);
        Exit;
      end;
    end;
    Sleep(500);
  end;
  Result := False;
  Log('UpdateAgentService did not report STOPPED after the forced kill.');
  DeleteFile(StatusFile);
end;

{ Terminate every app that can lock files before an uninstall or an install. }
function TerminateAgentApps: Boolean;
var
  Sc: String;
begin
  DisableServiceRecovery;
  Sc := ExpandConstant('{sys}\sc.exe');

  { Stop through the SCM first, then force-close anything still running. }
  RunHidden(Sc, 'stop ' + ServiceName);
  Sleep(800);

  Result := True;
  if not KillAndWait('UpdateAgent.SessionHelper.exe') then Result := False;
  if not KillAndWait('UpdateAgent.Agent.Desktop.exe') then Result := False;
  if not KillAndWait('UpdateAgent.Cli.exe') then Result := False;
  if not KillAndWait('UpdateAgent.Service.exe') then Result := False;
  if not WaitForServiceStopped then Result := False;
end;

function ExtractGuid(const S: String): String;
var
  P, Q: Integer;
begin
  Result := '';
  P := Pos('{', S);
  if P = 0 then Exit;
  Q := Pos('}', S);
  if Q <= P then Exit;
  Result := Copy(S, P, Q - P + 1);
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
  if RegQueryStringValue(HKLM64, UninstallKey + '\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1', 'UninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKLM32, UninstallKey + '\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1', 'UninstallString', Value) then
    Result := Value;
end;

function WaitForInnoUninstallComplete: Boolean;
var
  I: Integer;
  KeyExists: Boolean;
begin
  { Inno 제거 프로그램은 종료 코드를 반환한 뒤 임시 복제본에서 계속 파일을 지울 수 있다. }
  Result := True;
  for I := 0 to 40 do
  begin
    KeyExists :=
      RegKeyExists(HKLM64, UninstallKey + '\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1') or
      RegKeyExists(HKLM32, UninstallKey + '\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1');
    if not KeyExists then Exit;
    Sleep(250);
  end;
  Result := not KeyExists;
end;

function FindMsiProductCodeIn(const RootKey: Integer): String;
var
  Names: TArrayOfString;
  I: Integer;
  DisplayName, UninstallString: String;
  IsMsi: Cardinal;
begin
  Result := '';
  if not RegGetSubkeyNames(RootKey, UninstallKey, Names) then Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if RegQueryStringValue(RootKey, UninstallKey + '\' + Names[I], 'DisplayName', DisplayName) and
       (Pos('UpdateAgent Agent', DisplayName) = 1) and
       RegQueryDWordValue(RootKey, UninstallKey + '\' + Names[I], 'WindowsInstaller', IsMsi) and
       (IsMsi = 1) then
    begin
      if RegQueryStringValue(RootKey, UninstallKey + '\' + Names[I], 'UninstallString', UninstallString) then
      begin
        Result := ExtractGuid(UninstallString);
        if Result <> '' then Exit;
      end;
    end;
  end;
end;

function FindMsiProductCode: String;
begin
  Result := FindMsiProductCodeIn(HKLM64);
  if Result = '' then
    Result := FindMsiProductCodeIn(HKLM32);
end;

function CmdParamExists(const Prefix: String): Boolean;
var
  I: Integer;
  UpperPrefix, Param: String;
begin
  Result := False;
  UpperPrefix := Uppercase(Prefix);
  for I := 1 to ParamCount do
  begin
    Param := Uppercase(RemoveQuotes(ParamStr(I)));
    if Pos(UpperPrefix, Param) = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function CmdParamValue(const Prefix: String): String;
var
  I: Integer;
  Param, UpperPrefix: String;
begin
  Result := '';
  UpperPrefix := Uppercase(Prefix);
  for I := 1 to ParamCount do
  begin
    Param := RemoveQuotes(ParamStr(I));
    if Pos(UpperPrefix, Uppercase(Param)) = 1 then
    begin
      Result := Copy(Param, Length(Prefix) + 1, Length(Param));
      Exit;
    end;
  end;
end;

function JsonEscape(const Value: String): String;
var
  I: Integer;
  Ch: Char;
begin
  Result := '';
  for I := 1 to Length(Value) do
  begin
    Ch := Value[I];
    if Ch = '\' then
      Result := Result + '\\'
    else if Ch = '"' then
      Result := Result + '\"'
    else if Ch = #9 then
      Result := Result + '\t'
    else if Ch = #10 then
      Result := Result + '\n'
    else if Ch = #13 then
      Result := Result + '\r'
    else
      Result := Result + Ch;
  end;
end;

function InitializeSetup: Boolean;
begin
  { 1) 종료 → 언인스톨 → 설치 순서를 위해 실행 중인 앱을 먼저 정리한다. }
  TerminateAgentApps;

  { 2) 기존 설치는 선택 없이 PrepareToInstall에서 항상 제거한다. }
  OldInnoUninstall := FindInnoUninstallString;
  OldMsiProduct := FindMsiProductCode;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe: String;
begin
  Result := '';

  { 언인스톨보다 먼저 모든 실행 중인 앱을 종료한다. 종료 실패 시 설치를 중단한다. }
  if not TerminateAgentApps then
  begin
    Result := '기존 UpdateAgent 앱을 종료하지 못해 설치를 중단합니다. ' +
      '실행 중인 UpdateAgent 창/서비스를 닫은 뒤 다시 실행해 주세요.';
    Exit;
  end;

  { 이전 제거 프로그램/InstallDelete가 appsettings.json을 지우므로 먼저 백업한다. }
  AgentConfigFile := ExpandConstant('{app}\Service\appsettings.json');
  AgentConfigBackup := AddBackslash(GetTempDir) + 'UpdateAgent.Agent.appsettings.json.bak';
  AgentConfigRestoreNeeded := False;
  DeleteFile(AgentConfigBackup);
  if FileExists(AgentConfigFile) then
  begin
    Log('Backing up Agent appsettings.json: ' + AgentConfigFile + ' -> ' + AgentConfigBackup);
    if not CopyFile(AgentConfigFile, AgentConfigBackup, False) then
    begin
      Result := '기존 Agent appsettings.json 백업에 실패해 설치를 중단합니다: ' + AgentConfigBackup;
      Exit;
    end;
    AgentConfigRestoreNeeded := True;
  end;

  { MSI로 설치된 제품이 있으면 Inno 설치와 공존할 수 없으므로 항상 제거한다. }
  if OldMsiProduct <> '' then
  begin
    Code := -1;
    if not Exec(ExpandConstant('{sys}\msiexec.exe'),
        '/x ' + OldMsiProduct + ' /qn /norestart', '',
        SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      Result := '기존 WiX MSI UpdateAgent Agent 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.';
      Exit;
    end;

    { 0/3010/1641은 모두 성공이다. 3010/1641은 재부팅이 필요함을 의미한다. }
    if not (Code in [0, 3010, 1641]) then
    begin
      Result := '기존 WiX MSI UpdateAgent Agent 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.';
      Exit;
    end;
    if Code in [3010, 1641] then
    begin
      NeedsRestart := True;
      Result := '기존 WiX MSI UpdateAgent Agent 제거에 재부팅이 필요합니다. ' +
        'PC를 재부팅한 뒤 설치를 다시 실행해 주세요.';
      Exit;
    end;
    Sleep(1200);
  end;

  if OldInnoUninstall <> '' then
  begin
    UninstallExe := ExtractExePath(OldInnoUninstall);
    if (UninstallExe = '') or not FileExists(UninstallExe) then
    begin
      Result := '기존 UpdateAgent Agent 제거 프로그램을 찾을 수 없어 설치를 중단합니다.';
      Exit;
    end;

    Code := -1;
    if not Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      Result := '기존 UpdateAgent Agent 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.';
      Exit;
    end;

    if not WaitForInnoUninstallComplete then
    begin
      Result := '기존 UpdateAgent Agent 제거가 완료되지 않아 설치를 중단합니다.';
      Exit;
    end;
  end;

  { 설정 복원은 InstallDelete/[Files]가 끝난 뒤 ssPostInstall에서 수행한다. }
end;

function EnsureService: Boolean;
var
  ServiceExe: String;
  Sc: String;
  Code: Integer;
begin
  Result := False;
  ServiceExe := ExpandConstant('{app}\Service\UpdateAgent.Service.exe');
  Sc := ExpandConstant('{sys}\sc.exe');

  if RunHidden(Sc, 'query ' + ServiceName) <> 0 then
    Code := RunHidden(Sc, 'create ' + ServiceName + ' binPath= "' + ServiceExe +
      '" start= auto DisplayName= "UpdateAgent Service"')
  else
    Code := RunHidden(Sc, 'config ' + ServiceName + ' binPath= "' + ServiceExe + '" start= auto');

  if Code <> 0 then
  begin
    Log('Failed to create/configure UpdateAgentService (code ' + IntToStr(Code) + ')');
    Exit;
  end;

  RunHidden(Sc, 'description ' + ServiceName + ' "Windows Update automation agent for computer labs"');
  { Restore the SCM failure recovery disabled before the forced kill. }
  RunHidden(Sc, 'failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  RunHidden(Sc, 'failureflag ' + ServiceName + ' 1');
  ServiceRecoveryDisabled := False;
  Result := True;
end;

procedure AddFirewallRule(const RuleName, ProgramPath, Description: String);
var
  Netsh: String;
begin
  Netsh := ExpandConstant('{sys}\netsh.exe');
  RunHidden(Netsh, 'advfirewall firewall add rule name="' + RuleName +
    '" dir=in action=allow program="' + ProgramPath +
    '" enable=yes profile=any group="' + FirewallGroup +
    '" description="' + Description + '"');
end;

procedure ApplyFirewallRules;
var
  ServiceExe: String;
  Netsh: String;
begin
  ServiceExe := ExpandConstant('{app}\Service\UpdateAgent.Service.exe');
  Netsh := ExpandConstant('{sys}\netsh.exe');

  { 이전 버전이 만든 버전별 규칙까지 그룹 단위로 정리한 뒤 다시 등록한다. }
  RunHidden(Netsh, 'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  AddFirewallRule('UpdateAgent Agent', ServiceExe, 'UpdateAgent Service (all networks)');
  AddFirewallRule('UpdateAgent Agent {#AppVersion}', ServiceExe, 'UpdateAgent Service (versioned rule)');
end;

procedure WriteAgentConfigIfRequested;
var
  Url, TokenFile, Token, ConfigPath, SessionHelper: String;
  TokenLines: TArrayOfString;
  Lines: TArrayOfString;
  UrlRequested, TokenFileRequested: Boolean;
begin
  AgentConfigWriteFailed := False;
  UrlRequested := CmdParamExists('/LabManagerUrl=');
  TokenFileRequested := CmdParamExists('/AgentTokenFile=');
  if (not UrlRequested) and (not TokenFileRequested) then Exit;

  Url := Trim(CmdParamValue('/LabManagerUrl='));
  TokenFile := Trim(CmdParamValue('/AgentTokenFile='));
  if (not UrlRequested) or (not TokenFileRequested) or (Url = '') or (TokenFile = '') then
  begin
    AgentConfigWriteFailed := True;
    if not WizardSilent then
      MsgBox('/LabManagerUrl과 /AgentTokenFile은 함께 지정해야 합니다. 설정 파일을 쓰지 않습니다.',
        mbError, MB_OK);
    Exit;
  end;

  { The token is read from a file only; it is never accepted on the command line. }
  if (not LoadStringsFromFile(TokenFile, TokenLines)) or (GetArrayLength(TokenLines) = 0) then
  begin
    AgentConfigWriteFailed := True;
    if not WizardSilent then
      MsgBox('에이전트 토큰 파일을 읽을 수 없습니다: ' + TokenFile, mbError, MB_OK);
    Exit;
  end;

  Token := Trim(TokenLines[0]);
  if (Length(Token) > 0) and (Token[1] = #65279) then
    Delete(Token, 1, 1);
  Token := Trim(Token);
  if Token = '' then
  begin
    AgentConfigWriteFailed := True;
    if not WizardSilent then
      MsgBox('에이전트 토큰 파일이 비어 있습니다: ' + TokenFile, mbError, MB_OK);
    Exit;
  end;

  ConfigPath := ExpandConstant('{commonappdata}\UpdateAgent\agent.json');
  SessionHelper := ExpandConstant('{app}\SessionHelper\UpdateAgent.SessionHelper.exe');
  ForceDirectories(ExtractFileDir(ConfigPath));

  SetArrayLength(Lines, 6);
  Lines[0] := '{';
  Lines[1] := '  "Agent": {';
  Lines[2] := '    "LabManagerUrl": "' + JsonEscape(Url) + '",';
  Lines[3] := '    "AgentToken": "' + JsonEscape(Token) + '",';
  Lines[4] := '    "SessionHelperPath": "' + JsonEscape(SessionHelper) + '"';
  Lines[5] := '  }' + #13#10 + '}';

  { Inno Pascal cannot pre-create a file with a restricted DACL before writing it,
    so agent.json is briefly created with inherited (world-readable) permissions.
    The ACL is tightened immediately after the write; this small window is accepted. }
  if not SaveStringsToUTF8File(ConfigPath, Lines, False) then
  begin
    AgentConfigWriteFailed := True;
    if not WizardSilent then
      MsgBox('에이전트 설정 파일을 쓸 수 없습니다: ' + ConfigPath, mbError, MB_OK);
    Exit;
  end;
  RunHidden(ExpandConstant('{sys}\icacls.exe'),
    '"' + ConfigPath + '" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)"');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssInstall then
  begin
    { 언인스톨이 끝난 뒤 파일 교체 직전에도 남은 프로세스가 없음을 보장한다. }
    if not TerminateAgentApps then
      RaiseException('기존 UpdateAgent 앱을 종료하지 못해 설치를 중단합니다.');
  end
  else if CurStep = ssPostInstall then
  begin
    { InstallDelete와 [Files]가 모두 끝난 뒤에 설정을 복원해야 덮어써지지 않는다. }
    if AgentConfigRestoreNeeded then
    begin
      Log('Restoring Agent appsettings.json: ' + AgentConfigBackup + ' -> ' + AgentConfigFile);
      ForceDirectories(ExtractFileDir(AgentConfigFile));
      { [Files] may have created appsettings.json with onlyifdoesntexist; overwrite with the backup. }
      if not CopyFile(AgentConfigBackup, AgentConfigFile, False) then
        RaiseException('기존 Agent appsettings.json 복원에 실패해 설치를 중단합니다. 백업: ' + AgentConfigBackup);
      DeleteFile(AgentConfigBackup);
      AgentConfigRestoreNeeded := False;
    end;

    if not EnsureService then
      RaiseException('UpdateAgentService 등록/설정에 실패해 설치를 중단합니다.');

    ApplyFirewallRules;
    WriteAgentConfigIfRequested;
    if AgentConfigWriteFailed then
      RaiseException('에이전트 설정 파일을 기록하지 못해 설치를 중단합니다. ' +
        '명령줄 인자(/LabManagerUrl, /AgentTokenFile)와 토큰 파일을 확인하세요.');

    Code := RunHidden(ExpandConstant('{sys}\sc.exe'), 'start ' + ServiceName);
    if (Code <> 0) and (Code <> 1056) then
      RaiseException('UpdateAgentService 시작에 실패했습니다(sc ' + IntToStr(Code) + ').');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { 제거 시작 전에도 같은 종료 절차를 사용한다. }
    TerminateAgentApps;
    RunHidden(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  end;

  if CurUninstallStep = usPostUninstall then
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete ' + ServiceName);
end;

procedure DeinitializeSetup;
begin
  { Restore SCM recovery if the install aborted before ssPostInstall. }
  RestoreServiceRecovery;
end;

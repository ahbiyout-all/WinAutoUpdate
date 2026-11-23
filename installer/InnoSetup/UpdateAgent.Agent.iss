; ============================================================================
;  UpdateAgent Agent - Inno Setup 6 installer
;
;  설치 흐름
;    0) 일반 설치: 각 단계 시작 전 안내 화면을 표시하고 몇 초 후 자동 진행
;    1) 실행 중인 기존 앱 강제 종료(서비스 중지 포함)
;    2) 라이선스 고지
;    3) 설치 직전 기존 설치 자동 감지 후 무인 제거(Inno/MSI, 설정 파일은 백업/복원)
;    4) 개별 파일(Unpacked Multi-File) 설치
;    5) 바탕화면 바로가기 선택
;    6) 서비스 등록 + 방화벽 규칙(기본 이름/버전 이름) 등록
;    7) 서비스 시작 및 상태 확인 실행
;
;  무인 설치(/VERYSILENT)에서는 안내 화면과 대기 시간을 생략한다.
;
;  무인 설치
;    UpdateAgent.Agent-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;    선택: /LabManagerUrl=http://<teacher-ip>:5080 /AgentTokenFile=<token-file>
;    또는 설치 exe와 같은 폴더의 agent-install.ini([Agent] 섹션)를 자동으로 읽는다.
;      [Agent]
;      LabManagerUrl=http://labmanager:5080
;      AgentTokenFile=agent-token.txt
;    설정 파일의 상대 경로는 설치 exe가 있는 폴더 기준이다. 명령줄 값이 우선한다.
;    토큰 값은 명령줄/설정 파일에 쓰지 않고 별도 파일로만 전달한다.
;
;  AppId는 제품 고유 값이며 절대 변경하지 않는다(업그레이드/제거 연결 기준).
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.31"
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
#ifndef NoticeDelaySeconds
  #define NoticeDelaySeconds 5
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
; 첫 설치에서만 읽는 내장 토큰. 설치 폴더에는 복사하지 않는다.
Source: "{#StageRoot}\agent-token.txt"; Flags: dontcopy

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
  FirstInstall: Boolean;
  AgentConfigFile: String;
  AgentConfigBackup: String;
  AgentConfigRestoreNeeded: Boolean;
  AgentConfigWriteFailed: Boolean;
  AgentConfigError: String;
  AgentConfigRequested: Boolean;
  AgentConfigSkipWrite: Boolean;
  AgentConfigUrl: String;
  AgentConfigToken: String;
  RunHiddenStarted: Boolean;
  ServiceRecoveryDisabled: Boolean;
  StepNoticePage: TOutputProgressWizardPage;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  ServiceName = 'UpdateAgentService';
  FirewallGroup = 'UpdateAgent';
  NoticeSeconds = {#NoticeDelaySeconds};
  InstallConfigFileName = 'agent-install.ini';
  InstallConfigSection = 'Agent';
  BundledTokenFileName = 'agent-token.txt';
  DefaultLabManagerUrl = 'http://labmanager:5080';

procedure ValidateAgentConfigRequest; forward;

procedure InitializeWizard;
begin
  StepNoticePage := CreateOutputProgressPage('UpdateAgent 설치', '단계 안내');
end;

function BoolText(const Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  { Exec가 실패하면 Code는 0으로 남으므로 별도로 성공 여부를 기록한다. }
  RunHiddenStarted := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not RunHiddenStarted then Code := -1;
  Result := Code;
end;

{ Non-modal notice with a countdown. The caller's next statement is the step
  that the notice describes, so the wizard advances automatically when the
  countdown ends. WizardSilent keeps unattended installs delay-free. }
procedure ShowStepNotice(const ACaption, AMessage, ASubMessage: String);
var
  I, Remaining: Integer;
begin
  if WizardSilent then Exit;
  if StepNoticePage = nil then Exit;

  StepNoticePage.Caption := ACaption;
  StepNoticePage.Description := '잠시 후 다음 단계로 자동 진행합니다.';
  StepNoticePage.SetText(AMessage, ASubMessage + ' (' + IntToStr(NoticeSeconds) + '초 후 자동 진행)');
  StepNoticePage.SetProgress(0, NoticeSeconds);
  StepNoticePage.Show;
  try
    for I := 1 to NoticeSeconds do
    begin
      Sleep(1000);
      Remaining := NoticeSeconds - I;
      StepNoticePage.SetProgress(I, NoticeSeconds);
      if Remaining > 0 then
        StepNoticePage.SetText(AMessage, ASubMessage + ' (' + IntToStr(Remaining) + '초 후 자동 진행)')
      else
        StepNoticePage.SetText(AMessage, '이제 진행합니다...');
      StepNoticePage.Msg1Label.Refresh;
      StepNoticePage.Msg2Label.Refresh;
    end;
  finally
    StepNoticePage.Hide;
  end;
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

{ INI 값의 뒤에 붙은 ; 주석은 값에서 제거한다. 명령줄 값은 그대로 사용한다. }
function StripIniComment(const Value: String): String;
var
  P: Integer;
begin
  P := Pos(';', Value);
  if P > 0 then
    Result := Trim(Copy(Value, 1, P - 1))
  else
    Result := Trim(Value);
end;

{ 명령줄 값이 우선하고, 없으면 설치 exe와 같은 폴더의 INI에서 읽는다. }
function InstallSetting(const IniPath, Key, CmdPrefix: String): String;
begin
  if CmdParamExists(CmdPrefix) then
  begin
    Result := Trim(CmdParamValue(CmdPrefix));
    Exit;
  end;

  Result := '';
  if FileExists(IniPath) then
    Result := StripIniComment(GetIniString(InstallConfigSection, Key, '', IniPath));
end;

{ 상대 경로는 설치 exe가 있는 폴더 기준으로 해석한다. }
function ResolveInstallTokenFile(const Value: String): String;
var
  TokenFile: String;
begin
  TokenFile := Trim(RemoveQuotes(Value));
  if Copy(TokenFile, 1, 2) = '.\' then
    TokenFile := Copy(TokenFile, 3, Length(TokenFile) - 2);

  { //server/share 도 UNC로 취급한다. }
  if Copy(TokenFile, 1, 2) = '//' then
    TokenFile := '\\' + Copy(TokenFile, 3, Length(TokenFile) - 2);

  if (Pos(':', TokenFile) = 2) or (Copy(TokenFile, 1, 2) = '\\') then
    Result := TokenFile
  else
    Result := ExpandConstant('{src}\' + TokenFile);
end;

// 설정 파일 경로도 소스 폴더 상수를 사용해야 한다. ParamStr(0)은 상대 경로/경로 없는 실행에서
// 빈 문자열이 될 수 있어 현재 작업 폴더를 잘못 읽는다.
function ResolveInstallConfigFile: String;
begin
  Result := ExpandConstant('{src}\' + InstallConfigFileName);
end;

function NormalizeInstallUrl(const Value: String): String;
begin
  Result := Trim(Value);
  while (Length(Result) > 0) and (Result[Length(Result)] = '/') do
    Delete(Result, Length(Result), 1);
end;

function IsValidInstallUrl(const Value: String): Boolean;
var
  UpperValue: String;
begin
  UpperValue := Uppercase(Value);
  Result := (Copy(UpperValue, 1, 7) = 'HTTP://') or
    (Copy(UpperValue, 1, 8) = 'HTTPS://');
end;

function IsPlaceholderToken(const Value: String): Boolean;
var
  UpperToken: String;
begin
  UpperToken := Uppercase(Trim(Value));
  Result := Copy(UpperToken, 1, 10) = 'CHANGE-ME-';
  if (not Result) and (Length(UpperToken) >= 21) then
    Result := (Copy(UpperToken, 1, 7) = 'SAMPLE-') and
      (Copy(UpperToken, Length(UpperToken) - 20, 21) = '-REPLACE-BEFORE-PILOT');
end;

{ JSON 텍스트에서 문자열 값 하나를 읽는다(이스케이프된 따옴표 처리). }
function GetJsonStringValue(const Text, Key: String): String;
var
  KeyPos, I, StartPos, EndPos: Integer;
begin
  Result := '';
  KeyPos := Pos('"' + Key + '"', Text);
  if KeyPos = 0 then Exit;
  I := KeyPos + Length(Key) + 2;
  while (I <= Length(Text)) and (Text[I] <> ':') do Inc(I);
  if I > Length(Text) then Exit;
  Inc(I);
  while (I <= Length(Text)) and (Text[I] <= ' ') do Inc(I);
  if (I > Length(Text)) or (Text[I] <> '"') then Exit;
  Inc(I);
  StartPos := I;
  while (I <= Length(Text)) and (Text[I] <> '"') do
  begin
    if (Text[I] = '\') and (I < Length(Text)) then Inc(I);
    Inc(I);
  end;
  if I > Length(Text) then Exit;
  EndPos := I - 1;
  Result := Copy(Text, StartPos, EndPos - StartPos + 1);
end;

{ 업그레이드에서 기존 agent.json을 유지하기 전에 형태와 토큰을 확인한다.
  공개된 기본/샘플 토큰이 들어 있는 설정은 재사용하지 않는다. }
function IsValidExistingAgentConfig(const Path: String): Boolean;
var
  Lines: TArrayOfString;
  Text: String;
  I: Integer;
begin
  Result := False;
  if not FileExists(Path) then Exit;
  if not LoadStringsFromFile(Path, Lines) then Exit;

  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Text := Text + Lines[I];
  Text := Trim(Text);
  if Length(Text) < 2 then Exit;

  Result := (Text[1] = '{') and (Text[Length(Text)] = '}') and
    (Pos('"Agent"', Text) > 0) and
    (Pos('"LabManagerUrl"', Text) > 0) and
    (Pos('"AgentToken"', Text) > 0) and
    (Length(Trim(GetJsonStringValue(Text, 'LabManagerUrl'))) > 0) and
    (Length(Trim(GetJsonStringValue(Text, 'AgentToken'))) > 0) and
    (not IsPlaceholderToken(GetJsonStringValue(Text, 'AgentToken')));
end;

{ 업그레이드에서 기존 토큰을 유지할 때 사용한다. 내장 토큰은 읽지 않는다. }
function ReadExistingAgentToken(const Path: String): String;
var
  Lines: TArrayOfString;
  Text: String;
  I: Integer;
begin
  Result := '';
  if not FileExists(Path) then Exit;
  if not LoadStringsFromFile(Path, Lines) then Exit;
  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if (I = 0) and (Length(Lines[0]) > 0) and (Lines[0][1] = #65279) then
      Text := Copy(Lines[0], 2, Length(Lines[0]) - 1)
    else
      Text := Text + Lines[I];
  end;
  Result := GetJsonStringValue(Text, 'AgentToken');
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
var
  ConfigPath: String;
begin
  { 종료 → 언인스톨 → 설치 순서는 PrepareToInstall에서 안내와 함께 수행한다. }
  OldInnoUninstall := FindInnoUninstallString;
  OldMsiProduct := FindMsiProductCode;
  { 레지스트리 잔존 여부가 아니라 재사용 가능한 설정 파일이 있는지로 첫 설치를 판단한다.
    레지스트리가 남았지만 설정이 없거나 기본/샘플 토큰이면 첫 설치로 취급해 토큰을 다시 요청한다. }
  ConfigPath := ExpandConstant('{commonappdata}\UpdateAgent\agent.json');
  FirstInstall := not IsValidExistingAgentConfig(ConfigPath);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe: String;
begin
  Result := '';

  { 어떤 것도 지우기 전에 설정/토큰을 검증한다. 실패하면 PC를 변경하지 않는다. }
  ValidateAgentConfigRequest;
  if AgentConfigWriteFailed then
  begin
    Result := AgentConfigError;
    Exit;
  end;

  { 언인스톨보다 먼저 모든 실행 중인 앱을 종료한다. 종료 실패 시 설치를 중단한다. }
  ShowStepNotice('기존 앱 종료',
    '실행 중인 UpdateAgent 앱을 종료합니다.',
    '창과 서비스를 정리한 뒤 언인스톨을 준비합니다.');
  if not TerminateAgentApps then
  begin
    Result := '기존 UpdateAgent 앱을 종료하지 못해 설치를 중단합니다. ' +
      '실행 중인 UpdateAgent 창/서비스를 닫은 뒤 다시 실행해 주세요.';
    Exit;
  end;

  ShowStepNotice('기존 설치 제거',
    '기존 UpdateAgent 설치가 있으면 제거합니다.',
    'appsettings.json은 백업된 뒤 새 설치에 복원됩니다.');

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

    { 1605는 이미 설치되지 않은 제품(레지스트리만 남은 경우)이므로 무시하고 계속한다. }
    if Code = 1605 then
      Log('Previous Agent MSI product is not installed; continuing installation: ' + OldMsiProduct)
    else
    begin
      { 0/3010/1641은 모두 성공이다. 3010/1641은 재부팅이 필요함을 의미한다. }
      if not (Code in [0, 3010, 1641]) then
      begin
        Result := '기존 WiX MSI UpdateAgent Agent 제거에 실패했습니다. (code ' +
          IntToStr(Code) + ') 설치를 중단합니다.';
        Exit;
      end;
      if Code in [3010, 1641] then
      begin
        { 실제 실패가 아니라 재부팅이 필요한 성공이다. 설치를 계속하고 마지막에 재부팅을 요청한다. }
        NeedsRestart := True;
        Log('Previous Agent MSI uninstall requested a restart; continuing installation.');
      end;
      Sleep(1200);
    end;
  end;

  if OldInnoUninstall <> '' then
  begin
    UninstallExe := ExtractExePath(OldInnoUninstall);
    if (UninstallExe = '') or not FileExists(UninstallExe) then
    begin
      { 레지스트리만 남고 제거 프로그램이 없으면 이미 제거된 것으로 보고 계속 진행한다. }
      Log('Previous Agent uninstaller not found; continuing installation: ' + OldInnoUninstall);
    end
    else
    begin
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

function PsLiteral(const Value: String): String;
begin
  Result := '''' + ReplaceAllText(Value, '''', '''''') + '''';
end;

{ netsh advfirewall firewall add/delete rule은 group= 인자를 지원하지 않는다.
  그룹 규칙을 실제로 만들고 정리하려면 PowerShell NetSecurity cmdlet을 사용해야 한다. }
function ApplyFirewallRules: Boolean;
var
  ScriptPath, PowerShellExe: String;
  Lines: TArrayOfString;
  Code: Integer;
begin
  ScriptPath := ExpandConstant('{tmp}\UpdateAgentFirewall.ps1');
  { 학생 PC는 교사 PC로 폴링만 한다. 수신 대기 구성 요소가 없으므로 인바운드 규칙을
    만들지 않고, 이전 버전이 만든 규칙만 그룹 단위로 정리한다. }
  SetArrayLength(Lines, 2);
  Lines[0] := '$ErrorActionPreference = ''Stop''';
  Lines[1] := 'Get-NetFirewallRule -Group ' + PsLiteral(FirewallGroup) +
    ' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop';

  if not SaveStringsToUTF8File(ScriptPath, Lines, True) then
  begin
    Log('Could not create the firewall setup script: ' + ScriptPath);
    Result := False;
    Exit;
  end;

  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Code := RunHidden(PowerShellExe,
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"');
  Result := RunHiddenStarted and (Code = 0);
  if not Result then
    Log('Failed to clean up legacy UpdateAgent firewall rules (started=' +
      BoolText(RunHiddenStarted) + ', code ' + IntToStr(Code) + ')');
end;

procedure RemoveFirewallRules;
var
  ScriptPath, PowerShellExe: String;
  Lines: TArrayOfString;
begin
  ScriptPath := ExpandConstant('{tmp}\UpdateAgentFirewallRemove.ps1');
  SetArrayLength(Lines, 2);
  Lines[0] := '$ErrorActionPreference = ''SilentlyContinue''';
  Lines[1] := 'Get-NetFirewallRule -Group ' + PsLiteral(FirewallGroup) +
    ' | Remove-NetFirewallRule -ErrorAction SilentlyContinue';
  if not SaveStringsToUTF8File(ScriptPath, Lines, True) then Exit;
  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  RunHidden(PowerShellExe,
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"');
end;

{ 기존 설치를 제거하기 전에 모든 입력을 검증한다. 여기서 실패하면 PC를 변경하지 않는다.
  내장 토큰은 첫 설치 전용이며, 업데이트에서는 명시적 외부 토큰 파일만 사용한다. }
procedure ValidateAgentConfigRequest;
var
  IniPath, ExistingConfigPath, Url, TokenFile, Token, BundledTokenPath: String;
  TokenLines: TArrayOfString;
  IniLines: TArrayOfString;
  SettingsFromCmd, HasExternalToken, CanKeepExisting: Boolean;
begin
  AgentConfigWriteFailed := False;
  AgentConfigRequested := False;
  AgentConfigSkipWrite := False;
  AgentConfigError := '';
  AgentConfigUrl := '';
  AgentConfigToken := '';

  IniPath := ResolveInstallConfigFile;
  if FileExists(IniPath) then
    Log('Loading agent install settings from: ' + IniPath);

  { 두 키 중 하나라도 명령줄로 왔으면 재설정 의사로 본다. 부분 overriding도 같다. }
  SettingsFromCmd := CmdParamExists('/LabManagerUrl=') or CmdParamExists('/AgentTokenFile=');
  Url := InstallSetting(IniPath, 'LabManagerUrl', '/LabManagerUrl=');
  TokenFile := InstallSetting(IniPath, 'AgentTokenFile', '/AgentTokenFile=');
  HasExternalToken := TokenFile <> '';

  { 설정 파일도 명령줄도 없이 첫 설치하면 교사 PC DNS 별칭을 기본값으로 쓴다.
    외부 INI/명령줄은 항상 우선하며, BOM이 깨진 INI가 있으면 기본값으로 덮지 않고 오류를 유지한다. }
  if (Url = '') and (not HasExternalToken) and (not SettingsFromCmd) and
     (not FileExists(IniPath)) and FirstInstall then
  begin
    Url := DefaultLabManagerUrl;
    Log('Using default first-install LabManagerUrl (labmanager DNS alias).');
  end;

  ExistingConfigPath := ExpandConstant('{commonappdata}\UpdateAgent\agent.json');
  CanKeepExisting := (not FirstInstall) and IsValidExistingAgentConfig(ExistingConfigPath);

  if (Url = '') and (not HasExternalToken) then
  begin
    { BOM이 있는 INI는 Windows API가 섹션을 찾지 못해 조용히 빈 값이 된다. }
    { 명령줄 키가 있어도 BOM이 깨진 INI는 조용히 무시하지 않고 중단한다. }
    if FileExists(IniPath) and
       LoadStringsFromFile(IniPath, IniLines) and (GetArrayLength(IniLines) > 0) then
    begin
      AgentConfigWriteFailed := True;
      AgentConfigError := InstallConfigFileName + '에 설정 값을 읽을 수 없습니다. ' +
        'UTF-8 BOM 없이 저장하고 [Agent] 섹션을 확인하세요.';
      Exit;
    end;

    if CanKeepExisting then
    begin
      Log('No install settings after a previous install; refreshing SessionHelperPath only.');
      AgentConfigRequested := True;
      AgentConfigSkipWrite := True;
      Exit;
    end;

    AgentConfigWriteFailed := True;
    if FirstInstall then
      AgentConfigError := 'LabManagerUrl이 없습니다. ' + InstallConfigFileName + ' 또는 ' +
        '/LabManagerUrl=http://labmanager:5080 을 지정하세요.'
    else
      AgentConfigError := '기존 설치가 감지되었지만 유효한 agent.json과 설치 설정이 없습니다. ' +
        InstallConfigFileName + ' 또는 명령줄 인자를 확인하세요.';
    Exit;
  end;

  if Url = '' then
  begin
    AgentConfigWriteFailed := True;
    AgentConfigError := 'LabManagerUrl이 없습니다. ' + InstallConfigFileName + ' 또는 ' +
      '/LabManagerUrl=http://labmanager:5080 을 지정하세요.';
    Exit;
  end;

  Url := NormalizeInstallUrl(Url);
  if not IsValidInstallUrl(Url) then
  begin
    AgentConfigWriteFailed := True;
    AgentConfigError := 'LabManagerUrl은 http:// 또는 https:// 로 시작해야 합니다: ' + Url;
    Exit;
  end;

  { 업데이트에서는 외부 토큰 파일이 명시된 경우에만 설정을 갱신한다.
    내장 토큰은 첫 설치에서만 사용하고, 업데이트에서는 절대 읽지 않는다. }
  if (not HasExternalToken) and CanKeepExisting then
  begin
    { 업그레이드에서는 내장 토큰을 절대 사용하지 않고 기존 토큰을 유지한다.
      INI/명령줄의 URL은 키별 override 규칙에 따라 반드시 반영한다. }
    AgentConfigToken := ReadExistingAgentToken(ExistingConfigPath);
    AgentConfigUrl := Url;
    AgentConfigRequested := True;
    AgentConfigSkipWrite := False;
    Log('Upgrade without external token: applying requested URL and preserving the existing token.');
    Exit;
  end;

  if not HasExternalToken then
  begin
    if not FirstInstall then
    begin
      AgentConfigWriteFailed := True;
      AgentConfigError := '기존 설치가 감지되었지만 agent.json이 유효하지 않습니다. ' +
        '명령줄 /AgentTokenFile 또는 ' + InstallConfigFileName + '에 토큰 파일을 지정하세요.';
      Exit;
    end;

    BundledTokenPath := ExpandConstant('{tmp}\' + BundledTokenFileName);
    if not FileExists(BundledTokenPath) then
      ExtractTemporaryFile(BundledTokenFileName);
    if not FileExists(BundledTokenPath) then
    begin
      AgentConfigWriteFailed := True;
      AgentConfigError := '첫 설치용 내장 에이전트 토큰 파일을 찾을 수 없습니다. ' +
        InstallConfigFileName + '에 AgentTokenFile을 지정하세요.';
      Exit;
    end;
    TokenFile := BundledTokenPath;
    Log('Using embedded first-install agent token (value hidden).');
  end
  else
    TokenFile := ResolveInstallTokenFile(TokenFile);

  Log('Agent token file: ' + TokenFile);

  if not FileExists(TokenFile) then
  begin
    AgentConfigWriteFailed := True;
    if HasExternalToken then
      AgentConfigError := '에이전트 토큰 파일을 읽을 수 없습니다: ' + TokenFile
    else
      AgentConfigError := '첫 설치용 내장 에이전트 토큰 파일을 읽을 수 없습니다.';
    Exit;
  end;

  { The token is read from a file only; it is never accepted on the command line. }
  if (not LoadStringsFromFile(TokenFile, TokenLines)) or (GetArrayLength(TokenLines) = 0) then
  begin
    AgentConfigWriteFailed := True;
    if HasExternalToken then
      AgentConfigError := '에이전트 토큰 파일이 비어 있습니다: ' + TokenFile
    else
      AgentConfigError := '첫 설치용 내장 에이전트 토큰이 비어 있습니다. ' +
        'UPDATEAGENT_AGENT_TOKEN secret을 설정해 다시 빌드하거나 ' +
        InstallConfigFileName + '에 AgentTokenFile을 지정하세요.';
    Exit;
  end;

  Token := Trim(TokenLines[0]);
  if (Length(Token) > 0) and (Token[1] = #65279) then
    Delete(Token, 1, 1);
  Token := Trim(Token);
  if Token = '' then
  begin
    AgentConfigWriteFailed := True;
    if HasExternalToken then
      AgentConfigError := '에이전트 토큰 파일이 비어 있습니다: ' + TokenFile
    else
      AgentConfigError := '첫 설치용 내장 에이전트 토큰이 비어 있습니다. ' +
        'UPDATEAGENT_AGENT_TOKEN secret을 설정해 다시 빌드하거나 ' +
        InstallConfigFileName + '에 AgentTokenFile을 지정하세요.';
    Exit;
  end;

  if IsPlaceholderToken(Token) then
  begin
    AgentConfigWriteFailed := True;
    AgentConfigError := '에이전트 토큰 파일에 기본/샘플 토큰이 있습니다. 실제 토큰으로 교체하세요: ' +
      TokenFile;
    Exit;
  end;

  AgentConfigUrl := Url;
  AgentConfigToken := Token;
  AgentConfigRequested := True;
end;

{ "Agent" 개체의 시작/끝 위치를 찾는다. 문자열 리터럴 안의 중괄호는 무시한다. }
procedure FindAgentObject(const Text: String; var StartPos, EndPos: Integer; var Found: Boolean);
var
  AgentPos, I, Depth: Integer;
  Ch: Char;
  InString, Escaped: Boolean;
begin
  Found := False;
  AgentPos := Pos('"Agent"', Text);
  if AgentPos = 0 then Exit;

  I := AgentPos + Length('"Agent"');
  while (I <= Length(Text)) and (Text[I] <> '{') do Inc(I);
  if I > Length(Text) then Exit;

  StartPos := I;
  Depth := 0;
  InString := False;
  Escaped := False;
  for I := StartPos to Length(Text) do
  begin
    Ch := Text[I];
    if InString then
    begin
      if Escaped then Escaped := False
      else if Ch = '\' then Escaped := True
      else if Ch = '"' then InString := False;
    end
    else if Ch = '"' then InString := True
    else if Ch = '{' then Inc(Depth)
    else if Ch = '}' then
    begin
      Dec(Depth);
      if Depth = 0 then
      begin
        EndPos := I;
        Found := True;
        Exit;
      end;
    end;
  end;
end;

{ 기존 agent.json의 다른 설정은 유지하면서 문자열 속성 하나만 교체/추가한다. }
function SetJsonStringValue(var Text: String; const Key, Value: String): Boolean;
var
  StartPos, EndPos, P, ColonPos, ValueEnd, I: Integer;
  Obj, KeyPattern, NewValue: String;
  Found, Escaped: Boolean;
begin
  Result := False;
  FindAgentObject(Text, StartPos, EndPos, Found);
  if not Found then Exit;

  Obj := Copy(Text, StartPos, EndPos - StartPos + 1);
  KeyPattern := '"' + Key + '"';
  NewValue := '"' + JsonEscape(Value) + '"';
  P := Pos(KeyPattern, Obj);

  if P > 0 then
  begin
    ColonPos := P + Length(KeyPattern);
    while (ColonPos <= Length(Obj)) and (Obj[ColonPos] <> ':') do Inc(ColonPos);
    if ColonPos > Length(Obj) then Exit;
    Inc(ColonPos);
    while (ColonPos <= Length(Obj)) and
          (Obj[ColonPos] in [' ', #9, #13, #10]) do Inc(ColonPos);
    if (ColonPos > Length(Obj)) or (Obj[ColonPos] <> '"') then Exit;

    ValueEnd := ColonPos + 1;
    Escaped := False;
    while ValueEnd <= Length(Obj) do
    begin
      if Escaped then Escaped := False
      else if Obj[ValueEnd] = '\' then Escaped := True
      else if Obj[ValueEnd] = '"' then Break;
      Inc(ValueEnd);
    end;
    if ValueEnd > Length(Obj) then Exit;

    Obj := Copy(Obj, 1, ColonPos - 1) + NewValue +
      Copy(Obj, ValueEnd + 1, Length(Obj) - ValueEnd);
  end
  else
  begin
    I := Length(Obj);
    while (I > 0) and (Obj[I] <> '}') do Dec(I);
    if I = 0 then Exit;
    if I = 2 then
      Obj := '{' + KeyPattern + ':' + NewValue + '}'
    else
      Obj := Copy(Obj, 1, I - 1) + ',' + KeyPattern + ':' + NewValue +
        Copy(Obj, I, Length(Obj) - I + 1);
  end;

  Text := Copy(Text, 1, StartPos - 1) + Obj + Copy(Text, EndPos + 1, Length(Text) - EndPos);
  Result := True;
end;

procedure WriteAgentConfigIfRequested;
var
  ConfigPath, SessionHelper, ConfigText: String;
  Lines, EmptyLines, ExistingLines: TArrayOfString;
  I: Integer;
begin
  AgentConfigWriteFailed := False;
  if not AgentConfigRequested then Exit;

  ConfigPath := ExpandConstant('{commonappdata}\UpdateAgent\agent.json');
  SessionHelper := ExpandConstant('{app}\SessionHelper\UpdateAgent.SessionHelper.exe');
  ForceDirectories(ExtractFileDir(ConfigPath));

  ConfigText := '';
  if IsValidExistingAgentConfig(ConfigPath) and LoadStringsFromFile(ConfigPath, ExistingLines) then
  begin
    for I := 0 to GetArrayLength(ExistingLines) - 1 do
    begin
      if (I = 0) and (Length(ExistingLines[0]) > 0) and
         (ExistingLines[0][1] = #65279) then
        ConfigText := ConfigText + Copy(ExistingLines[0], 2, Length(ExistingLines[0]) - 1) + #13#10
      else
        ConfigText := ConfigText + ExistingLines[I] + #13#10;
    end;
    if AgentConfigSkipWrite then
    begin
      { URL/토큰은 유지하고 설치 경로가 바뀐 경우 SessionHelperPath만 갱신한다. }
      if not SetJsonStringValue(ConfigText, 'SessionHelperPath', SessionHelper) then
      begin
        AgentConfigWriteFailed := True;
        AgentConfigError := '기존 에이전트 설정의 SessionHelperPath를 갱신할 수 없습니다: ' + ConfigPath;
        Exit;
      end;
    end
    else if (not SetJsonStringValue(ConfigText, 'LabManagerUrl', AgentConfigUrl)) or
       (not SetJsonStringValue(ConfigText, 'AgentToken', AgentConfigToken)) or
       (not SetJsonStringValue(ConfigText, 'SessionHelperPath', SessionHelper)) then
    begin
      AgentConfigWriteFailed := True;
      AgentConfigError := '기존 에이전트 설정을 갱신할 수 없습니다: ' + ConfigPath;
      Exit;
    end;
    SetArrayLength(Lines, 1);
    Lines[0] := ConfigText;
  end
  else
  begin
    if AgentConfigSkipWrite then
    begin
      AgentConfigWriteFailed := True;
      AgentConfigError := '기존 에이전트 설정이 없어 SessionHelperPath만 갱신할 수 없습니다: ' + ConfigPath;
      Exit;
    end;
    SetArrayLength(Lines, 6);
    Lines[0] := '{';
    Lines[1] := '  "Agent": {';
    Lines[2] := '    "LabManagerUrl": "' + JsonEscape(AgentConfigUrl) + '",';
    Lines[3] := '    "AgentToken": "' + JsonEscape(AgentConfigToken) + '",';
    Lines[4] := '    "SessionHelperPath": "' + JsonEscape(SessionHelper) + '"';
    Lines[5] := '  }' + #13#10 + '}';
  end;

  { 비밀을 쓰기 전에 빈 파일을 만들고 ACL을 먼저 제한해 일반 사용자의 읽기 창을 없앤다. }
  SetArrayLength(EmptyLines, 0);
  if not SaveStringsToUTF8File(ConfigPath, EmptyLines, False) then
  begin
    AgentConfigWriteFailed := True;
    AgentConfigError := '에이전트 설정 파일을 만들 수 없습니다: ' + ConfigPath;
    Exit;
  end;
  if RunHidden(ExpandConstant('{sys}\icacls.exe'),
      '"' + ConfigPath + '" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)"') <> 0 then
  begin
    DeleteFile(ConfigPath);
    AgentConfigWriteFailed := True;
    AgentConfigError := '에이전트 설정 파일 권한을 제한하지 못했습니다: ' + ConfigPath;
    Exit;
  end;
  if not SaveStringsToUTF8File(ConfigPath, Lines, False) then
  begin
    DeleteFile(ConfigPath);
    AgentConfigWriteFailed := True;
    AgentConfigError := '에이전트 설정 파일을 쓸 수 없습니다: ' + ConfigPath;
    Exit;
  end;
end;

{ 서비스가 실제로 RUNNING 상태에 도달했는지 확인한다(최대 30초). }
function WaitForServiceRunning: Boolean;
var
  I, J: Integer;
  OutputFile, Output: String;
  Strings: TArrayOfString;
begin
  Result := False;
  OutputFile := ExpandConstant('{tmp}\updateagent-sc-query.txt');
  for I := 0 to 30 do
  begin
    DeleteFile(OutputFile);
    RunHidden(ExpandConstant('{sys}\cmd.exe'),
      '/c "' + ExpandConstant('{sys}\sc.exe') + '" query "' + ServiceName +
      '" > "' + OutputFile + '" 2>&1');
    if FileExists(OutputFile) and LoadStringsFromFile(OutputFile, Strings) then
    begin
      Output := '';
      for J := 0 to GetArrayLength(Strings) - 1 do
        Output := Output + Strings[J];
      if Pos('RUNNING', Uppercase(Output)) > 0 then
      begin
        Result := True;
        Exit;
      end;
    end;
    Sleep(1000);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssInstall then
  begin
    ShowStepNotice('새 버전 설치',
      'UpdateAgent 새 버전 설치를 시작합니다.',
      '파일 교체·서비스 등록·방화벽 설정까지 자동으로 진행합니다.');
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

    { 설정 오류는 서비스/방화벽을 만들기 전에 중단해 반쪽 상태를 남기지 않는다. }
    WriteAgentConfigIfRequested;
    if AgentConfigWriteFailed then
      RaiseException('에이전트 설정 파일을 기록하지 못해 설치를 중단합니다. ' + AgentConfigError);

    if not EnsureService then
      RaiseException('UpdateAgentService 등록/설정에 실패해 설치를 중단합니다.');

    if not ApplyFirewallRules then
      RaiseException('UpdateAgent 방화벽 규칙을 등록하지 못해 설치를 중단합니다. ' +
        'Setup 로그와 Windows Defender 방화벽 상태를 확인하세요.');

    Code := RunHidden(ExpandConstant('{sys}\sc.exe'), 'start "' + ServiceName + '"');
    if (Code <> 0) and (Code <> 1056) then
      RaiseException('UpdateAgentService 시작에 실패했습니다(sc ' + IntToStr(Code) + ').');
    if not WaitForServiceRunning then
      RaiseException('UpdateAgentService가 RUNNING 상태가 되지 못했습니다. ' +
        'Setup 로그와 이벤트 로그를 확인하세요.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { 제거 시작 전에도 같은 종료 절차를 사용한다. }
    TerminateAgentApps;
    RemoveFirewallRules;
  end;

  if CurUninstallStep = usPostUninstall then
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete ' + ServiceName);
end;

procedure DeinitializeSetup;
begin
  { Restore SCM recovery if the install aborted before ssPostInstall. }
  RestoreServiceRecovery;
end;

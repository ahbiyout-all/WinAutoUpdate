; ============================================================================
;  UpdateAgent LabManager - Inno Setup 6 installer (교사 PC)
;
;  설치 흐름
;    0) 일반 설치: 각 단계 시작 전 안내 화면을 표시하고 몇 초 후 자동 진행
;    1) 실행 중인 기존 앱 강제 종료
;    2) 라이선스 고지
;    3) 설치 직전 기존 설치 자동 감지 후 무인 제거(설정 파일은 백업/복원)
;    4) 개별 파일(Unpacked Multi-File) 설치
;    5) 바탕화면 바로가기 선택
;    6) 방화벽 규칙(기본 이름/버전 이름) 등록
;    7) 앱 실행
;
;  무인 설치(/VERYSILENT)에서는 안내 화면과 대기 시간을 생략한다.
;
;  무인 설치
;    UpdateAgent.LabManager-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;
;  AppId는 제품 고유 값이며 절대 변경하지 않는다.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.27"
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
#ifndef NoticeDelaySeconds
  #define NoticeDelaySeconds 5
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
Source: "{#StageRoot}\*"; DestDir: "{app}"; Excludes: "appsettings.json,labmanager-token.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
; 첫 설치에서만 읽는 내장 토큰. 설치 폴더에는 복사하지 않는다.
Source: "{#StageRoot}\labmanager-token.txt"; Flags: dontcopy

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
  FirstInstall: Boolean;
  BundledAdminToken: String;
  BundledAgentToken: String;
  LabManagerConfigFile: String;
  LabManagerConfigBackup: String;
  LabManagerConfigRestoreNeeded: Boolean;
  FirewallPort: String;
  LaunchSkippedForDefaultTokens: Boolean;
  StepNoticePage: TOutputProgressWizardPage;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  FirewallGroup = 'UpdateAgent LabManager';
  SampleAdminToken = 'sample-admin-token-replace-before-pilot';
  SampleAgentToken = 'sample-agent-token-replace-before-pilot';
  BundledTokenFileName = 'labmanager-token.txt';
  NoticeSeconds = {#NoticeDelaySeconds};

function ReplaceAllText(const S, OldPat, NewPat: String): String; forward;

procedure InitializeWizard;
begin
  StepNoticePage := CreateOutputProgressPage('UpdateAgent LabManager 설치', '단계 안내');
end;

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
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
  { 종료 → 언인스톨 → 설치 순서는 PrepareToInstall에서 안내와 함께 수행한다. }
  OldInnoUninstall := FindInnoUninstallString;
  { 레지스트리만 보고 첫 설치로 판단하면 레지스트리가 남은 재이미징 PC에서
    내장 토큰 검증이 건너뛰어진다. 기존 설정 파일이 있으면 업데이트로 취급한다. }
  FirstInstall := (OldInnoUninstall = '') and
    (not FileExists(ExpandConstant('{app}\appsettings.json')));
  Result := True;
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

function NormalizeTokenLine(const Value: String): String;
var
  S: String;
begin
  S := Trim(Value);
  if (Length(S) > 0) and (S[1] = #65279) then
    Delete(S, 1, 1);
  Result := Trim(S);
end;

{ 업데이트에서는 내장 토큰을 읽지 않는다. 첫 설치에서만 임시 파일을 추출해 검증한다. }
function ValidateBundledLabManagerTokens: String;
var
  Path: String;
  Lines: TArrayOfString;
  AdminToken, AgentToken: String;
begin
  Result := '';
  BundledAdminToken := '';
  BundledAgentToken := '';
  if not FirstInstall then Exit;

  Path := ExpandConstant('{tmp}\' + BundledTokenFileName);
  if not FileExists(Path) then
    ExtractTemporaryFile(BundledTokenFileName);
  if not FileExists(Path) then
  begin
    Result := '첫 설치용 내장 LabManager 토큰 파일을 찾을 수 없습니다.';
    Exit;
  end;

  if not LoadStringsFromFile(Path, Lines) then
  begin
    Result := '첫 설치용 내장 LabManager 토큰 파일을 읽을 수 없습니다.';
    Exit;
  end;

  { Fail closed: 공개된 샘플/기본 토큰으로 서버가 뜨면 lab 전체가 노출된다.
    첫 설치에는 반드시 실제 토큰 두 개가 내장되어 있어야 한다. 업데이트는 이 함수를 통과하지 않는다. }
  if GetArrayLength(Lines) < 2 then
  begin
    Result := '첫 설치용 내장 LabManager 토큰 파일에 AdminToken과 AgentToken 두 줄이 없습니다. ' +
      '실제 토큰 두 개를 넣어 다시 빌드하세요.';
    Exit;
  end;
  AdminToken := NormalizeTokenLine(Lines[0]);
  AgentToken := NormalizeTokenLine(Lines[1]);
  if (AdminToken = '') or (AgentToken = '') then
  begin
    Result := '첫 설치용 내장 LabManager 토큰 파일에 빈 값이 있습니다. ' +
      '실제 토큰 두 개를 넣어 다시 빌드하세요.';
    Exit;
  end;
  if IsPlaceholderToken(AdminToken) or IsPlaceholderToken(AgentToken) then
  begin
    Result := '첫 설치용 내장 LabManager 토큰에 기본/샘플 값이 있습니다. 실제 토큰으로 다시 빌드하세요.';
    Exit;
  end;

  BundledAdminToken := AdminToken;
  BundledAgentToken := AgentToken;
  Log('Using embedded first-install LabManager tokens (values hidden).');
end;

{ 첫 설치에서만 appsettings.json의 토큰을 내장 값으로 교체한다. }
function ApplyBundledLabManagerTokens: Boolean;
var
  Path, RuntimeTokenPath, Text: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if (not FirstInstall) or (BundledAdminToken = '') or (BundledAgentToken = '') then
  begin
    Result := True;
    Exit;
  end;

  { 이전 설치 흔적이 남은 런타임 관리자 토큰이 있으면 내장 토큰이 우선되도록 제거한다. }
  RuntimeTokenPath := ExpandConstant('{localappdata}\UpdateAgent\LabManager\runtime-admin-token.json');
  if FileExists(RuntimeTokenPath) then
    DeleteFile(RuntimeTokenPath);

  Path := ExpandConstant('{app}\appsettings.json');
  if not LoadStringsFromFile(Path, Lines) then Exit;

  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Text := Text + Lines[I] + #13#10;

  Text := ReplaceAllText(Text, 'change-me-admin-token', BundledAdminToken);
  Text := ReplaceAllText(Text, SampleAdminToken, BundledAdminToken);
  Text := ReplaceAllText(Text, 'change-me-agent-token', BundledAgentToken);
  Text := ReplaceAllText(Text, SampleAgentToken, BundledAgentToken);

  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  Result := SaveStringsToUTF8File(Path, Lines, False);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe: String;
begin
  Result := '';

  { 어떤 것도 지우기 전에 내장 토큰을 검증한다. 업데이트에서는 이 함수가 아무 것도 하지 않는다. }
  Result := ValidateBundledLabManagerTokens;
  if Result <> '' then Exit;

  { 언인스톨보다 먼저 모든 실행 중인 앱을 종료한다. 종료 실패 시 설치를 중단한다. }
  ShowStepNotice('기존 앱 종료',
    '실행 중인 UpdateAgent LabManager 앱을 종료합니다.',
    '대시보드 창과 서버를 정리한 뒤 언인스톨을 준비합니다.');
  if not TerminateLabManagerApps then
  begin
    Result := '기존 UpdateAgent LabManager 앱을 종료하지 못해 설치를 중단합니다. ' +
      '대시보드 창과 LabManager 서버를 닫은 뒤 다시 실행해 주세요.';
    Exit;
  end;

  ShowStepNotice('기존 설치 제거',
    '기존 UpdateAgent LabManager 설치가 있으면 제거합니다.',
    'appsettings.json은 백업된 뒤 새 설치에 복원됩니다.');

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
      { 레지스트리만 남고 제거 프로그램이 없으면 이미 제거된 것으로 보고 계속 진행한다. }
      Log('Previous LabManager uninstaller not found; continuing installation: ' + OldInnoUninstall);
    end
    else
    begin
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

function PsLiteral(const Value: String): String;
begin
  Result := '''' + ReplaceAllText(Value, '''', '''''') + '''';
end;

{ netsh는 group= 인자를 지원하지 않아 그룹 규칙을 만들지 못한다.
  PowerShell NetSecurity cmdlet으로 Domain/Private TCP 규칙을 등록한다. }
function ApplyFirewallRules: Boolean;
var
  ScriptPath, PowerShellExe: String;
  Lines: TArrayOfString;
  Code: Integer;
begin
  { 복원된 appsettings.json의 포트를 사용한다. 설정이 없으면 5080. }
  FirewallPort := GetConfiguredFirewallPort;
  ScriptPath := ExpandConstant('{tmp}\UpdateAgentLabManagerFirewall.ps1');
  SetArrayLength(Lines, 4);
  Lines[0] := '$ErrorActionPreference = ''Stop''';
  Lines[1] := 'Get-NetFirewallRule -Group ' + PsLiteral(FirewallGroup) +
    ' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop';
  Lines[2] := 'New-NetFirewallRule -DisplayName ''UpdateAgent LabManager'' -Direction Inbound' +
    ' -Action Allow -Protocol TCP -LocalPort ' + FirewallPort +
    ' -Enabled True -Profile Domain,Private -Group ' + PsLiteral(FirewallGroup) +
    ' -Description ''UpdateAgent LabManager dashboard/API'' -ErrorAction Stop | Out-Null';
  Lines[3] := 'New-NetFirewallRule -DisplayName ' + PsLiteral('UpdateAgent LabManager {#AppVersion}') +
    ' -Direction Inbound -Action Allow -Protocol TCP -LocalPort ' + FirewallPort +
    ' -Enabled True -Profile Domain,Private -Group ' + PsLiteral(FirewallGroup) +
    ' -Description ''UpdateAgent LabManager (versioned rule)'' -ErrorAction Stop | Out-Null';

  if not SaveStringsToUTF8File(ScriptPath, Lines, False) then
  begin
    Log('Could not create the firewall setup script: ' + ScriptPath);
    Result := False;
    Exit;
  end;

  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Code := RunHidden(PowerShellExe,
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"');
  Result := Code = 0;
  if not Result then
    Log('Failed to configure LabManager firewall rules (code ' + IntToStr(Code) + ')');
end;

procedure RemoveFirewallRules;
var
  ScriptPath, PowerShellExe: String;
  Lines: TArrayOfString;
begin
  ScriptPath := ExpandConstant('{tmp}\UpdateAgentLabManagerFirewallRemove.ps1');
  SetArrayLength(Lines, 2);
  Lines[0] := '$ErrorActionPreference = ''SilentlyContinue''';
  Lines[1] := 'Get-NetFirewallRule -Group ' + PsLiteral(FirewallGroup) +
    ' | Remove-NetFirewallRule -ErrorAction SilentlyContinue';
  if not SaveStringsToUTF8File(ScriptPath, Lines, False) then Exit;
  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  RunHidden(PowerShellExe,
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"');
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

{ 보존된 설정에 공개된 기본/샘플 토큰이 남아 있는지 확인한다.
  남아 있으면 서버가 시작하지 않으므로 대시보드 자동 실행도 생략한다. }
function HasDefaultLabManagerToken: Boolean;
var
  Lines: TStringList;
  I: Integer;
  LowerLine: String;
begin
  Result := False;
  if LabManagerConfigFile = '' then
    LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  if not FileExists(LabManagerConfigFile) then Exit;

  Lines := TStringList.Create;
  try
    Lines.LoadFromFile(LabManagerConfigFile);
    for I := 0 to Lines.Count - 1 do
    begin
      LowerLine := Lowercase(Lines.Strings[I]);
      if (Pos('change-me-', LowerLine) > 0) or
         (Pos('sample-', LowerLine) > 0) and (Pos('-replace-before-pilot', LowerLine) > 0) then
      begin
        Result := True;
        Exit;
      end;
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
    ShowStepNotice('새 버전 설치',
      'UpdateAgent LabManager 새 버전 설치를 시작합니다.',
      '파일 교체·방화벽 설정까지 자동으로 진행합니다.');
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
      { 보존된 토큰은 절대 바꾸지 않는다. 공개된 기본/샘플 값이 남아 있으면
        LabManager가 시작 시 fail-closed로 거부하므로 교사가 실제 토큰을 넣어야 한다. }
      if HasDefaultLabManagerToken then
        Log('appsettings.json still contains placeholder tokens; LabManager will refuse to start until real tokens are configured.');
      DeleteFile(LabManagerConfigBackup);
      LabManagerConfigRestoreNeeded := False;
    end;

    { 첫 설치에서만 내장 토큰을 appsettings.json에 반영한다.
      기존 설치를 업데이트하는 경우 이 함수는 아무 것도 바꾸지 않는다. }
    if not ApplyBundledLabManagerTokens then
      RaiseException('첫 설치용 내장 LabManager 토큰을 설정에 반영하지 못해 설치를 중단합니다.');

    if not ApplyFirewallRules then
      RaiseException('LabManager 방화벽 규칙을 등록하지 못해 설치를 중단합니다. ' +
        'Setup 로그와 Windows Defender 방화벽 상태를 확인하세요.');
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
    RemoveFirewallRules;
  end;
end;

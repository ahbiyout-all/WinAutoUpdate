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
  #define AppVersion "0.1.43"
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
  RunHiddenStarted: Boolean;
  StepNoticePage: TOutputProgressWizardPage;
  TokenChoicePage: TInputOptionWizardPage;
  HasExistingTokenForChoice: Boolean;
  ReplaceExistingToken: Boolean;
  ExternalTokenFile: String;
  ExternalAdminToken: String;
  ExternalAgentToken: String;
  LastTokenApplyError: String;
  LastAclDetail: String;
  LastBackupCleanupError: String;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  FirewallGroup = 'UpdateAgent LabManager';
  SampleAdminToken = 'sample-admin-token-replace-before-pilot';
  SampleAgentToken = 'sample-agent-token-replace-before-pilot';
  BundledTokenFileName = 'labmanager-token.txt';
  ExternalTokenSwitch = '/LabManagerTokenFile=';
  NoticeSeconds = {#NoticeDelaySeconds};

function ReplaceAllText(const S, OldPat, NewPat: String): String; forward;
function FindTextNoCase(const Text, Pattern: String): Integer; forward;
function NormalizeTokenLine(const Value: String): String; forward;

procedure InitializeWizard;
begin
  StepNoticePage := CreateOutputProgressPage('UpdateAgent LabManager 설치', '단계 안내');

  { 기존 설치가 있을 때만 토큰 처리 방법을 묻는다. 무인 설치에서는 기본값(재사용)을 유지한다. }
  HasExistingTokenForChoice := OldInnoUninstall <> '';
  TokenChoicePage := CreateInputOptionPage(wpWelcome,
    '기존 토큰 처리',
    '기존 LabManager 토큰을 어떻게 할까요?',
    '기존 관리자/에이전트 토큰을 그대로 재사용하거나, 릴리스에 내장된 새 토큰으로 교체할 수 있습니다. ' +
      '다른 토큰을 쓰려면 /LabManagerTokenFile=<파일>을 지정하세요.',
    True, False);
  TokenChoicePage.Add('기존 토큰을 백업해 재사용 (권장)');
  TokenChoicePage.Add('기존 토큰을 삭제하고 새 토큰 기록');
  TokenChoicePage.Values[0] := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (TokenChoicePage <> nil) and (PageID = TokenChoicePage.ID) then
    Result := WizardSilent or (not HasExistingTokenForChoice);
end;

function BoolText(const Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

{ "/LabManagerTokenFile=\"C:\path with spaces\tokens.txt\"" 처럼 값 안에 남은
  큰따옴표를 제거한다. Windows 경로에는 큰따옴표가 올 수 없다. }
function StripDoubleQuotes(const Value: String): String;
begin
  Result := ReplaceAllText(Value, '"', '');
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

{ whoami /user /fo csv /nh 출력에서 두 번째 필드(SID)만 추출한다.
  계정 이름을 하드코딩하지 않고 설치를 실행한 교사 계정을 사용한다. }
function ParseCurrentUserSid(const Line: String): String;
var
  P, StartPos: Integer;
begin
  Result := '';
  { 예: "DOMAIN\teacher","S-1-5-21-1234567890-1234567890-1234567890-1001" }
  P := Pos('","', Line);
  if P = 0 then Exit;
  StartPos := P + 3;
  P := StartPos;
  while (P <= Length(Line)) and (Line[P] <> '"') do Inc(P);
  if P > Length(Line) then Exit;
  Result := Copy(Line, StartPos, P - StartPos);
  if Copy(Result, 1, 4) <> 'S-1-' then Result := '';
end;

function GetCurrentUserSid: String;
var
  OutFile, Cmd: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  OutFile := AddBackslash(GetTempDir) + 'UpdateAgent.LabManager.current-sid.txt';
  DeleteFile(OutFile);
  { cmd /c는 첫/마지막 따옴표를 제거하므로 명령 전체를 한 쌍의 따옴표로 감싼다.
    IsProcessRunning과 같은 형태를 사용하지 않으면 whoami 경로는 깨지고
    SID를 얻지 못해 appsettings.json ACL 제한이 실패한다. }
  Cmd := '/c ""' + ExpandConstant('{sys}\whoami.exe') + '" /user /fo csv /nh > "' +
    OutFile + '" 2>&1"';
  RunHidden(ExpandConstant('{sys}\cmd.exe'), Cmd);
  if not LoadStringsFromFile(OutFile, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Result := ParseCurrentUserSid(Lines[I]);
    if Result <> '' then Exit;
  end;
end;

{ 실제 토큰이 들어 있는 appsettings.json은 Administrators, SYSTEM,
  설치를 실행한 교사 계정만 읽도록 제한한다. 실패하면 호출자가 설치를 중단한다. }
function RestrictLabManagerConfigAcl(const Path: String): Boolean;
var
  Sid, Params: String;
  Code, Attempt: Integer;
begin
  Result := False;
  LastAclDetail := '';
  Sid := GetCurrentUserSid;
  if Sid = '' then
  begin
    LastAclDetail := '현재 사용자 SID를 확인하지 못했습니다.';
    Log('Could not determine the current teacher account SID for appsettings.json ACL.');
    Exit;
  end;

  Params := '"' + Path + '" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" "*' +
    Sid + ':(F)"';
  Log('Restricting LabManager appsettings.json ACL (values hidden): ' + Path);
  { 서버가 설정 파일을 잠깐 열어 두는 경우를 대비해 짧게 재시도한다. }
  for Attempt := 1 to 3 do
  begin
    Code := RunHidden(ExpandConstant('{sys}\icacls.exe'), Params);
    if Code = 0 then
    begin
      Result := True;
      Exit;
    end;
    LastAclDetail := 'icacls 종료 코드 ' + IntToStr(Code) + ' (시도 ' + IntToStr(Attempt) + ')';
    Log('Failed to restrict LabManager appsettings.json ACL: ' + LastAclDetail);
    Sleep(250);
  end;
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
  { app 상수는 InitializeSetup에서 아직 초기화되지 않는다.
    기존 appsettings.json 확인은 app 상수가 사용 가능한 PrepareToInstall로 옮긴다. }
  FirstInstall := True;
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

{ JSON 문자열 속성 하나를 대소문자 무관하게 읽는다. SetJsonStringValueAnywhere와
  같은 키 검색 규칙을 써야 재사용 판단과 실제 치환이 어긋나지 않는다. }
function GetJsonStringValueAnywhere(const Text, Key: String): String;
var
  P, ColonPos, ValueEnd: Integer;
  KeyPattern: String;
  Escaped: Boolean;
begin
  Result := '';
  KeyPattern := '"' + Key + '"';
  P := FindTextNoCase(Text, KeyPattern);
  if P = 0 then Exit;

  ColonPos := P + Length(KeyPattern);
  while (ColonPos <= Length(Text)) and (Text[ColonPos] <> ':') do Inc(ColonPos);
  if ColonPos > Length(Text) then Exit;
  Inc(ColonPos);
  while (ColonPos <= Length(Text)) and
        (Text[ColonPos] in [' ', #9, #13, #10]) do Inc(ColonPos);
  if (ColonPos > Length(Text)) or (Text[ColonPos] <> '"') then Exit;
  Inc(ColonPos);

  ValueEnd := ColonPos;
  Escaped := False;
  while ValueEnd <= Length(Text) do
  begin
    if Escaped then Escaped := False
    else if Text[ValueEnd] = '\' then Escaped := True
    else if Text[ValueEnd] = '"' then Break;
    Inc(ValueEnd);
  end;
  if ValueEnd > Length(Text) then Exit;
  Result := NormalizeTokenLine(Copy(Text, ColonPos, ValueEnd - ColonPos));
end;

{ 레지스트리 흔적이 남아 있어도 실제 설정이 없거나 샘플 토큰이면 첫 설치로 취급한다.
  이 경우에만 내장 토큰을 사용하고, 실제 토큰이 없으면 무인 설치도 중단한다. }
function HasUsableLabManagerConfig: Boolean;
var
  Lines: TStringList;
  I: Integer;
  LowerLine, Text, AdminValue, AgentValue: String;
  HasAdmin, HasAgent: Boolean;
begin
  Result := False;
  if LabManagerConfigFile = '' then
    LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  if not FileExists(LabManagerConfigFile) then Exit;

  Lines := TStringList.Create;
  try
    Lines.LoadFromFile(LabManagerConfigFile);
    HasAdmin := False;
    HasAgent := False;
    for I := 0 to Lines.Count - 1 do
    begin
      LowerLine := Lowercase(Trim(Lines.Strings[I]));
      if (Pos('"admintoken"', LowerLine) > 0) or
         (Pos('"agenttoken"', LowerLine) > 0) then
      begin
        if (Pos('change-me-', LowerLine) > 0) or
           ((Pos('sample-', LowerLine) > 0) and (Pos('-replace-before-pilot', LowerLine) > 0)) then
          Exit;
        if Pos('"admintoken"', LowerLine) > 0 then HasAdmin := True;
        if Pos('"agenttoken"', LowerLine) > 0 then HasAgent := True;
      end;
    end;
    if not (HasAdmin and HasAgent) then Exit;

    { 같은 토큰이면 서버가 시작 시 거부하므로 재사용 가능한 설정이 아니다. }
    Text := '';
    for I := 0 to Lines.Count - 1 do
      Text := Text + Lines.Strings[I] + #13#10;
    AdminValue := GetJsonStringValueAnywhere(Text, 'AdminToken');
    AgentValue := GetJsonStringValueAnywhere(Text, 'AgentToken');
    Result := (AdminValue <> '') and (AgentValue <> '') and (AdminValue <> AgentValue);
  finally
    Lines.Free;
  end;
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

{ 대소문자를 구분하지 않는 부분 문자열 검색. 원본 텍스트의 인덱스를 그대로 반환하므로
  값에 유니코드 문자가 있어도 위치가 밀리지 않는다. HasUsableLabManagerConfig가
  소문자로 키를 찾는 것과 같은 규칙을 써야 교체 경로가 일관된다. }
function FindTextNoCase(const Text, Pattern: String): Integer;
var
  I: Integer;
begin
  Result := 0;
  if (Pattern = '') or (Length(Text) < Length(Pattern)) then Exit;
  for I := 1 to Length(Text) - Length(Pattern) + 1 do
  begin
    if CompareText(Copy(Text, I, Length(Pattern)), Pattern) = 0 then
    begin
      Result := I;
      Exit;
    end;
  end;
end;

{ JSON 문자열 속성을 찾아 값만 교체한다. 첫 설치의 자리표시자 교체와 업데이트의 실제 토큰 교체에 모두 쓴다. }
function SetJsonStringValueAnywhere(var Text: String; const Key, Value: String): Boolean;
var
  P, ColonPos, ValueEnd: Integer;
  KeyPattern, NewValue: String;
  Escaped: Boolean;
begin
  Result := False;
  KeyPattern := '"' + Key + '"';
  P := FindTextNoCase(Text, KeyPattern);
  if P = 0 then Exit;

  ColonPos := P + Length(KeyPattern);
  while (ColonPos <= Length(Text)) and (Text[ColonPos] <> ':') do Inc(ColonPos);
  if ColonPos > Length(Text) then Exit;
  Inc(ColonPos);
  while (ColonPos <= Length(Text)) and
        (Text[ColonPos] in [' ', #9, #13, #10]) do Inc(ColonPos);
  if (ColonPos > Length(Text)) or (Text[ColonPos] <> '"') then Exit;

  ValueEnd := ColonPos + 1;
  Escaped := False;
  while ValueEnd <= Length(Text) do
  begin
    if Escaped then Escaped := False
    else if Text[ValueEnd] = '\' then Escaped := True
    else if Text[ValueEnd] = '"' then Break;
    Inc(ValueEnd);
  end;
  if ValueEnd > Length(Text) then Exit;

  NewValue := '"' + JsonEscape(Value) + '"';
  Text := Copy(Text, 1, ColonPos - 1) + NewValue +
    Copy(Text, ValueEnd + 1, Length(Text) - ValueEnd);
  Result := True;
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
    Param := StripDoubleQuotes(ParamStr(I));
    if Pos(UpperPrefix, Uppercase(Param)) = 1 then
    begin
      Result := Copy(Param, Length(Prefix) + 1, Length(Param));
      Exit;
    end;
  end;
end;

{ 상대 경로는 설치 exe가 있는 폴더 기준으로 해석한다. }
function ResolveInstallTokenFile(const Value: String): String;
var
  TokenFile: String;
begin
  TokenFile := Trim(StripDoubleQuotes(Value));
  if Copy(TokenFile, 1, 2) = '.\' then
    TokenFile := Copy(TokenFile, 3, Length(TokenFile) - 2);
  if Copy(TokenFile, 1, 2) = '//' then
    TokenFile := '\\' + Copy(TokenFile, 3, Length(TokenFile) - 2);

  if (Pos(':', TokenFile) = 2) or (Copy(TokenFile, 1, 2) = '\\') then
    Result := TokenFile
  else
    Result := ExpandConstant('{src}\' + TokenFile);
end;

{ 릴리스에 내장된 두 줄 토큰 파일을 임시 폴더에 펼쳐 검증한다.
  첫 설치와, 사용자가 명시적으로 교체를 선택한 업데이트에서만 사용한다.
  공개된 샘플/기본 토큰이면 lab 전체가 노출되므로 fail-closed로 중단한다. }
function LoadBundledLabManagerTokens(var AdminToken, AgentToken: String): String;
var
  Path: String;
  Lines: TArrayOfString;
begin
  Result := '';
  AdminToken := '';
  AgentToken := '';
  Path := ExpandConstant('{tmp}\' + BundledTokenFileName);
  if not FileExists(Path) then
    ExtractTemporaryFile(BundledTokenFileName);
  if not FileExists(Path) then
  begin
    Result := '릴리스 내장 LabManager 토큰 파일을 찾을 수 없습니다.';
    Exit;
  end;

  if not LoadStringsFromFile(Path, Lines) then
  begin
    Result := '릴리스 내장 LabManager 토큰 파일을 읽을 수 없습니다.';
    Exit;
  end;

  if GetArrayLength(Lines) < 2 then
  begin
    Result := '릴리스 내장 LabManager 토큰 파일에 AdminToken과 AgentToken 두 줄이 없습니다. ' +
      '실제 토큰 두 개를 넣어 다시 빌드하세요.';
    Exit;
  end;
  AdminToken := NormalizeTokenLine(Lines[0]);
  AgentToken := NormalizeTokenLine(Lines[1]);
  if (AdminToken = '') or (AgentToken = '') then
  begin
    Result := '릴리스 내장 LabManager 토큰 파일에 빈 값이 있습니다. ' +
      '실제 토큰 두 개를 넣어 다시 빌드하세요.';
    Exit;
  end;
  if IsPlaceholderToken(AdminToken) or IsPlaceholderToken(AgentToken) then
  begin
    Result := '릴리스 내장 LabManager 토큰에 기본/샘플 값이 있습니다. 실제 토큰으로 다시 빌드하세요.';
    Exit;
  end;
  { 초기화된 값은 이미 NormalizeTokenLine으로 트리밍되어 있다. 역할 분리를 위해
    두 토큰이 같으면 토큰 값을 기록하지 않고 일반적인 메시지로 중단한다. }
  if AdminToken = AgentToken then
  begin
    Result := 'AdminToken과 AgentToken이 같습니다. 서로 다른 토큰을 사용하세요.';
    Exit;
  end;
end;

{ 업데이트에서 토큰 교체를 선택한 경우: 외부 파일이 있으면 그것을,
  없으면 릴리스에 내장된 토큰을 사용한다. }
function ValidateReplacementLabManagerTokens: String;
var
  Lines: TArrayOfString;
  AdminToken, AgentToken: String;
begin
  Result := '';
  ExternalAdminToken := '';
  ExternalAgentToken := '';
  if (not ReplaceExistingToken) or FirstInstall then Exit;

  ExternalTokenFile := Trim(CmdParamValue(ExternalTokenSwitch));
  if ExternalTokenFile <> '' then
  begin
    ExternalTokenFile := ResolveInstallTokenFile(ExternalTokenFile);
    if not FileExists(ExternalTokenFile) then
    begin
      Result := '새 LabManager 토큰 파일을 읽을 수 없습니다: ' + ExternalTokenFile;
      Exit;
    end;
    if (not LoadStringsFromFile(ExternalTokenFile, Lines)) or
       (GetArrayLength(Lines) < 2) then
    begin
      Result := '새 LabManager 토큰 파일에는 AdminToken과 AgentToken 두 줄이 필요합니다: ' +
        ExternalTokenFile;
      Exit;
    end;

    AdminToken := NormalizeTokenLine(Lines[0]);
    AgentToken := NormalizeTokenLine(Lines[1]);
    if (AdminToken = '') or (AgentToken = '') then
    begin
      Result := '새 LabManager 토큰 파일에 빈 값이 있습니다: ' + ExternalTokenFile;
      Exit;
    end;
    if IsPlaceholderToken(AdminToken) or IsPlaceholderToken(AgentToken) then
    begin
      Result := '새 LabManager 토큰 파일에 기본/샘플 값이 있습니다. 실제 토큰으로 교체하세요: ' +
        ExternalTokenFile;
      Exit;
    end;
    { 트리밍된 두 토큰이 같으면 관리자/에이전트 역할이 구분되지 않는다. }
    if AdminToken = AgentToken then
    begin
      Result := 'AdminToken과 AgentToken이 같습니다. 서로 다른 토큰을 사용하세요: ' +
        ExternalTokenFile;
      Exit;
    end;
    Log('Using explicit replacement LabManager token file (values hidden).');
  end
  else
  begin
    Result := LoadBundledLabManagerTokens(AdminToken, AgentToken);
    if Result <> '' then
    begin
      Result := '교체용 릴리스 내장 토큰을 읽을 수 없습니다: ' + Result;
      Exit;
    end;
    Log('Using embedded release LabManager tokens for replacement (values hidden).');
  end;

  ExternalAdminToken := AdminToken;
  ExternalAgentToken := AgentToken;
end;

{ 첫 설치에서는 내장 토큰만 사용한다. 업데이트는 재사용 시 설정을 그대로 두고,
  교체 선택 시 위의 ValidateReplacementLabManagerTokens가 토큰을 준비한다. }
function ValidateBundledLabManagerTokens: String;
var
  AdminToken, AgentToken: String;
begin
  Result := '';
  BundledAdminToken := '';
  BundledAgentToken := '';
  if not FirstInstall then Exit;

  Result := LoadBundledLabManagerTokens(AdminToken, AgentToken);
  if Result <> '' then Exit;
  BundledAdminToken := AdminToken;
  BundledAgentToken := AgentToken;
  Log('Using embedded first-install LabManager tokens (values hidden).');
end;

{ CopyFile는 원본 파일 속성을 유지한다. 백업본이 읽기 전용이면 복원된
  appsettings.json도 읽기 전용이 되어 토큰 저장이 실패하므로 먼저 해제한다. }
function EnsureWritableConfigFile(const Path: String): Boolean;
begin
  Result := True;
  if not FileExists(Path) then Exit;
  if RunHidden(ExpandConstant('{sys}\attrib.exe'), '-R "' + Path + '"') <> 0 then
  begin
    Log('Could not clear read-only attribute on LabManager config: ' + Path);
    Result := False;
  end;
end;

{ 백업본에는 이전 토큰 원문이 들어 있다. 성공 경로와 중단 경로 모두에서
  읽기 전용 속성을 해제하고 여러 번 시도해 반드시 정리한다. }
function RemoveLabManagerConfigBackup(const Path: String): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  LastBackupCleanupError := '';
  if (Path = '') or (not FileExists(Path)) then
  begin
    Result := True;
    Exit;
  end;
  EnsureWritableConfigFile(Path);
  for Attempt := 1 to 3 do
  begin
    if DeleteFile(Path) and (not FileExists(Path)) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(200);
  end;
  LastBackupCleanupError := '임시 LabManager 설정 백업을 삭제하지 못했습니다: ' + Path;
  Log(LastBackupCleanupError);
end;

function FailWithLabManagerBackupCleanup(const Message: String): String;
begin
  Result := Message;
  if (LabManagerConfigBackup <> '') and
     (not RemoveLabManagerConfigBackup(LabManagerConfigBackup)) then
    Result := Result + ' ' + LastBackupCleanupError;
  LabManagerConfigRestoreNeeded := False;
end;

{ 첫 설치에서만 appsettings.json의 토큰을 내장 값으로 교체한다. }
function ApplyBundledLabManagerTokens: Boolean;
var
  Path, RuntimeTokenPath, Text: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  LastTokenApplyError := '';
  if (not FirstInstall) or (BundledAdminToken = '') or (BundledAgentToken = '') then
  begin
    Result := True;
    Exit;
  end;

  { 이전 설치 흔적이 남은 런타임 관리자 토큰이 있으면 내장 토큰이 우선되도록 제거한다.
    삭제에 실패하면 새 내장 토큰이 런타임 재정의에 가려지므로 설치를 중단한다. }
  RuntimeTokenPath := ExpandConstant('{localappdata}\UpdateAgent\LabManager\runtime-admin-token.json');
  if FileExists(RuntimeTokenPath) and (not DeleteFile(RuntimeTokenPath)) then
  begin
    LastTokenApplyError := '이전 관리자 토큰 재정의 파일을 삭제하지 못했습니다.';
    Log('Could not remove runtime admin token override during first install: ' + RuntimeTokenPath);
    Exit;
  end;

  Path := ExpandConstant('{app}\appsettings.json');
  if not LoadStringsFromFile(Path, Lines) then
  begin
    LastTokenApplyError := 'appsettings.json을 읽을 수 없습니다.';
    Log('Could not read appsettings.json during first install: ' + Path);
    Exit;
  end;

  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Text := Text + Lines[I] + #13#10;

  Text := ReplaceAllText(Text, 'change-me-admin-token', BundledAdminToken);
  Text := ReplaceAllText(Text, SampleAdminToken, BundledAdminToken);
  Text := ReplaceAllText(Text, 'change-me-agent-token', BundledAgentToken);
  Text := ReplaceAllText(Text, SampleAgentToken, BundledAgentToken);

  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  if not EnsureWritableConfigFile(Path) then
  begin
    LastTokenApplyError := 'appsettings.json의 읽기 전용 속성을 해제하지 못했습니다.';
    Exit;
  end;
  Result := SaveStringsToUTF8File(Path, Lines, False);
  if not Result then
  begin
    LastTokenApplyError := 'appsettings.json에 토큰을 저장할 수 없습니다.';
    Log('Could not write appsettings.json during first install: ' + Path);
    Exit;
  end;
  Result := RestrictLabManagerConfigAcl(Path);
  if not Result then
    LastTokenApplyError := 'appsettings.json 접근 권한을 제한하지 못했습니다. ' + LastAclDetail;
end;

{ 업데이트에서 교체를 선택한 경우에만 실제 토큰 두 개를 설정에 반영한다. }
function ApplyReplacementLabManagerTokens: Boolean;
var
  Path, RuntimeTokenPath, Text: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  LastTokenApplyError := '';
  if FirstInstall or (not ReplaceExistingToken) or
     (ExternalAdminToken = '') or (ExternalAgentToken = '') then
  begin
    Result := True;
    Exit;
  end;

  { 이전 관리자 토큰의 런타임 재정의가 남아 있으면 새 토큰이 무시된다. }
  RuntimeTokenPath := ExpandConstant('{localappdata}\UpdateAgent\LabManager\runtime-admin-token.json');
  if FileExists(RuntimeTokenPath) and (not DeleteFile(RuntimeTokenPath)) then
  begin
    LastTokenApplyError := '이전 관리자 토큰 재정의 파일을 삭제하지 못했습니다.';
    Log('Could not remove runtime admin token override during replacement: ' + RuntimeTokenPath);
    Exit;
  end;

  Path := ExpandConstant('{app}\appsettings.json');
  if not LoadStringsFromFile(Path, Lines) then
  begin
    LastTokenApplyError := 'appsettings.json을 읽을 수 없습니다.';
    Log('Could not read appsettings.json during token replacement: ' + Path);
    Exit;
  end;

  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Text := Text + Lines[I];

  if not SetJsonStringValueAnywhere(Text, 'AdminToken', ExternalAdminToken) then
  begin
    LastTokenApplyError := 'AdminToken 키를 찾지 못했거나 값 형식이 올바르지 않습니다.';
    Log('AdminToken key was not found or is malformed during replacement: ' + Path);
    Exit;
  end;
  if not SetJsonStringValueAnywhere(Text, 'AgentToken', ExternalAgentToken) then
  begin
    LastTokenApplyError := 'AgentToken 키를 찾지 못했거나 값 형식이 올바르지 않습니다.';
    Log('AgentToken key was not found or is malformed during replacement: ' + Path);
    Exit;
  end;

  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  if not EnsureWritableConfigFile(Path) then
  begin
    LastTokenApplyError := 'appsettings.json의 읽기 전용 속성을 해제하지 못했습니다.';
    Exit;
  end;
  Result := SaveStringsToUTF8File(Path, Lines, False);
  if not Result then
  begin
    LastTokenApplyError := 'appsettings.json에 토큰을 저장할 수 없습니다.';
    Log('Could not write appsettings.json during token replacement: ' + Path);
    Exit;
  end;
  Result := RestrictLabManagerConfigAcl(Path);
  if not Result then
    LastTokenApplyError := 'appsettings.json 접근 권한을 제한하지 못했습니다. ' + LastAclDetail;
end;

{ 기존 appsettings.json은 강제 삭제하지 않고 백업본의 토큰 값만 비운다.
  언인스톨이 설치 폴더의 파일을 지우더라도 주소·로깅 등 다른 설정은 복원되고,
  이전 토큰 값은 새 설치에 남지 않는다. }
function SanitizeTokenBackup: Boolean;
var
  Lines: TArrayOfString;
  Text: String;
  I: Integer;
begin
  Result := False;
  if (LabManagerConfigBackup = '') or (not FileExists(LabManagerConfigBackup)) then
  begin
    Log('No LabManager config backup to sanitize.');
    Result := True;
    Exit;
  end;

  if not LoadStringsFromFile(LabManagerConfigBackup, Lines) then
  begin
    Log('Could not read the LabManager config backup for token removal.');
    Exit;
  end;
  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    Text := Text + Lines[I] + #13#10;
  if (not SetJsonStringValueAnywhere(Text, 'AdminToken', '')) or
     (not SetJsonStringValueAnywhere(Text, 'AgentToken', '')) then
  begin
    Log('Could not clear old token keys in the LabManager config backup.');
    Exit;
  end;
  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  if not EnsureWritableConfigFile(LabManagerConfigBackup) then Exit;
  if not SaveStringsToUTF8File(LabManagerConfigBackup, Lines, False) then
  begin
    Log('Could not write the sanitized LabManager config backup.');
    Exit;
  end;
  Log('Old LabManager token values removed from the config backup (values hidden).');
  Result := True;
end;

{ 토큰 교체를 선택하면 언인스톨 직후 기존 토큰 값과 런타임 재정의를 삭제한다.
  설정 파일 자체는 보존하므로 다른 설정은 그대로 복원된다. }
function RemoveExistingLabManagerTokens: Boolean;
var
  RuntimeTokenPath: String;
begin
  Result := False;
  if LabManagerConfigRestoreNeeded and (not SanitizeTokenBackup) then Exit;

  RuntimeTokenPath := ExpandConstant('{localappdata}\UpdateAgent\LabManager\runtime-admin-token.json');
  if FileExists(RuntimeTokenPath) and (not DeleteFile(RuntimeTokenPath)) then
  begin
    Log('Could not remove the runtime override containing old LabManager tokens.');
    Exit;
  end;

  Log('Old LabManager tokens removed before writing replacement tokens (values hidden).');
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe: String;
begin
  Result := '';

  { 이 시점에는 app 상수가 초기화되어 있다. 첫 설치 여부는 레지스트리가 아니라
    사용 가능한 appsettings.json(실제 토큰 두 개) 존재 여부로 판단한다.
    레지스트리만 남은 재이미징 PC에서도 설정이 없으면 첫 설치로 취급해 fail closed한다. }
  FirstInstall := not HasUsableLabManagerConfig;

  { 마법사에서 기존 토큰 교체를 선택하면 새 토큰 파일을 요구한다.
    무인 설치에서 /LabManagerTokenFile=를 명시하면 재사용 기본값을 overriding하여
    토큰 교체로 처리한다. 명시하지 않으면 기존 토큰을 유지한다. }
  ReplaceExistingToken := ((not WizardSilent) and HasExistingTokenForChoice and
    (TokenChoicePage <> nil) and TokenChoicePage.Values[1]) or
    (Trim(CmdParamValue(ExternalTokenSwitch)) <> '');

  { 어떤 것도 지우기 전에 토큰을 검증한다. 첫 설치는 내장 토큰,
    업데이트의 교체 선택은 /LabManagerTokenFile= 외부 파일을 쓰고, 없으면 내장 토큰을 쓴다. }
  Result := ValidateBundledLabManagerTokens;
  if Result <> '' then Exit;
  Result := ValidateReplacementLabManagerTokens;
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

  { 레지스트리 흔적이 없어도 설치 폴더와 제거 프로그램이 남아 있으면 기존 설치로 본다.
    토큰 교체를 선택했다면 새 파일을 쓰기 전에 반드시 이 언인스톨을 먼저 끝낸다. }
  if (OldInnoUninstall = '') and FileExists(ExpandConstant('{app}\appsettings.json')) then
  begin
    UninstallExe := ExpandConstant('{app}\unins000.exe');
    if FileExists(UninstallExe) then
      OldInnoUninstall := '"' + UninstallExe + '"';
  end;

  ShowStepNotice('기존 설치 제거',
    '기존 UpdateAgent LabManager 설치를 먼저 완전히 제거합니다.',
    '토큰을 새로 기록할 때는 언인스톨이 끝난 뒤에만 새 설치를 진행하며, ' +
    '기존 토큰 값과 런타임 재정의는 삭제됩니다. ' +
    'appsettings.json의 다른 설정은 백업에서 복원됩니다.');

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
      { 언인스톨을 건너뛰고 새 파일을 덮어쓰면 이전 설치 잔해가 남는다.
        특히 토큰 교체를 선택했다면 반드시 제거를 먼저 수행해야 하므로 중단한다. }
      Result := FailWithLabManagerBackupCleanup(
        '기존 UpdateAgent LabManager 제거 프로그램을 찾지 못해 설치를 중단합니다: ' +
        OldInnoUninstall);
      Exit;
    end;

    Code := -1;
    if not Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      Result := FailWithLabManagerBackupCleanup(
        '기존 UpdateAgent LabManager 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.');
      Exit;
    end;

    if not WaitForInnoUninstallComplete then
    begin
      Result := FailWithLabManagerBackupCleanup(
        '기존 UpdateAgent LabManager 제거가 완료되지 않아 설치를 중단합니다.');
      Exit;
    end;
    Log('Existing LabManager uninstall completed before installing new files (tokens replaced: ' +
      BoolText(ReplaceExistingToken) + ').');
  end;

  { 새 토큰 기록을 선택했다면 언인스톨이 끝난 시점에 기존 토큰을 제거한다.
    실패하면 이전 토큰이 새 설치에 섞일 수 있으므로 여기서 중단한다. }
  if ReplaceExistingToken then
  begin
    if not RemoveExistingLabManagerTokens then
    begin
      Result := FailWithLabManagerBackupCleanup(
        '기존 LabManager 토큰을 삭제하지 못해 설치를 중단합니다.');
      Exit;
    end;
  end;

  { 설정 복원은 InstallDelete/[Files]가 끝난 뒤 ssPostInstall에서 수행한다.
    토큰 교체를 선택한 경우에는 토큰 값만 비운 백업을 복원하므로
    주소·로깅 등 다른 설정은 유지되고 이전 토큰 값은 남지 않는다. }
end;

{ 교사 PC 방화벽은 TCP 5080만 Domain/Private 프로필로 연다.
  appsettings.json의 Urls/ListenUrl 값을 따르면 오래된 설정이 임의 포트를 열 수 있으므로
  설정 파일과 무관하게 고정한다. }
function GetConfiguredFirewallPort: String;
begin
  Result := '5080';
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
    Log('Failed to configure LabManager firewall rules (started=' +
      BoolText(RunHiddenStarted) + ', code ' + IntToStr(Code) + ')');
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
  if not SaveStringsToUTF8File(ScriptPath, Lines, True) then Exit;
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
      EnsureWritableConfigFile(LabManagerConfigFile);
      { [Files] may have created appsettings.json with onlyifdoesntexist; overwrite with the backup. }
      if not CopyFile(LabManagerConfigBackup, LabManagerConfigFile, False) then
        RaiseException('기존 LabManager appsettings.json 복원에 실패해 설치를 중단합니다. 백업: ' + LabManagerConfigBackup);
      { 보존된 토큰은 절대 바꾸지 않는다. 공개된 기본/샘플 값이 남아 있으면
        LabManager가 시작 시 fail-closed로 거부하므로 교사가 실제 토큰을 넣어야 한다. }
      if HasDefaultLabManagerToken then
        Log('appsettings.json still contains placeholder tokens; LabManager will refuse to start until real tokens are configured.');
    end;

    { 첫 설치에서만 내장 토큰을 appsettings.json에 반영한다.
      기존 설치를 업데이트하는 경우 이 함수는 아무 것도 바꾸지 않는다. }
    if not ApplyBundledLabManagerTokens then
      RaiseException('첫 설치용 내장 LabManager 토큰을 설정에 반영하지 못해 설치를 중단합니다. ' +
        LastTokenApplyError);

    { 업데이트에서 교체를 선택한 경우에만 백업에서 복원된 설정의 토큰을 새 값으로 바꾼다. }
    if not ApplyReplacementLabManagerTokens then
      RaiseException('새 LabManager 토큰을 설정에 반영하지 못해 설치를 중단합니다. ' +
        LastTokenApplyError);

    { 두 토큰 적용이 모두 성공한 뒤에만 백업을 삭제한다. 토큰 저장이 실패하면
      복원본을 남겨 두어 이전 단계의 실패에서도 되돌릴 수 있게 한다.
      삭제 자체가 실패하면 이전 토큰이 임시 폴더에 남으므로 fail closed한다. }
    if LabManagerConfigRestoreNeeded then
    begin
      if not RemoveLabManagerConfigBackup(LabManagerConfigBackup) then
        RaiseException('임시 LabManager 설정 백업을 삭제하지 못해 설치를 중단합니다. ' +
          LastBackupCleanupError);
      LabManagerConfigRestoreNeeded := False;
    end;

    if not ApplyFirewallRules then
      RaiseException('LabManager 방화벽 규칙을 등록하지 못해 설치를 중단합니다. ' +
        'Setup 로그와 Windows Defender 방화벽 상태를 확인하세요.');
  end
  else if CurStep = ssDone then
  begin
    if LaunchSkippedForDefaultTokens and not WizardSilent then
      MsgBox(
        '기본/샘플 토큰(change-me-* 또는 sample-*-replace-before-pilot)이 남아 있어 대시보드를 자동 실행하지 않았습니다.' + #13#10 +
        '설치 폴더의 appsettings.json에서 AdminToken과 AgentToken을 변경한 뒤 런처를 실행하세요.',
        mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Terminated: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    Terminated := TerminateLabManagerApps;
    if not Terminated then
      RaiseException('기존 UpdateAgent LabManager 앱을 종료하지 못해 제거를 중단합니다.');
    RemoveFirewallRules;
  end;
end;

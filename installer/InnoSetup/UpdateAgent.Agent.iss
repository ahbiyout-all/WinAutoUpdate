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
  #define AppVersion "0.1.0"
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
AppPublisherURL=https://github.com/AhBiYout-all/UpdateAgent
AppSupportURL=https://github.com/AhBiYout-all/UpdateAgent/issues
AppUpdatesURL=https://github.com/AhBiYout-all/UpdateAgent/releases
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
UninstallDisplayIcon={app}\Service\UpdateAgent.Service.exe
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
DirExistsWarning=no
UsePreviousAppDir=yes
SetupLogging=yes
ShowLanguageDialog=no
LanguageDetectionMethod=none

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 UpdateAgent 상태 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: checkedonce

[Files]
; 설치 폴더는 서비스/세션 도우미/CLI로 나뉜 개별 파일 구조다.
; 문서, 심볼, 압축/포터블 파일은 빌드 스크립트에서 스테이징 단계에 제거한다.
Source: "{#StageRoot}\Service\*"; DestDir: "{app}\Service"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\Service\appsettings.json"; DestDir: "{app}\Service"; Flags: onlyifdoesntexist
Source: "{#StageRoot}\SessionHelper\*"; DestDir: "{app}\SessionHelper"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageRoot}\Cli\*"; DestDir: "{app}\Cli"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; 덮어쓰기 설치에서 이전 버전의 잔여 파일을 남기지 않는다.
Type: filesandordirs; Name: "{app}\Service"
Type: filesandordirs; Name: "{app}\SessionHelper"
Type: filesandordirs; Name: "{app}\Cli"

[Icons]
Name: "{group}\UpdateAgent 상태 확인"; Filename: "{cmd}"; Parameters: "/k ""{app}\Cli\UpdateAgent.Cli.exe"" status"; WorkingDir: "{app}\Cli"; IconFilename: "{app}\Cli\UpdateAgent.Cli.exe"
Name: "{autodesktop}\UpdateAgent 상태 확인"; Filename: "{cmd}"; Parameters: "/k ""{app}\Cli\UpdateAgent.Cli.exe"" status"; WorkingDir: "{app}\Cli"; IconFilename: "{app}\Cli\UpdateAgent.Cli.exe"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/k ""{app}\Cli\UpdateAgent.Cli.exe"" status"; WorkingDir: "{app}\Cli"; Description: "UpdateAgent 상태 확인"; Flags: postinstall nowait skipifsilent

[Code]
var
  OldInnoUninstall: String;
  OldMsiProduct: String;

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

procedure KillProcess(const ImageName: String);
begin
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "' + ImageName + '" /T');
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

function CmdParamValue(const Prefix: String): String;
var
  I: Integer;
  Param: String;
begin
  Result := '';
  for I := 1 to ParamCount do
  begin
    Param := ParamStr(I);
    if Pos(Prefix, Param) = 1 then
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
  { 1) 실행 중인 기존 앱을 먼저 강제 종료한다. }
  KillProcess('UpdateAgent.SessionHelper.exe');
  KillProcess('UpdateAgent.Cli.exe');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop ' + ServiceName);
  KillProcess('UpdateAgent.Service.exe');

  { 2) 기존 설치는 선택 없이 PrepareToInstall에서 항상 제거한다. }
  OldInnoUninstall := FindInnoUninstallString;
  OldMsiProduct := FindMsiProductCode;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  UninstallExe, ConfigFile, BackupFile: String;
  ConfigSaved: Boolean;
begin
  Result := '';

  KillProcess('UpdateAgent.SessionHelper.exe');
  KillProcess('UpdateAgent.Cli.exe');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop ' + ServiceName);
  Sleep(1200);
  KillProcess('UpdateAgent.Service.exe');

  { 이전 제거 프로그램이 appsettings.json도 삭제하므로 먼저 백업한다. }
  ConfigFile := ExpandConstant('{app}\Service\appsettings.json');
  BackupFile := AddBackslash(GetTempDir) + 'UpdateAgent.Agent.appsettings.json.bak';
  ConfigSaved := False;
  if FileExists(ConfigFile) then
  begin
    ConfigSaved := CopyFile(ConfigFile, BackupFile, False);
    if not ConfigSaved then
      Log('Failed to back up existing Agent appsettings.json');
  end;

  { MSI로 설치된 제품이 있으면 Inno 설치와 공존할 수 없으므로 항상 제거한다. }
  if OldMsiProduct <> '' then
  begin
    Code := -1;
    if not Exec(ExpandConstant('{sys}\msiexec.exe'),
        '/x ' + OldMsiProduct + ' /qn /norestart', '',
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      Result := '기존 WiX MSI UpdateAgent Agent 제거에 실패했습니다. (code ' +
        IntToStr(Code) + ') 설치를 중단합니다.';
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
  end;

  if ConfigSaved then
  begin
    ForceDirectories(ExtractFileDir(ConfigFile));
    if not CopyFile(BackupFile, ConfigFile, False) then
      Log('Failed to restore Agent appsettings.json');
    DeleteFile(BackupFile);
  end;
end;

procedure EnsureService;
var
  ServiceExe: String;
  Sc: String;
begin
  ServiceExe := ExpandConstant('{app}\Service\UpdateAgent.Service.exe');
  Sc := ExpandConstant('{sys}\sc.exe');

  if RunHidden(Sc, 'query ' + ServiceName) <> 0 then
    RunHidden(Sc, 'create ' + ServiceName + ' binPath= "' + ServiceExe +
      '" start= auto DisplayName= "UpdateAgent Service"')
  else
    RunHidden(Sc, 'config ' + ServiceName + ' binPath= "' + ServiceExe + '" start= auto');

  RunHidden(Sc, 'description ' + ServiceName + ' "Windows Update automation agent for computer labs"');
  RunHidden(Sc, 'failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  RunHidden(Sc, 'failureflag ' + ServiceName + ' 1');
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
begin
  Url := Trim(CmdParamValue('/LabManagerUrl='));
  TokenFile := Trim(CmdParamValue('/AgentTokenFile='));
  if (Url = '') and (TokenFile = '') then Exit;

  if (Url = '') or (TokenFile = '') then
  begin
    if not WizardSilent then
      MsgBox('/LabManagerUrl과 /AgentTokenFile은 함께 지정해야 합니다. 설정 파일을 쓰지 않습니다.',
        mbInformation, MB_OK);
    Exit;
  end;

  if (not LoadStringsFromFile(TokenFile, TokenLines)) or (GetArrayLength(TokenLines) = 0) then
  begin
    if not WizardSilent then
      MsgBox('에이전트 토큰 파일을 읽을 수 없습니다: ' + TokenFile, mbError, MB_OK);
    Exit;
  end;

  Token := Trim(TokenLines[0]);
  if (Length(Token) > 0) and (Token[1] = #65279) then
    Delete(Token, 1, 1);
  Token := Trim(Token);
  if Token = '' then Exit;

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

  if SaveStringsToUTF8File(ConfigPath, Lines, False) then
    RunHidden(ExpandConstant('{sys}\icacls.exe'),
      '"' + ConfigPath + '" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)"');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    EnsureService;
    ApplyFirewallRules;
    WriteAgentConfigIfRequested;

    Code := RunHidden(ExpandConstant('{sys}\sc.exe'), 'start ' + ServiceName);
    if (Code <> 0) and (Code <> 1056) and not WizardSilent then
      MsgBox('서비스 시작에 실패했습니다(sc ' + IntToStr(Code) + '). Windows 서비스에서 UpdateAgentService를 시작하세요.',
        mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop ' + ServiceName);
    KillProcess('UpdateAgent.SessionHelper.exe');
    KillProcess('UpdateAgent.Cli.exe');
    KillProcess('UpdateAgent.Service.exe');
    RunHidden(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  end;

  if CurUninstallStep = usPostUninstall then
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete ' + ServiceName);
end;

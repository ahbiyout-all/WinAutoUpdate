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
  #define AppVersion "0.1.1"
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
; 대시보드 정적 파일은 설치 패키지가 항상 최신 상태로 덮어쓴다. appsettings.json은 제외한다.
Type: filesandordirs; Name: "{app}\wwwroot"

[Icons]
Name: "{group}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"
Name: "{group}\UpdateAgent LabManager 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; Description: "UpdateAgent LabManager 실행"; Flags: postinstall nowait skipifsilent

[Code]
var
  OldInnoUninstall: String;
  LabManagerConfigFile: String;
  LabManagerConfigBackup: String;
  LabManagerConfigRestoreNeeded: Boolean;

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  FirewallGroup = 'UpdateAgent LabManager';

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
  { 1) 실행 중인 기존 앱을 먼저 강제 종료한다. }
  KillProcess('UpdateAgent.LabManager.Desktop.exe');
  KillProcess('UpdateAgent.LabManager.exe');

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
  KillProcess('UpdateAgent.LabManager.Desktop.exe');
  KillProcess('UpdateAgent.LabManager.exe');

  { 이전 제거 프로그램/InstallDelete가 appsettings.json을 지우므로 먼저 백업한다. }
  LabManagerConfigFile := ExpandConstant('{app}\appsettings.json');
  LabManagerConfigBackup := AddBackslash(GetTempDir) + 'UpdateAgent.LabManager.appsettings.json.bak';
  LabManagerConfigRestoreNeeded := False;
  DeleteFile(LabManagerConfigBackup);
  if FileExists(LabManagerConfigFile) then
  begin
    if not CopyFile(LabManagerConfigFile, LabManagerConfigBackup, False) then
    begin
      Result := '기존 LabManager appsettings.json 백업에 실패해 설치를 중단합니다.';
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

procedure AddFirewallRule(const RuleName, ProgramPath, Description: String);
begin
  RunHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="' + RuleName +
    '" dir=in action=allow program="' + ProgramPath +
    '" enable=yes profile=any group="' + FirewallGroup +
    '" description="' + Description + '"');
end;

procedure ApplyFirewallRules;
var
  AppExe: String;
begin
  AppExe := ExpandConstant('{app}\UpdateAgent.LabManager.exe');

  RunHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  AddFirewallRule('UpdateAgent LabManager', AppExe, 'UpdateAgent LabManager dashboard/API');
  AddFirewallRule('UpdateAgent LabManager {#AppVersion}', AppExe, 'UpdateAgent LabManager (versioned rule)');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { InstallDelete/[Files]가 끝난 뒤에 설정을 복원해야 덮어써지지 않는다. }
    if LabManagerConfigRestoreNeeded then
    begin
      ForceDirectories(ExtractFileDir(LabManagerConfigFile));
      if not CopyFile(LabManagerConfigBackup, LabManagerConfigFile, True) then
        RaiseException('기존 LabManager appsettings.json 복원에 실패해 설치를 중단합니다.');
      DeleteFile(LabManagerConfigBackup);
      LabManagerConfigRestoreNeeded := False;
    end;

    ApplyFirewallRules;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillProcess('UpdateAgent.LabManager.Desktop.exe');
    KillProcess('UpdateAgent.LabManager.exe');
    RunHidden(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name=all group="' + FirewallGroup + '"');
  end;
end;

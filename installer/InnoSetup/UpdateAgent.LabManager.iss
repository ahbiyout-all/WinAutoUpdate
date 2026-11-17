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
  #define AppVersion "0.1.0"
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
AppPublisherURL=https://github.com/AhBiYout-all/UpdateAgent
AppSupportURL=https://github.com/AhBiYout-all/UpdateAgent/issues
AppUpdatesURL=https://github.com/AhBiYout-all/UpdateAgent/releases
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

[Icons]
Name: "{group}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"
Name: "{group}\UpdateAgent LabManager 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\UpdateAgent LabManager"; Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UpdateAgent.LabManager.Desktop.exe"; WorkingDir: "{app}"; Description: "UpdateAgent LabManager 실행"; Flags: postinstall nowait skipifsilent

[Code]
var
  OldInnoUninstall: String;

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
  UninstallExe, ConfigFile, BackupFile: String;
  ConfigSaved: Boolean;
begin
  Result := '';
  KillProcess('UpdateAgent.LabManager.Desktop.exe');
  KillProcess('UpdateAgent.LabManager.exe');

  { 이전 제거 프로그램이 appsettings.json도 삭제하므로 먼저 백업한다. }
  ConfigFile := ExpandConstant('{app}\appsettings.json');
  BackupFile := AddBackslash(GetTempDir) + 'UpdateAgent.LabManager.appsettings.json.bak';
  ConfigSaved := False;
  if FileExists(ConfigFile) then
  begin
    ConfigSaved := CopyFile(ConfigFile, BackupFile, False);
    if not ConfigSaved then
      Log('Failed to back up existing LabManager appsettings.json');
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
  end;

  if ConfigSaved then
  begin
    ForceDirectories(ExtractFileDir(ConfigFile));
    if not CopyFile(BackupFile, ConfigFile, False) then
      Log('Failed to restore LabManager appsettings.json');
    DeleteFile(BackupFile);
  end;
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
    ApplyFirewallRules;
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

# MSI 패키징 설계 (학생 PC 에이전트)

> 상태: **구현 완료, 상승된 VM/실 PC 검증 대기**. MSI 빌드와 ICE 검증은 통과했습니다.
> 범위: Phase 0 파일럿 이후, 학생 PC 에이전트를 `install-service.ps1` 대신 MSI로 배포하는 것.
>
> **권장 설치 방식은 Inno Setup입니다.** 이 문서의 MSI는 대안으로 유지됩니다.
> 개별 파일(Unpacked Multi-File) 설치, 무인 설치, 방화벽 사전 등록, 프로그램 추가/제거 등록은
> [`INSTALLER.md`](INSTALLER.md)를 참고하세요. 두 설치 파일은 같은 AppID/폴더 규칙을 공유하지 않으며,
> MSI가 설치된 PC에 Inno Setup을 실행하면 MSI 제품을 자동 제거한 뒤 전환합니다.

## 1. 목표와 범위

### 목표

- 학생 PC에 **관리자 권한으로 한 번에 설치/업그레이드/제거**할 수 있는 MSI를 만든다.
- 토큰 같은 비밀값은 MSI에 넣지 않고, 설치 후 랩별 설정 파일로 주입한다.
- 현재 파일럿 설치(`C:\ProgramData\UpdateAgent\app`)와 데이터(`C:\ProgramData\UpdateAgent`)를 잃지 않고 이전한다.
- Maestro Network 7의 파일 복원/보호 기능과 충돌하지 않도록 설치 절차를 문서화한다.

### 이번 단계 포함

| 항목 | 내용 |
|---|---|
| 패키지 | `UpdateAgent.Agent` MSI 1개 |
| 설치 대상 | 학생 PC (Windows 10/11, Server 2016–2025, x64) |
| 설치 방식 | 최소 UI 또는 `msiexec /qn` 무인 설치 + PowerShell 래퍼 |
| 설정 | `%ProgramData%\UpdateAgent\agent.json` |
| 서비스 | `UpdateAgentService` (LocalSystem, 자동 시작) |
| CLI | 선택 기능 (`ADDLOCAL=CliFeature`) |

### 이번 단계 제외

| 항목 | 이유 / 대안 |
|---|---|
| LabManager MSI | 현재 콘솔 앱 + `%LOCALAPPDATA%` 저장. 서비스화·설정 재설계가 선행되어야 함 (아래) |
| MSI 제작 UI | 래퍼가 대화형 설치를 담당. MSI는 무인 전용 |
| 기능 업데이트 / ISO 캐시 | Phase 2 |
| Maestro 자동 설정 | 앱은 Maestro 설정을 변경하지 않는다는 원칙 유지 |
| 원격 일괄 배포 자동화 | 1차는 USB/공유 폴더 + 래퍼. GPO/Intune은 2차 |
| 코드 서명 인증서 발급 | 인증서가 없으면 파일럿은 미서명으로 진행하고 경고 표시 |

### LabManager MSI를 2단계로 미루는 이유

| 선행 작업 | 현재 상태 |
|---|---|
| Windows 서비스/예약 작업 전환 | 콘솔 앱, 교사가 직접 실행 |
| 데이터 위치 | `%LOCALAPPDATA%\UpdateAgent\LabManager` → ProgramData 필요 |
| 방화벽 규칙 | `allow-labmanager.ps1` 별도 실행 |
| 설정/토큰 | `appsettings.json` 직접 수정 |
| 대시보드 재시작/자동 시작 | 수동 |

교사 PC는 랩당 1대이므로 파일럿 단계에서는 현재 런북으로 충분합니다. 에이전트 MSI가 검증된 뒤 같은 방식(서비스화 → ProgramData → MSI)으로 진행합니다.

## 2. 현재 방식과 MSI의 차이

| 항목 | 현재 (`install-service.ps1`) | MSI |
|---|---|---|
| 설치 위치 | `C:\ProgramData\UpdateAgent\app\service` | `C:\Program Files\UpdateAgent\Service` |
| 설정 파일 | `appsettings.json` (URL/토큰 포함) | `appsettings.json`(비밀 없음) + `%ProgramData%\UpdateAgent\agent.json` |
| 설치 주체 | PowerShell 스크립트 | Windows Installer + 래퍼 |
| 업그레이드 | 폴더 삭제 후 재복사 | Major Upgrade (서비스 중지 → 교체 → 시작) |
| 제거 | `uninstall-service.ps1` | `msiexec /x` (데이터는 보존) |
| 서비스 복구 | `sc.exe failure` + `failureflag` | `util:ServiceConfig` + 래퍼의 `failureflag` |
| 무결성 | 파일 복사 | MSI 자체 무결성 + (선택) 서명 |

## 3. 결정 요약 (권장안)

| # | 결정 | 권장안 | 비고 |
|---|---|---|---|
| 1 | 패키저 | **WiX Toolset 5.0.2** (`WixToolset.Sdk`) | v5+ 내장 `<Files>` 하베스팅 사용, Heat 불필요 |
| 2 | 패키지 범위 | **에이전트 MSI만** | LabManager는 2단계 |
| 3 | 아키텍처 | **per-machine, x64** | 32비트 OS 미지원 |
| 4 | 런타임 | **self-contained** (`win-x64`) | 학생 PC에 .NET 설치 불필요 |
| 5 | 설치 위치 | **Program Files** | 코드는 읽기 전용, 데이터와 분리 |
| 6 | 가변 설정 | **`%ProgramData%\UpdateAgent\agent.json`** | 업그레이드/제거에도 보존 |
| 7 | 설치 진입점 | **`scripts\install-agent-msi.ps1`** | `msiexec /qn` + 설정 주입 + 서비스 시작 |
| 8 | MSI UI | **최소 UI** (`WixUI_Minimal` + 한국어 안내) | 기능/경로 선택 없음. `/qn` 무인도 지원 |
| 9 | CLI | **선택 기능 (Level 2)** | 기본 미설치, `ADDLOCAL=CliFeature` |
| 10 | 커스텀 액션 | **없음** | 실패 플래그만 래퍼에서 처리 |
| 11 | Maestro | 설치/업그레이드 전 **수동 모드** 필수 | 래퍼가 `-ConfirmMaestro` 요구 |
| 12 | 서명 | 가능하면 **exe + MSI 모두 서명** | 없으면 파일럿은 미서명 + 경고 |

> **WiX 버전 선택 이유:** WiX v6부터 Open Source Maintenance Fee(OSMF)가 적용되고, v7은 EULA 동의를 강제합니다. 연 매출 $10,000을 넘는 조직은 OSMF 후원 의무가 있으므로, 학교/조직의 라이선스 검토 없이 도입하지 않도록 **OSMF가 적용되지 않는 마지막 버전인 WiX 5.0.2**를 사용합니다. v6/v7이 필요하면 조직 차원의 OSMF 동의를 먼저 받습니다.

## 4. 설치 레이아웃

```text
C:\Program Files\UpdateAgent\
  Service\
    UpdateAgent.Service.exe
    UpdateAgent.Service.dll
    ... (self-contained 런타임, deps)
    appsettings.json          ← MSI가 관리, 업그레이드 시 교체, 비밀 없음
  SessionHelper\
    UpdateAgent.SessionHelper.exe
    ... (self-contained 런타임, deps)
  Desktop\
    UpdateAgent.Agent.Desktop.exe
    ... (self-contained 런타임, deps)
  Cli\                        ← 선택 기능
    UpdateAgent.Cli.exe
    ...

C:\ProgramData\UpdateAgent\    ← MSI가 소유하지 않음 (제거해도 보존)
  agent.json                  ← 래퍼가 생성/갱신. 토큰 포함. ACL 제한
  agent.db                    ← 서비스가 생성
  resume-marker.json          ← 서비스가 생성
  logs\
    service-YYYYMMDD.log
```

### ACL

| 경로 | ACL | 이유 |
|---|---|---|
| `C:\Program Files\UpdateAgent` | 기본값 (Users: RX, Administrators/SYSTEM: F) | 코드 보호 |
| `agent.json` | `Administrators:(F)`, `SYSTEM:(R)` | 토큰 노출 최소화. 서비스는 읽기만 필요 |
| `C:\ProgramData\UpdateAgent` | 기본 상속 (SYSTEM/Administrators F) | 서비스가 DB/마커/로그 생성 |
| `C:\ProgramData\UpdateAgent\logs` | 기본 상속 | 운영자가 로그 확인 |

> 학생 계정이 로컬 관리자인 환경에서는 `Administrators` 그룹에 속하므로 토큰을 완전히 숨길 수 없습니다. 이는 기존 파일럿과 동일한 알려진 제한입니다.

> MSI는 `C:\ProgramData\UpdateAgent`를 **만들지 않습니다.** 래퍼(설치 시)와 서비스(최초 실행 시)가 만듭니다. MSI가 만들면 제거 시 데이터까지 지울 위험이 있습니다.

## 5. MSI 설계

### 5-1. 제품 식별자

| 항목 | 값 | 비고 |
|---|---|---|
| `Name` | `UpdateAgent Agent` | ARP 표시 이름 |
| `Manufacturer` | `AhBiYout` | 작성자/게시자와 일치 |
| `UpgradeCode` | `{58D1BA8F-C2D1-4A36-B944-063870B8F361}` | **첫 배포 후 절대 변경 금지** |
| `ProductCode` | `*` (빌드 시 자동 생성) | 빌드마다 새 GUID |
| `Version` | `$(var.ProductVersion)` | `Directory.Build.props`의 `<Version>`에서 변환 |
| `Scope` | `perMachine` | 관리자 설치, 모든 사용자 |
| `Language` | `1042` (한국어) | UI가 없으므로 정보성 |
| `InstallerVersion` | `500` | Windows Installer 5.0 이상 |

`UpgradeCode`는 랩 전체가 같은 값을 써야 업그레이드가 됩니다. MSI를 새로 만들 때도 이 값을 재사용합니다.

### 5-2. 기능(Feature)

| Feature | Level | 내용 |
|---|---|---|
| `AgentFeature` | 1 | 서비스 + SessionHelper + 상태 확인 데스크톱 앱 (기본 설치) |
| `CliFeature` | 2 | CLI 도구 (선택) |

- 기본: `AgentFeature`만 설치.
- CLI 필요 시: `msiexec /i ... ADDLOCAL=CliFeature`.
- `Level=0`으로 두면 `ADDLOCAL=ALL`로도 설치되지 않으므로 2를 사용합니다.

### 5-3. 파일 수집

WiX v5+ 내장 `<Files>`로 게시 폴더를 그대로 하베스팅합니다. Heat/`heat.exe`는 사용하지 않습니다.

```xml
<ComponentGroup Id="ServiceFiles" Directory="ServiceDir">
  <Files Include="$(var.PublishRoot)\agent\service\**">
    <Exclude Files="$(var.PublishRoot)\agent\service\UpdateAgent.Service.exe" />
    <Exclude Files="$(var.PublishRoot)\agent\service\appsettings.json" />
    <Exclude Files="$(var.PublishRoot)\agent\service\appsettings.Development.json" />
    <Exclude Files="$(var.PublishRoot)\agent\service\**\*.pdb" />
  </Files>

  <!-- 서비스는 수동 작성: ServiceInstall이 키 경로 파일과 같은 컴포넌트에 있어야 함 -->
  <Component Id="AgentServiceComponent" Guid="*">
    <File Source="$(var.PublishRoot)\agent\service\UpdateAgent.Service.exe" KeyPath="yes" />
    <File Source="$(var.PublishRoot)\agent\service\appsettings.json" />
    <ServiceInstall ... />
    <ServiceControl ... />
  </Component>
</ComponentGroup>
```

- `UpdateAgent.Service.exe`는 `ServiceInstall` 때문에 하베스팅에서 제외하고 수동 컴포넌트로 넣습니다.
- `appsettings.json`은 빌드 시 **정제된 템플릿**으로 교체한 뒤 패키징합니다 (6-5).
- `.pdb`, `appsettings.Development.json`은 배포하지 않습니다.
- SessionHelper, 상태 확인 데스크톱 앱, CLI는 각각 `SessionHelperDir`, `DesktopDir`, `CliDir` 루트로 하베스팅합니다.

### 5-4. 서비스 등록

```xml
<ServiceInstall Id="AgentServiceInstall"
                Name="UpdateAgentService"
                DisplayName="UpdateAgent Service"
                Description="Windows Update automation agent for computer labs"
                Type="ownProcess"
                Start="auto"
                ErrorControl="normal"
                Account="LocalSystem"
                Arguments="" />

<ServiceControl Id="AgentServiceControl"
                Name="UpdateAgentService"
                Stop="both"
                Remove="uninstall"
                Start="install"
                Wait="yes" />
```

- `Stop="both"`: 설치(업그레이드)와 제거 시 서비스를 중지합니다.
- `Remove="uninstall"`: 제거 시 서비스 등록을 삭제합니다.
- `Start="install"`: 파일 교체 후 서비스를 시작합니다.
- `Wait="yes"`: 중지/시작 완료를 기다립니다.
- 서비스 실행 파일은 `ServiceInstall`이 포함된 컴포넌트의 키 경로여야 합니다.

### 5-5. 서비스 복구 동작

```xml
<!-- Package 요소에 xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util" 선언 필요 -->
<util:ServiceConfig FirstFailureActionType="restart"
                    SecondFailureActionType="restart"
                    ThirdFailureActionType="restart"
                    RestartServiceDelayInSeconds="60"
                    ResetPeriodInDays="1" />
```

`util:` 확장을 쓰려면 wixproj에서 Util 확장 패키지를 참조해야 합니다.

```xml
<PackageReference Include="WixToolset.Util.wixext" Version="5.0.2" />
```

- 크래시(서비스 프로세스 비정상 종료) 시 60초 후 자동 재시작.
- `util:ServiceConfig`는 `SERVICE_FAILURE_ACTIONS`를 설정하지만 **`failureflag`(비크래시 실패에도 복구 적용)는 설정하지 못합니다.**
- v1은 커스텀 액션을 추가하지 않고, 래퍼가 설치 마지막에 아래를 실행합니다.

```powershell
sc.exe failureflag UpdateAgentService 1
```

> MSI만 설치하는 원격 배포(GPO/Intune)에서는 `failureflag`가 빠질 수 있습니다. 2단계에서 지연 커스텀 액션 또는 별도 구성 스크립트로 보완합니다.

### 5-6. 설치 조건

```xml
<Launch Condition="VersionNT64" Message="UpdateAgent requires 64-bit Windows." />
<Launch Condition="VersionNT &gt;= 1000" Message="UpdateAgent requires Windows 10 or later." />
```

- Windows 10 22H2, Windows 11, Server 2016–2025를 모두 포함합니다 (`VersionNT = 1000`).
- Server Core에는 대화형 데스크톱이 없으므로 SessionHelper(WPF)가 표시되지 않습니다. 설치/서비스는 허용하고, 오버레이는 기존과 같이 동작하지 않습니다.
- 레거시 설치(`C:\ProgramData\UpdateAgent\app`)가 있어도 MSI 설치 자체는 막지 않습니다. 마이그레이션은 래퍼가 담당합니다 (11장).

### 5-7. 업그레이드 / 다운그레이드 / 복구

```xml
<MajorUpgrade Schedule="afterInstallInitialize"
              DowngradeErrorMessage="A newer version of UpdateAgent Agent is already installed." />
<MediaTemplate EmbedCab="yes" />
```

- 같은 `UpgradeCode`의 이전 버전을 자동 제거한 뒤 새 버전을 설치합니다.
- `afterInstallInitialize`라서 이전 제품의 `ServiceControl`이 먼저 서비스를 중지합니다.
- 다운그레이드는 차단됩니다.
- **같은 버전으로 다시 빌드한 MSI**는 업그레이드가 아니라 유지 관리 모드로 들어갑니다. 파일럿 중 재빌드 시에는 버전을 올리거나 먼저 제거합니다.
- 복구: `msiexec /fomus "UpdateAgent.Agent-x.y.z-x64.msi" /qn`.

### 5-8. 제거

- `ServiceControl`이 서비스를 중지/삭제합니다.
- `Program Files\UpdateAgent` 파일과 폴더를 제거합니다.
- `%ProgramData%\UpdateAgent`(DB, 마커, 로그, `agent.json`)는 **보존**합니다.
- 데이터까지 지우려면 별도 스크립트를 사용합니다.

```powershell
.\scripts\uninstall-agent-msi.ps1              # MSI 제거, 데이터 보존
.\scripts\uninstall-agent-msi.ps1 -RemoveData  # 데이터까지 삭제
```

### 5-9. 설치 UI

`WixUI_Minimal`을 사용합니다. 환영/안내 + 진행 + 완료만 있는 가장 단순한 UI이며, 설치 경로와 기능 선택은 노출하지 않습니다.

```xml
<ui:WixUI Id="WixUI_Minimal" />
<WixVariable Id="WixUILicenseRtf" Value="payload\notice.ko.rtf" />
```

- `notice.ko.rtf`는 사용권 계약이 아니라 **설치 전 확인 안내**입니다. 다음 내용을 포함합니다.
  - 관리자 권한 필요
  - 설치 전 Maestro를 수동 복구/보호 해제로 전환
  - 설치 후 현재 상태 저장 + 자동 복구 재설정
  - 업데이트/재부팅 가능성
  - 토큰/URL은 설치 후 래퍼가 설정한다는 점
- `Language="1042"`(한국어)로 빌드합니다. WiX 5.0.2 UI 확장은 en-US 문자열만 포함하므로,
  `installer\UpdateAgent.Agent.Installer\ko-KR.wxl`에서 필요한 String Id를 한국어로 재정의합니다.
  - 번역 대상: `WixUI_Minimal`에서 도달 가능한 다이얼로그(WelcomeEula, Progress, Exit, Cancel, Fatal, FilesInUse, Maintenance*, VerifyReady, Resume, OutOf*Disk, WaitForCosting) + 공용 단추 + 주요 `ProgressText*`.
  - `{\WixUI_Font_*}` 접두사는 다이얼로그 서식이므로 그대로 유지합니다.
  - MSI Control Text에서 `[...]`는 속성 참조로 해석됩니다. 안내 문구의 인용은 `[설치]` 대신 `&quot;설치&quot;`처럼 적습니다(ICE03 방지).
  - `.wxl`의 `Codepage="949"`가 MSI 요약 정보 코드페이지를 결정합니다. 실제 값: `949`, Template `x64;1042`.
- `ko-KR.wxl`이 있으면 WiX 출력이 `bin\x64\Release\ko-KR\` 하위 폴더로 이동합니다.
  `build-msi.ps1`은 하위 폴더까지 검색해 `artifacts\msi`로 복사하므로 경로를 직접 다룰 필요는 없습니다.
- CLI는 UI에서 선택할 수 없습니다. 필요하면 명령줄에서 `ADDLOCAL=CliFeature`를 사용합니다.
- 무인 배포는 기존처럼 `/qn`을 사용합니다.

```xml
<PackageReference Include="WixToolset.UI.wixext" Version="5.0.2" />
```

## 6. 설정과 비밀

### 6-1. 두 파일의 역할

| 파일 | 소유 | 비밀 포함 | 업그레이드 | 제거 |
|---|---|---|---|---|
| `C:\Program Files\UpdateAgent\Service\appsettings.json` | MSI | 아니오 | 교체됨 | 삭제됨 |
| `%ProgramData%\UpdateAgent\agent.json` | 래퍼/운영자 | **예 (토큰)** | 보존됨 | 보존됨 |

### 6-2. `agent.json` 스키마

```json
{
  "Agent": {
    "LabManagerUrl": "http://192.168.0.10:5080",
    "AgentToken": "<agent-token>",
    "SessionHelperPath": "C:\\Program Files\\UpdateAgent\\SessionHelper\\UpdateAgent.SessionHelper.exe",
    "OverlayEnabled": true,
    "DryRun": false
  }
}
```

- 나머지 값(`PollIntervalSeconds`, `MaxCommandsPerCheckIn`, `ResumeOnStartup`, `OverlayCompletionHoldSeconds`, `CheckInEnabled`)은 `appsettings.json` 기본값을 사용합니다.
- 필요할 때만 `agent.json`에 추가로 넣습니다.
- `SessionHelperPath`는 명시적으로 기록합니다. 누락 시 코드가 `..\SessionHelper\UpdateAgent.SessionHelper.exe`를 찾습니다.

### 6-3. 설정 로드 순서

목표 우선순위:

```text
appsettings.json  <  agent.json  <  환경 변수  <  명령줄 인수
```

`Program.cs`에서 `Host.CreateApplicationBuilder(args)` 직후 다음을 추가합니다.

```csharp
var machineConfigPath = UpdateAgentConstants.ConfigFilePath; // %ProgramData%\UpdateAgent\agent.json
builder.Configuration
    .AddJsonFile(machineConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);
```

- `agent.json`이 없으면 기존과 동일하게 동작합니다.
- 환경 변수/명령줄을 다시 추가하는 이유는, 기본 빌더가 이미 추가한 소스보다 `agent.json`이 뒤에 오기 때문입니다. 마지막 소스가 이깁니다.
- `agent.json`은 서비스 시작 시 한 번만 읽습니다. 변경 후 서비스를 재시작해야 반영됩니다.

### 6-4. 토큰 취급 원칙

- 토큰을 `msiexec` 명령줄, MSI 속성, MSI 로그에 **절대 넣지 않습니다.**
- 래퍼는 토큰 파일(UTF-8 BOM 허용) 또는 `UPDATEAGENT_AGENT_TOKEN` 환경 변수에서 읽습니다. 토큰 값을 명령줄 매개변수로 받지 않습니다.
- `agent.json`은 같은 폴더의 임시 파일에 쓴 뒤 원자적으로 교체합니다.
- ACL은 `icacls`에 SID를 사용해 한국어/영어 OS 모두에서 동작하게 합니다.

```powershell
icacls $configPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)"
```

- 기본 토큰(`change-me-*`)은 MSI에 포함하지 않습니다(템플릿은 빈 값). 빌드 스크립트가 기본 토큰 잔존을 감지하면 중단하고, 설치 래퍼도 기본값을 감지하면 경고합니다.
- 토큰 교체: 동일 래퍼를 `-AgentTokenFile`과 함께 다시 실행하거나, 새 `configure-agent.ps1`(6-6)을 사용합니다.

### 6-5. 설치 시 기본 설정

MSI에 들어가는 `appsettings.json`은 빌드 시 정제된 템플릿으로 교체합니다.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "System.Net.Http.HttpClient": "Warning"
    }
  },
  "Agent": {
    "LabManagerUrl": "",
    "AgentToken": "",
    "PollIntervalSeconds": 30,
    "CheckInEnabled": true,
    "OverlayEnabled": true,
    "OverlayCompletionHoldSeconds": 20,
    "ResumeOnStartup": true,
    "MaxCommandsPerCheckIn": 20,
    "DryRun": false
  }
}
```

- `LabManagerUrl`이 비어 있으면 `RemoteEnabled = false`가 되어 원격 체크인을 하지 않습니다.
- 토큰이 비어 있어도 서비스는 시작되며, 래퍼가 `agent.json`을 쓴 뒤 재시작합니다.
- 개발/파일럿용 `src\UpdateAgent.Service\appsettings.json`은 그대로 두고, MSI 빌드 단계에서만 템플릿을 덮어씁니다.

### 6-6. `configure-agent.ps1` 변경

현재 스크립트는 설치 방식을 자동 감지합니다.

| 상황 | 대상 | 동작 |
|---|---|---|
| `C:\Program Files\UpdateAgent\Service\UpdateAgent.Service.exe` 존재 | `%ProgramData%\UpdateAgent\agent.json` | 없으면 생성, `Agent` 섹션만 갱신 |
| MSI가 없고 레거시 `appsettings.json` 존재 | `...\app\service\appsettings.json` | 경고 후 레거시 모드 |
| `-Legacy` 또는 `-InstallRoot` 지정 | 레거시 `appsettings.json` | 강제 레거시 모드 |
| `-ConfigPath` 지정 | 지정한 파일 | 파일명으로 모드 추정 |

- 두 모드 모두 ACL 재적용과 `-RestartService`를 지원합니다.
- MSI 모드에서는 파일이 없으면 새로 만듭니다.

## 7. 필요한 코드/스크립트 변경

| 파일 | 변경 | 검증 |
|---|---|---|
| `src\UpdateAgent.Service\Program.cs` | `agent.json` 구성 소스 추가 | 우선순위 수동 확인 |
| `src\UpdateAgent.Service\Configuration\AgentOptions.cs` | SessionHelper 경로 fallback `..\SessionHelper\...` 추가 | 단위 테스트 |
| `src\UpdateAgent.Shared\UpdateAgentConstants.cs` | `ConfigFilePath` 추가, `AgentVersion`을 어셈블리 버전에서 계산 | 단위 테스트 |
| `src\UpdateAgent.Shared\Models\JobModels.cs` | `AgentVersion` 기본값 하드코딩 제거 | 빌드 |
| `scripts\configure-agent.ps1` | `agent.json` 대상으로 전환 | 임시 폴더 테스트 |
| `scripts\install-agent-msi.ps1` | 신규 래퍼 | VM 테스트 |
| `scripts\uninstall-agent-msi.ps1` | 신규 제거 스크립트 | VM 테스트 |
| `installer\...` | WiX 프로젝트/패키지 | `wix msi validate` |
| `scripts\build-msi.ps1` | 빌드/서명/해시 | 산출물 확인 |
| `README.md`, `docs\PILOT.md` | MSI 절차 링크/대체 | 리뷰 |

`AgentVersion` 권장 구현:

```csharp
// using System.Reflection;
public static string AgentVersion { get; } =
    (typeof(UpdateAgentConstants).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "0.0.0")
    .Split('+')[0];
```

## 8. 빌드 파이프라인

### 8-1. 프로젝트 구조

```text
installer\
  UpdateAgent.Agent.Installer\
    UpdateAgent.Agent.Installer.wixproj
    Package.wxs
    ko-KR.wxl                 ← WixUI 한국어 번역
    payload\
      appsettings.json        ← 정제된 템플릿
      notice.ko.rtf           ← 설치 전 확인 안내 (WixUI)
artifacts\
  staging\                    ← build-msi.ps1이 만드는 임시 게시물
  msi\
    UpdateAgent.Agent-0.1.24-x64.msi
    UpdateAgent.Agent-0.1.24-x64.msi.sha256
    build-info.json
```

`UpdateAgent.Agent.Installer.wixproj` 골격:

```xml
<Project Sdk="WixToolset.Sdk/5.0.2">
  <PropertyGroup>
    <OutputType>Package</OutputType>
    <Platform>x64</Platform>
    <ProductVersion Condition="'$(ProductVersion)' == ''">0.1.24</ProductVersion>
    <PublishRoot Condition="'$(PublishRoot)' == ''">$(MSBuildThisFileDirectory)..\..\publish</PublishRoot>
    <DefineConstants>ProductVersion=$(ProductVersion);PublishRoot=$(PublishRoot)</DefineConstants>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="WixToolset.Util.wixext" Version="5.0.2" />
    <PackageReference Include="WixToolset.UI.wixext" Version="5.0.2" />
  </ItemGroup>
</Project>
```

### 8-2. `build-msi.ps1` 흐름

1. `publish\agent\service`, `publish\agent\sessionhelper`, `publish\cli` 존재 확인.
2. `-ProductVersion` 검증 (`Major.Minor.Build`, 숫자 3자리).
3. `artifacts\staging`에 `publish`와 같은 구조로 게시물 복사 (원본 `publish`는 건드리지 않음).
4. `installer\...\payload\appsettings.json`을 스테이징의 서비스 `appsettings.json`에 덮어씀.
5. `.pdb`, `appsettings.Development.json` 제거.
6. (선택) 스테이징된 exe/dll 서명.
7. `dotnet build -t:Rebuild`로 MSI 생성.
8. `dotnet tool restore` 후 `dotnet wix msi validate -sice ICE60` 실행.
9. `artifacts\msi\`로 복사, (선택) MSI 서명, SHA-256 기록.
10. `build-info.json`에 버전/커밋/빌드 시각/해시 기록.

### 8-3. 명령 예시

```powershell
.\scripts\publish.ps1
.\scripts\build-msi.ps1 -ProductVersion 0.1.24
```

```powershell
# build-msi.ps1이 publish를 artifacts\staging으로 복사한 뒤 실행하는 명령
dotnet build .\installer\UpdateAgent.Agent.Installer\UpdateAgent.Agent.Installer.wixproj `
  -c Release `
  -t:Rebuild `
  -p:Platform=x64 `
  -p:ProductVersion=0.1.24 `
  -p:PublishRoot="$PWD\artifacts\staging"

# 검증 (저장소 루트에서)
dotnet tool restore
dotnet wix msi validate -sice ICE60 .\artifacts\msi\UpdateAgent.Agent-0.1.24-x64.msi
```

### 8-4. 버전 규칙

- `Directory.Build.props`의 `<Version>`이 단일 원본입니다.
- MSI `ProductVersion`은 **숫자 3자리**(`0.1.0`)만 허용합니다. `-beta`, `+commit` 같은 접미사는 제거합니다.
- 새 MSI를 배포할 때는 최소 `Build` 자리를 올립니다. 그래야 Major Upgrade가 동작합니다.
- `UpgradeCode`는 고정, `ProductCode`는 빌드마다 자동 생성.

### 8-5. 산출물 크기

self-contained 게시물이 약 413MB이므로, CAB 압축 후 MSI는 대략 150–220MB가 될 것으로 예상했습니다.
실측 결과 **82.2MB** (게시물 304.8MB 스테이징, 853개 파일)이며, USB 배포에 충분합니다.

### 8-6. 재현성

- `WixToolset.Sdk/5.0.2`은 NuGet에서 복원합니다. 오프라인 빌드가 필요하면 패키지를 내부 피드에 캐시합니다.
- `wix msi validate`는 SDK 빌드가 아니라 **`wix` .NET 도구**가 필요합니다. 저장소의 `.config/dotnet-tools.json`(.NET 10 도구 매니페스트)에 `wix` 5.0.2을 고정하고 `dotnet tool restore` 후 `dotnet wix msi validate ...`로 실행합니다.
- .NET 호스트 exe는 언어가 중립(Language=0)이라 ICE60 경고가 발생합니다. 실제 결함이 아니므로 wixproj의 `SuppressIces`와 검증 명령의 `-sice ICE60`으로 억제합니다.
- `build-msi.ps1`은 `-t:Rebuild`로 빌드해, 게시물 타임스탬프가 오래되어도 이전 MSI를 재사용하지 않도록 합니다.
- 빌드 산출물에 커밋 해시를 기록해 어떤 소스로 만든 MSI인지 추적합니다.
- `publish.ps1`은 여전히 단일 게시 원본이며, MSI는 그 출력을 소비합니다.

## 9. 서명

권장 순서:

1. 게시된 exe/dll 서명 (`signtool sign /fd SHA256 ...`).
2. MSI 빌드.
3. MSI 서명 (`signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 ...`).
4. `signtool verify /pa /all`로 확인.

- 서명은 스테이징 단계에서 수행하고, MSI에는 서명된 파일이 들어가야 합니다.
- 인증서가 없으면 파일럿은 미서명으로 진행할 수 있지만, SmartScreen/백신 경고와 “알 수 없는 게시자” 표시를 감수해야 합니다.
- 자체 서명 인증서를 쓰는 경우 학생 PC의 “신뢰할 수 있는 게시자” 저장소에 등록해야 합니다. 랩 이미지/GPO로 배포하는 방법은 2단계에서 다룹니다.
- MSI 서명은 패키지 변조 방지에 도움이 되지만, Windows Installer가 기본적으로 서명을 강제하지는 않습니다. WDAC/AppLocker 정책이 있는 랩에서는 별도 허용 규칙이 필요합니다.

## 10. 설치 / 업그레이드 / 제거 절차

### 10-1. `install-agent-msi.ps1` (신규)

| 매개변수 | 필수 | 설명 |
|---|---|---|
| `-LabManagerUrl` | 최초 설치 시 | `http://<교사-PC-IP>:5080`. 기존 `agent.json`이 있으면 생략 가능 |
| `-AgentTokenFile` | 최초 설치 시 | BOM 허용 토큰 파일. `UPDATEAGENT_AGENT_TOKEN` 환경 변수로 대체 가능. 기존 `agent.json`이 있으면 생략 가능 |
| `-MsiPath` | 아니오 | 기본: `artifacts\msi\`에서 최신 MSI |
| `-DryRun` | 아니오 | 에이전트 DRY RUN 모드 |
| `-DisableOverlay` | 아니오 | 오버레이 비활성화 |
| `-ConfirmMaestro` | 권장 | Maestro 보호를 수동 모드로 전환했음을 확인 |
| `-SkipStart` | 아니오 | 서비스 시작 생략 (점검용) |
| `-Quiet` | 아니오 | `/qn` 무인 설치. 기본은 최소 UI 표시 |
| `-LogPath` | 아니오 | msiexec 로그 경로 |

\* 최초 설치에서는 토큰을 `-AgentTokenFile` 또는 `UPDATEAGENT_AGENT_TOKEN` 중 하나로 제공해야 합니다. 업그레이드에서 생략하면 기존 `agent.json` 값을 유지합니다.

동작 순서:

1. 관리자 권한 확인.
2. Maestro 설치 감지. 감지되면 `-ConfirmMaestro` 없이는 중단하고 안내.
3. 기존 레거시 설치 감지 시 마이그레이션 안내 (11장).
4. `msiexec /i "<msi>"` 실행. `-Quiet`면 `/qn`, 아니면 최소 UI를 표시하고 `/norestart /l*v "<log>"`를 추가.
   기존 Inno 등록 정보가 남았지만 제거 프로그램 파일이 없으면 경고하고 제거를 건너뛴 뒤 설치를 계속합니다.
5. 종료 코드 확인: `0`, `3010`, `1641` 성공으로 처리 (`3010`은 재부팅 권장).
6. 토큰이 제공된 경우에만 BOM-safe로 읽고, 기존 `agent.json` 값과 병합해 원자적으로 기록.
7. `agent.json` ACL 적용.
8. `sc.exe failureflag UpdateAgentService 1`.
9. 서비스 시작/재시작, 3초 후 상태 확인.
10. 로그 경로와 대시보드 확인 안내 출력.

예시:

```powershell
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content ".\pilot-agent-token.txt" -Raw
.\scripts\install-agent-msi.ps1 `
  -LabManagerUrl "http://192.168.0.10:5080" `
  -ConfirmMaestro
```

### 10-2. 수동 `msiexec`

```powershell
# 최소 UI (수동 설치)
msiexec /i ".\UpdateAgent.Agent-0.1.24-x64.msi" /norestart /l*v ".\install.log"

# 무인 설치
msiexec /i ".\UpdateAgent.Agent-0.1.24-x64.msi" /qn /norestart /l*v ".\install.log"

# 무인 + CLI
msiexec /i ".\UpdateAgent.Agent-0.1.24-x64.msi" /qn /norestart ADDLOCAL=CliFeature
```

- 토큰/URL은 이 명령으로 설정되지 않습니다. 설치 후 래퍼 또는 `configure-agent.ps1`을 실행해야 원격 관리가 활성화됩니다.
- MSI만 설치하면 서비스는 시작되지만 `LabManagerUrl`이 비어 있어 원격 체크인을 하지 않습니다.

### 10-3. 업그레이드

```powershell
.\scripts\install-agent-msi.ps1 -LabManagerUrl "http://192.168.0.10:5080" -ConfirmMaestro
```

- 새 MSI의 버전이 높으면 자동으로 이전 버전을 제거하고 교체합니다.
- `agent.json`은 보존되므로 토큰을 다시 넣지 않아도 됩니다. 래퍼는 기존 `agent.json`이 있으면 URL/토큰을 유지하고 전달된 값만 갱신합니다.
- 업그레이드 전에 Maestro 보호를 수동 모드로 전환해야 합니다.

### 10-4. 제거

```powershell
msiexec /x ".\UpdateAgent.Agent-0.1.24-x64.msi" /qn /norestart /l*v ".\uninstall.log"
```

또는:

```powershell
.\scripts\uninstall-agent-msi.ps1              # 데이터 보존
.\scripts\uninstall-agent-msi.ps1 -RemoveData  # agent.json, DB, 마커, 로그 삭제
```

### 10-5. 원격 배포 (2단계)

- GPO/Intune/PDQ에서 MSI를 배포할 수 있습니다. 단, 토큰 주입은 별도 단계가 필요합니다.
- 랩별로 동일한 에이전트 토큰을 사용하므로, 배포 도구의 “스크립트 후처리”로 `agent.json`을 배포하는 방식을 검토합니다.
- 토큰이 포함된 파일을 네트워크 공유에 두지 말고, 배포 도구의 비밀 저장 기능을 사용합니다.

## 11. 레거시 마이그레이션

현재 파일럿 설치(`install-service.ps1`)에서 MSI로 이전하는 절차입니다.

1. Maestro를 **수동 복구/보호 해제**로 전환하고 스크린샷을 남깁니다.
2. 서비스 중지: `Stop-Service UpdateAgentService`.
3. 기존 설정 백업:
   `Copy-Item C:\ProgramData\UpdateAgent\app\service\appsettings.json .\legacy-appsettings.json`
4. 기존 `appsettings.json`에서 `LabManagerUrl`, `AgentToken`, `OverlayEnabled`, `DryRun` 값을 읽습니다.
5. MSI 설치 + `agent.json` 생성:
   `.\scripts\install-agent-msi.ps1 -LabManagerUrl ... -AgentTokenFile ... -ConfirmMaestro`
6. 확인:
   - `Get-Service UpdateAgentService` → `Running`
   - 서비스 경로가 `C:\Program Files\UpdateAgent\Service\UpdateAgent.Service.exe`
   - 로그의 `Content root path`가 `...\Service\`
   - 대시보드에 90초 내 온라인
7. 정상 확인 후 레거시 앱 폴더만 삭제:
   `Remove-Item C:\ProgramData\UpdateAgent\app -Recurse -Force`
   (`agent.db`, `resume-marker.json`, `logs`는 유지)
8. Maestro에서 **현재 상태 저장 → 자동 복구 재설정** 후 대시보드에서 **상태 저장 + 보호 재활성화 확인** 기록.

롤백: MSI 제거 후 3번 백업과 `publish\agent` 게시물로 `install-service.ps1`을 다시 실행합니다.

## 12. Maestro 운영 주의

- 설치/업그레이드 전에 Maestro를 **매 부팅 자동 복구 → 수동 복구/해제**로 전환합니다.
- 전환하지 않으면 재부팅 시 Maestro가 `Program Files`의 서비스 파일/등록을 복원 전 상태로 되돌릴 수 있습니다.
- 설치 후 Maestro에서 **현재 상태 저장 → 자동 복구 재설정**을 수행하고, 대시보드에서 **상태 저장 + 보호 재활성화 확인**을 기록합니다.
- 래퍼는 Maestro가 감지되면 `-ConfirmMaestro`를 요구하고, 없으면 중단합니다.
- 앱은 Maestro 설정/서비스/WU 기능을 변경하지 않습니다. 이 원칙은 MSI에서도 동일합니다.
- Maestro WU가 켜져 있으므로, 작업 시작 전 WU 사용 중 감지 로직은 그대로 유지합니다.

## 13. 테스트 매트릭스

실제 MSI 테스트는 **관리자 권한이 있는 VM/테스트 PC**에서 수행합니다.

### 설치

| # | 시나리오 | 기대 결과 |
|---|---|---|
| 1 | 깨끗한 Win11 x64에 무인 설치 | 서비스 Running, 파일/서비스 경로 정상 |
| 2 | `ADDLOCAL=CliFeature` | `Cli\UpdateAgent.Cli.exe` 존재, `help` 동작 |
| 3 | MSI만 설치 (agent.json 없음) | 서비스 시작, 원격 체크인 안 함, 오류 없음 |
| 4 | 래퍼로 설치 | `agent.json` 생성, ACL 제한, 90초 내 온라인 |
| 5 | 32비트 OS | 설치 차단 + 메시지 |
| 6 | 비관리자 실행 | 권한 오류로 중단 |
| 7 | 한국어 경로/사용자 이름 | 설치/서비스 정상 |
| 8 | Server Core | 설치/서비스 정상, 오버레이 없음 |

### 업그레이드/복구/제거

| # | 시나리오 | 기대 결과 |
|---|---|---|
| 9 | 0.1.0 → 0.1.1 업그레이드 | 서비스 중지/교체/시작, `agent.json` 유지 |
| 10 | 같은 버전 재설치 | 유지 관리 모드 또는 안내대로 처리 |
| 11 | 다운그레이드 시도 | 차단 + 메시지 |
| 12 | `msiexec /fomus` 복구 | 파일/서비스 복구 |
| 13 | 제거 | 서비스/파일 삭제, ProgramData 보존 |
| 14 | `-RemoveData` 제거 | ProgramData까지 삭제 |
| 15 | 업그레이드 중 재부팅 | 재부팅 후 서비스 자동 시작, 작업 재개 |

### 설정/보안/운영

| # | 시나리오 | 기대 결과 |
|---|---|---|
| 16 | 잘못된 토큰 | LabManager 401, 서비스는 계속 실행 |
| 17 | 토큰 교체 | `agent.json` 갱신 + 재시작 후 정상 체크인 |
| 18 | `agent.json` ACL | Administrators F, SYSTEM R, 상속 해제 |
| 19 | MSI 로그 검사 | 토큰 문자열 없음 |
| 20 | 서비스 크래시 | 60초 후 자동 재시작 |
| 21 | `failureflag` 확인 | `sc.exe qfailureflag` 결과 1 |
| 22 | 레거시 마이그레이션 | 데이터 유지, 새 경로로 체크인 |
| 23 | Maestro 보호 중 설치 | 래퍼가 중단/경고 |
| 24 | `dotnet wix msi validate -sice ICE60` | ICE 오류 0 (허용 예외 문서화) |

### UI

| # | 시나리오 | 기대 결과 |
|---|---|---|
| U1 | 대화형 설치 | 한국어 안내/진행/완료 표시, 경로·기능 선택 없음 |
| U2 | UI에서 취소 | 설치/서비스 변경 없음 |
| U3 | `-Quiet` / `/qn` | UI 없이 설치, 동일한 결과 |

빌드 시점 확인(자동, 관리자 권한 불필요):

- MSI `Control` 테이블의 다이얼로그 문구가 한국어인지 확인했습니다 (예: `ProgressDlg.TitleInstalling = {\WixUI_Font_Title}[ProductName] 설치 중`).
- MSI 요약 정보 코드페이지 `949`, Template `x64;1042`, `ProductLanguage=1042`를 확인했습니다.
- 실제 화면 렌더링(U1~U3)은 상승된 VM/실 PC에서 확인해야 합니다.

### 파일럿 전 추가 확인

- [ ] `dotnet test` 전체 통과 (165개)
- [ ] `dotnet build UpdateAgent.sln` 경고 0/오류 0
- [ ] MSI SHA-256 기록
- [ ] 서명 검증 (`signtool verify /pa /all`)
- [ ] 제거 후 재설치 반복 3회
- [ ] 실제 Windows Update 1회 설치 + 재부팅/재개

## 14. 위험과 제한

| 위험 | 영향 | 완화 |
|---|---|---|
| Maestro 자동 복구가 설치 파일을 되돌림 | 서비스/파일 유실 | 설치 전 수동 모드, 래퍼 확인 |
| 토큰이 MSI 로그/명령줄에 노출 | 인증 정보 유출 | 속성/명령줄 사용 금지, 로그 검사 |
| `failureflag` 누락 (MSI 단독 설치) | 비크래시 실패 시 재시작 안 됨 | 래퍼에서 설정, 2단계 CA 검토 |
| 같은 버전 재빌드 | 업그레이드 대신 유지 관리 모드 | 배포마다 Build 버전 증가 |
| self-contained 용량 | 배포 시간/저장 공간 | CAB 압축, 필요 시 FDD 옵션 |
| 미서명 MSI | SmartScreen/백신 경고 | 인증서 확보, 파일럿 경고 |
| 레거시 `configure-agent.ps1` | 잘못된 파일 수정 | MSI 모드로 전환, 레거시 모드 명시 |
| Server Core 오버레이 | WPF 미동작 | 세션 없음 감지, 오버레이 생략 |
| LabManager는 여전히 수동 실행 | 교사 PC 재부팅 후 대시보드 없음 | 2단계에서 서비스화 |

## 15. 확정된 결정

| 항목 | 결정 |
|---|---|
| 패키저 | WiX Toolset **5.0.2** (`WixToolset.Sdk` + Util/UI 확장) |
| 런타임 | **self-contained** (`win-x64`) |
| 설정 위치 | Program Files(코드) + `%ProgramData%\UpdateAgent\agent.json`(비밀) |
| 설치 UI | **최소 UI** (`WixUI_Minimal` + 한국어 안내), `/qn` 무인 지원 |
| CLI | **선택 기능** (Level 2, `ADDLOCAL=CliFeature`) |
| 서명 | 파일럿은 **미서명**, 랩 롤아웃 전 인증서 확보 |

구현 기본값:

- Maestro가 감지되면 래퍼는 `-ConfirmMaestro` 없이는 중단합니다.
- `installer` 프로젝트는 `UpdateAgent.sln`에 추가하지 않고 `scripts\build-msi.ps1`로 직접 빌드합니다. 일반 `dotnet build UpdateAgent.sln`에 WiX SDK 복원을 강제하지 않기 위해서입니다.
- `configure-agent.ps1`는 MSI 설치를 감지하면 `agent.json`을 수정합니다.

## 16. 구현 상태와 남은 작업

| # | 항목 | 상태 |
|---|---|---|
| 1 | 코드 변경 (`agent.json`, `AgentVersion`, SessionHelper 폴백) + 단위 테스트 | 완료 (테스트 165개 통과) |
| 2 | WiX 프로젝트 + `Package.wxs` | 완료 |
| 3 | 정제된 `appsettings.json` 템플릿, `notice.ko.rtf` | 완료 |
| 3-1 | `ko-KR.wxl` 한국어 WixUI 번역 (Control 테이블 검증) | 완료 |
| 4 | `scripts\build-msi.ps1` (스테이징/버전/서명 옵션/ICE/해시/build-info) | 완료. 0.1.0 MSI 82.2MB 생성, ICE 오류 0 |
| 5 | `scripts\install-agent-msi.ps1`, `scripts\uninstall-agent-msi.ps1` | 작성 완료, 상승된 환경 테스트 필요 |
| 6 | `configure-agent.ps1`의 `agent.json`/레거시 자동 감지 | 완료 |
| 7 | `docs\PILOT.md`, `README.md` MSI 절차 | 완료 |
| 8 | 13장 테스트 매트릭스 (설치/업그레이드/제거/마이그레이션/UI) | **대기** — 관리자 권한 VM/실 PC 필요 |
| 9 | 실제 학생 PC 1대 Phase 0 파일럿 재실행 | **대기** |

알려진 빌드 환경 특이사항:

- WiX 5.0.2는 NuGet에서 자동 복원됩니다. 오프라인 빌드는 내부 피드 캐시가 필요합니다.
- 검증 도구는 `.config/dotnet-tools.json`으로 고정되어 있습니다 (`dotnet tool restore`).
- ICE60은 .NET 호스트 exe의 언어 중립 때문에 억제합니다.
- `ko-KR.wxl` 때문에 MSI는 `installer\...\bin\x64\Release\ko-KR\`에 생성됩니다. `build-msi.ps1`이 찾아서 `artifacts\msi`로 복사합니다.
- WiX 프로젝트 출력 경로를 직접 확인할 때는 위 하위 폴더를 포함하세요.

## 참고

- WiX `<Files>`: https://docs.firegiant.com/wix/schema/wxs/files/
- WiX `<ServiceInstall>`: https://docs.firegiant.com/wix/schema/wxs/serviceinstall/
- WiX `util:ServiceConfig`: https://docs.firegiant.com/wix/schema/util/serviceconfig/
- WiX `MajorUpgrade`: https://docs.firegiant.com/wix/schema/wxs/majorupgrade/
- MSI 검증: `dotnet wix msi validate -sice ICE60 <file.msi>`

# UpdateAgent

컴퓨터실(랩) 환경을 위한 Windows Update 자동화 에이전트 + 교사용 관리 서버입니다.

- 교사 PC에서 랩 전체(약 50대)의 업데이트 작업을 한 번에 시작합니다.
- 교사용 관리 콘솔은 시스템에 설치된 **Edge 또는 Chrome의 `--app=` 단독 창 모드**로 실행됩니다. 주소창/툴바가 없는 별도 창이며, WebView2는 사용하지 않습니다.
- 학생 PC는 서비스로 동작하며, 업데이트 설치·재부팅·재개를 스스로 이어서 수행합니다.
- 작업 중에는 선택적으로 전체화면 입력 잠금 오버레이와 진행률을 표시합니다.
- Maestro Network 7은 **수동 모드**로만 연동합니다. 앱이 Maestro 설정을 변경하지 않습니다.
- 기능 업데이트(OS 업그레이드)는 MVP에서 제외되어 있으며 2단계에서 다룹니다.

## 아키텍처

```
[교사 PC]                                        [학생 PC × N]
UpdateAgent.LabManager.Desktop (런처)               UpdateAgent.Service (SYSTEM)
  - Edge/Chrome 감지 후 --app= 단독 창 실행            - WUA 스캔/다운로드/설치
  - 창 없이 종료, 서버는 계속 실행                    - 재부팅/재개 상태 머신
        │ 로컬 HTTP API                             - Maestro 마커 검증
        ▼                                           - 원격 명령 실행
UpdateAgent.LabManager (ASP.NET Core) <─ HTTP 체크인 ─ └─ UpdateAgent.SessionHelper (WPF)
  - wwwroot 대시보드 + Minimal API                     전체화면 오버레이 + 진행률
  - SQLite (작업 원본)
  - Wake-on-LAN
  - 토큰 인증

런처(`UpdateAgent.LabManager.Desktop.exe`)는 창을 띄우지 않고 로컬 API 서버를 준비한 뒤
Edge를 우선, 없으면 Chrome을 찾아 `--app=http://127.0.0.1:5080/` 창을 엽니다.
서버가 없으면 같은 폴더의 `UpdateAgent.LabManager.exe --headless`를 자동 실행합니다.
앱 모드 창을 닫아도 서버는 계속 실행되어 학생 PC 체크인을 유지합니다.
전용 브라우저 프로필(`%LOCALAPPDATA%\UpdateAgent\LabManager\browser-profile`)을 사용하므로
일반 브라우저 창/탭과 섞이지 않으며, 관리자 토큰은 해당 프로필의 localStorage에 저장됩니다.

| 경로 | 설명 |
|---|---|
| `src/UpdateAgent.Shared` | DTO, 정책, 작업 상태, Maestro/오버레이 모델 |
| `src/UpdateAgent.Core` | WUA 연동, 정책 평가, 사전 점검, SQLite 저장소, 재부팅/재개, Maestro 수동 검증, 오버레이 파이프 |
| `src/UpdateAgent.Service` | 학생 PC Windows 서비스 (SYSTEM). 체크인/텔레메트리/원격 명령 |
| `src/UpdateAgent.SessionHelper` | 사용자 세션에 표시되는 전체화면 진행 오버레이 (WPF) |
| `src/UpdateAgent.LabManager` | 교사 PC 웹 대시보드(wwwroot) + HTTP API + SQLite + WoL |
| `src/UpdateAgent.LabManager.Desktop` | 교사 PC 런처. Edge/Chrome 감지 후 `--app=` 창으로 대시보드 실행 |
| `src/UpdateAgent.Cli` | 로컬 CLI (scan/preflight/run/status/maestro/pause/cancel) |
| `tests/UpdateAgent.Tests` | xUnit 단위 테스트 |
| `desktop/tauri` | 독립 실험용 Tauri 2 스캐폴드. LabManager/인스톨러/릴리스에는 포함되지 않음 |
| `installer/InnoSetup` | Inno Setup 6 설치 스크립트 (권장, 학생/교사 PC) |
| `installer/UpdateAgent.Agent.Installer` | WiX MSI 패키지 (대안, 학생 PC 에이전트) |
| `scripts/` | 파일럿 게시/설치 프로그램/설정/서비스 설치·제거/방화벽 스크립트 |
| `docs/INSTALLER.md` | Inno Setup 설치 프로그램 설계·무인 설치·체크리스트 |
| `docs/RELEASE.md` | 버전 관리·GitHub 릴리스·자동 업데이트 운영 |
| `docs/WORKLOG.md` | 날짜별 작업 로그와 검증 결과 |
| `docs/PILOT.md` | Phase 0 실 PC 파일럿 런북 |
| `docs/MSI.md` | MSI 패키징 설계·구현 상태·검증 매트릭스 |
| `docs/AI-WORK-INSTRUCTIONS.md` | AI 에이전트용 범용 작업 지시서·자아성찰 검증 게이트 |

## 지원 범위

- Windows 11 Home / Pro / Enterprise / IoT / LTSC
- Windows 10 22H2 (ESU 포함)
- Windows Server 2016–2025 (Server Core는 헤드리스로만 동작, 오버레이 없음)
- 작업 그룹 환경 (도메인 불필요)
- 학생 계정이 로컬 관리자인 환경을 전제로 합니다.

## 요구 사항

### 빌드 PC

- .NET SDK 10.0.4xx ( `global.json` 참고)
- Windows 10/11 또는 Windows Server 2016+

### 학생 PC

- .NET 10 Desktop Runtime (프레임워크 종속 배포 시)
- 관리자 권한 (WUA 설치)
- 서비스는 `LocalSystem` 계정으로 실행

### 교사 PC

- .NET 10 ASP.NET Core Runtime + .NET 10 Desktop Runtime(WPF 런처) (프레임워크 종속 배포 시)
- Microsoft Edge 또는 Google Chrome (앱 모드 창)
- TCP 5080 인바운드 허용 (학생 PC → 교사 PC)
- 관리자 토큰은 전용 브라우저 프로필의 localStorage에 저장됩니다.
- 학생 PC의 MAC 주소 사전 등록 (Wake-on-LAN 사용 시)

## 빌드 및 테스트

```powershell
dotnet build UpdateAgent.sln
dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj
```

현재 기준: 빌드 경고 0 / 오류 0, 테스트 89개 통과.

MSI는 솔루션 빌드에 포함되지 않습니다(일반 빌드에 WiX 복원을 강제하지 않기 위해).
설치 프로그램이 필요하면 `publish.ps1` 후 `build-inno.ps1`(권장) 또는 `build-msi.ps1`을 실행합니다.

에이전트만 빠르게 빌드하려면 저장소 루트의 `build.bat`을 더블클릭하거나 실행합니다.
`Directory.Build.props`의 버전을 읽어 설치 파일 이름에 버전을 붙이고, 버전별 폴더에 모은 뒤 탐색기를 엽니다.

```text
build\agent\<버전>\
  UpdateAgent.Agent-Setup-<버전>-x64.exe
  UpdateAgent.Agent-Setup-<버전>-x64.exe.sha256
  build-info.json
```

> .NET apphost(`UpdateAgent.Service.exe`, `UpdateAgent.Cli.exe` 등)의 파일 이름은 직접 바꾸지 않습니다.
> apphost는 내부 DLL 이름을 찾기 때문에 이름을 바꾸면 실행되지 않습니다. 버전은 설치 파일 이름으로 관리합니다.

## 파일럿 키트

실 PC 파일럿에 필요한 스크립트와 런북이 포함되어 있습니다. 상세 절차는 [`docs/PILOT.md`](docs/PILOT.md)를 참고하세요.
설치 프로그램 설계와 무인 설치 옵션은 [`docs/INSTALLER.md`](docs/INSTALLER.md),
버전/릴리스/자동 업데이트 운영은 [`docs/RELEASE.md`](docs/RELEASE.md),
작업 이력은 [`docs/WORKLOG.md`](docs/WORKLOG.md)에 있습니다.
MSI 패키징 설계와 제약은 [`docs/MSI.md`](docs/MSI.md)에 있습니다.

| 스크립트 | 실행 위치 | 설명 |
|---|---|---|
| `build.bat` | 개발 PC | 에이전트만 게시 → Inno 설치 파일 빌드 → `build\agent\<버전>\`에 모아 탐색기 열기 |
| `scripts/publish.ps1` | 개발 PC | LabManager/Service/SessionHelper/CLI 게시 (`-AgentOnly`로 학생 PC용만) |
| `scripts/build-inno.ps1` | 개발 PC | 게시물 스테이징 → Inno Setup 설치 파일 2개 빌드/검증/해시 (`-AgentOnly` 지원, **권장**) |
| `scripts/build-msi.ps1` | 개발 PC | 게시물 스테이징 → WiX MSI 빌드/ICE 검증/해시 (대안) |
| `scripts/configure-labmanager.ps1` | 교사 PC | 관리자/에이전트 토큰, 수신 URL 설정 |
| `scripts/allow-labmanager.ps1` | 교사 PC | TCP 5080 인바운드 방화벽 규칙 (Private/Domain) |
| `scripts/install-agent-msi.ps1` | 학생 PC | MSI 설치 + `agent.json` 설정 + 서비스 시작 (대안) |
| `scripts/uninstall-agent-msi.ps1` | 학생 PC | MSI 제거, 필요 시 데이터 삭제 |
| `scripts/install-service.ps1` | 학생 PC | 레거시 스크립트 설치 (서비스+오버레이) |
| `scripts/configure-agent.ps1` | 학생 PC | MSI `agent.json` 또는 레거시 `appsettings.json` 변경 |
| `scripts/uninstall-service.ps1` | 학생 PC | 레거시 서비스 제거, 필요 시 파일/데이터 삭제 |
| `scripts/github-push.ps1` | 개발 PC | 버전 올림 → 빌드/테스트 → 비밀값 검사 → 커밋/태그/push (**로컬 전용, Git 제외**) |

> `scripts/github-push.ps1`과 `scripts/github-push.bat`은 `.gitignore`에서 제외되어
> 저장소에 업로드되지 않습니다.

```powershell
# 개발 PC — 에이전트만 빌드 (버전별 폴더 + 탐색기 열기)
build.bat

# 개발 PC — 전체 게시 + 설치 파일
.\scripts\publish.ps1
.\scripts\build-inno.ps1         # artifacts\inno\*-Setup-<버전>-x64.exe (권장)
.\scripts\build-msi.ps1          # artifacts\msi\UpdateAgent.Agent-<버전>-x64.msi (대안)

# 교사 PC (관리자 PowerShell)
.\scripts\configure-labmanager.ps1 -AdminTokenFile <파일> -AgentTokenFile <파일>
.\scripts\allow-labmanager.ps1

# 학생 PC (관리자 PowerShell, Inno Setup 권장)
.\UpdateAgent.Agent-Setup-<버전>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /LabManagerUrl=http://<교사-PC-IP>:5080 /AgentTokenFile=<에이전트-토큰-파일>

# 학생 PC (관리자 PowerShell, MSI 대안)
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content <에이전트-토큰-파일> -Raw
.\scripts\install-agent-msi.ps1 -MsiPath <MSI 경로> -LabManagerUrl "http://<교사-PC-IP>:5080" -ConfirmMaestro

# 학생 PC (레거시 스크립트)
.\scripts\install-service.ps1 -LabManagerUrl "http://<교사-PC-IP>:5080"
```

> `publish.ps1`은 기본적으로 self-contained(`win-x64`)로 게시합니다. .NET 런타임이 설치된 환경에서는 `-FrameworkDependent`로 크기를 줄일 수 있습니다.
> 토큰은 명령줄 기록에 남지 않도록 환경 변수(`UPDATEAGENT_AGENT_TOKEN`, `UPDATEAGENT_ADMIN_TOKEN`) 또는 파일/프롬프트 입력을 사용하세요.
> **게시를 다시 실행하면 `appsettings.json`이 기본값으로 돌아갑니다.** 게시 후 configure/install 스크립트를 다시 실행하세요.

게시 결과:

```text
publish\labmanager
publish\agent\service
publish\agent\sessionhelper
publish\cli
```

학생 PC에서는 `publish\cli\UpdateAgent.Cli.exe`로 사전 점검과 상태 확인을 할 수 있습니다.
서비스가 실행 중일 때는 `scan`, `preflight`, `status`, `maestro`만 사용하고,
작업 제어(`run`/`resume`/`pause`/`cancel`)는 LabManager 대시보드를 사용하세요.

## 실행

### 1. 교사 PC — LabManager (브라우저 앱 모드)

```powershell
# API 서버만 먼저 띄우고 싶을 때
dotnet run --project src/UpdateAgent.LabManager
# 기본 주소: http://0.0.0.0:5080, 대시보드: http://localhost:5080
```

> 기본 토큰(`change-me-*`)으로는 서버가 시작되지 않습니다. 먼저
> `configure-labmanager.ps1`을 실행하거나 `LabManager__AdminToken` /
> `LabManager__AgentToken` 환경 변수를 설정하세요. 로컬 개발에서만
> `LabManager__AllowDefaultTokens=true`를 사용할 수 있습니다.

- 데이터베이스: `%LOCALAPPDATA%\UpdateAgent\LabManager\labmanager.db`
- 로그: 같은 폴더의 `logs\`
- 관리자 토큰은 앱 모드 브라우저 프로필의 localStorage에 저장됩니다(첫 실행 시 입력).
- 앱 모드 창을 닫아도 서버는 계속 실행됩니다.

실제 배포용으로는 파일럿 키트를 사용합니다.

```powershell
.\scripts\publish.ps1
# publish\labmanager\appsettings.json 토큰 설정
.\scripts\configure-labmanager.ps1 -AdminTokenFile <파일> -AgentTokenFile <파일>

Set-Location .\publish\labmanager
.\UpdateAgent.LabManager.Desktop.exe
# 서버가 없으면 런처가 UpdateAgent.LabManager.exe --headless를 자동 시작합니다.
# Edge를 우선 감지하고, 없으면 Chrome을 찾아 주소창/툴바 없는 --app= 창을 엽니다.
```

### 2. 학생 PC — Service

개발용(콘솔에서 바로 실행, DRY RUN):

```powershell
$env:Agent__LabManagerUrl = "http://192.168.0.10:5080"
$env:Agent__DryRun = "true"
$env:Agent__OverlayEnabled = "false"
dotnet run --project src/UpdateAgent.Service
```

운영용 설치는 관리자 PowerShell에서 **Inno Setup 설치 파일**을 사용하는 것을 권장합니다.
설치 파일은 개별 파일(Unpacked Multi-File) 구조로 `C:\Program Files\UpdateAgent`에 배치하고,
서비스 등록과 방화벽 규칙(기본 이름/버전 이름)을 설치 단계에서 조용히 처리합니다.

```powershell
# 학생 PC (관리자 PowerShell, 무인 설치)
.\UpdateAgent.Agent-Setup-<버전>-x64.exe `
  /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /LabManagerUrl=http://<교사-PC-IP>:5080 `
  /AgentTokenFile=<에이전트-토큰-파일>
```

토큰은 명령줄에 직접 쓰지 않고 `/AgentTokenFile`로 전달합니다.
설치 파일의 전체 옵션과 체크리스트는 [`docs/INSTALLER.md`](docs/INSTALLER.md)를 참고하세요.

MSI는 대안으로 유지됩니다. MSI는 서비스(`C:\Program Files\UpdateAgent\Service`)와 오버레이 헬퍼
(`...\SessionHelper`)를 설치하고, 래퍼가 `%ProgramData%\UpdateAgent\agent.json`에
`Agent:SessionHelperPath`를 포함한 설정을 기록합니다.

```powershell
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content <에이전트-토큰-파일> -Raw
.\scripts\install-agent-msi.ps1 `
  -MsiPath <MSI 경로> `
  -LabManagerUrl "http://<교사-PC-IP>:5080" `
  -ConfirmMaestro
```

레거시 스크립트 설치는 `install-service.ps1`을 사용하며,
`C:\ProgramData\UpdateAgent\app` 아래에 배치하고 `appsettings.json`을 수정합니다.
세 방식 모두 `scripts\configure-agent.ps1`로 URL/토큰/오버레이/DRY RUN을 변경할 수 있습니다.

### 3. 로컬 CLI

관리자 PowerShell에서 실행합니다.

```powershell
dotnet run --project src/UpdateAgent.Cli -- scan
dotnet run --project src/UpdateAgent.Cli -- preflight --confirm-maestro
dotnet run --project src/UpdateAgent.Cli -- run --confirm-maestro
dotnet run --project src/UpdateAgent.Cli -- status
dotnet run --project src/UpdateAgent.Cli -- maestro
```

게시된 CLI를 사용하면 .NET SDK 없이도 실행할 수 있습니다.

```powershell
.\publish\cli\UpdateAgent.Cli.exe preflight --confirm-maestro
.\publish\cli\UpdateAgent.Cli.exe scan
.\publish\cli\UpdateAgent.Cli.exe status
```

| 명령 | 설명 |
|---|---|
| `scan` | WUA 검색 결과를 정책 기준으로 선택/제외 표시 (`--json`, `--optional`, `--drivers`) |
| `preflight` | 관리자 권한·디스크·전원·네트워크·WSUS·Maestro·BitLocker·WU 사용 여부 점검 |
| `run` | 업데이트 작업 시작 (`--dry-run`으로 실제 설치 없이 흐름 검증) |
| `resume` | 재부팅 후 남은 작업 재개 |
| `status` | 현재/최근 작업 상태 |
| `maestro` | Maestro 감지 및 수동 체크리스트 출력 |
| `pause` / `cancel` | 진행 중 작업 일시중지/취소 |

`run` 옵션: `--max-reboots N`, `--optional`, `--drivers`, `--pause-on-user`, `--no-overlay`,
`--reboot-never`, `--shutdown`, `--dry-run`, `--confirm-maestro`

## 릴리스와 자동 업데이트

버전의 단일 소스는 `Directory.Build.props`의 `<Version>`입니다.
GitHub 저장소(`ahbiyout-all/WinAutoUpdate`)의 최신 릴리스를 조회해 새 버전이 있으면
LabManager 대시보드에 업데이트 팝업을 표시합니다. 설치 파일은 릴리스 자산에서만 받습니다.

```powershell
# 로컬 릴리스: 버전 올림 -> 빌드/테스트 -> 비밀값 검사 -> 커밋/태그/push
.\scripts\github-push.ps1 -Bump patch

# 태그(v*) push 시 GitHub Actions가 설치 파일을 빌드해 Release에 첨부
```

- 릴리스 자산은 Inno Setup 설치 파일 2개(`*Setup*.exe`)와 MSI 1개, 총 3개만 허용합니다.
- SHA-256은 릴리스 노트에 기록합니다.
- 상세 절차와 문제 해결: [`docs/RELEASE.md`](docs/RELEASE.md)

## 설정

우선순위: 명령줄 인수 > 환경 변수 > `%ProgramData%\UpdateAgent\agent.json` (MSI) > `appsettings.json` > 코드 기본값.

### LabManager (`LabManager` 섹션)

| 키 | 기본값 | 설명 |
|---|---|---|
| `AdminToken` | `change-me-admin-token` | 관리자 API/대시보드 토큰 |
| `AgentToken` | `change-me-agent-token` | 에이전트 체크인 토큰 |
| `DatabasePath` | `%LOCALAPPDATA%\UpdateAgent\LabManager\labmanager.db` | SQLite 경로 |
| `OnlineThresholdSeconds` | `90` | 온라인 판정 기준 |
| `WakePorts` | `[9, 7]` | WoL 매직 패킷 포트 |
| `WakeRepeatCount` | `3` | WoL 반복 횟수 |
| `Urls` | `http://0.0.0.0:5080` | 수신 주소 |

### Service (`Agent` 섹션)

| 키 | 기본값 | 설명 |
|---|---|---|
| `LabManagerUrl` | `http://localhost:5080` | 교사 PC 주소 |
| `AgentToken` | `change-me-agent-token` | LabManager와 동일해야 함 |
| `PollIntervalSeconds` | `30` | 체크인 주기 (최소 15초) |
| `CheckInEnabled` | `true` | `false`면 로컬 전용 |
| `OverlayEnabled` | `true` | 오버레이 사용 여부 |
| `OverlayCompletionHoldSeconds` | `20` | 완료 후 오버레이 유지 시간 |
| `SessionHelperPath` | (비어 있음) | SessionHelper 경로 |
| `ResumeOnStartup` | `true` | 시작 시 남은 작업 자동 재개 |
| `MaxCommandsPerCheckIn` | `20` | 체크인당 최대 명령 수 |
| `DryRun` | `false` | 실제 설치/재부팅 없이 흐름만 검증 |

환경 변수 예:

```powershell
$env:Agent__LabManagerUrl = "http://192.168.0.10:5080"
$env:Agent__AgentToken = "<강력한-토큰>"
```

데이터 경로:

- 학생 PC: `%ProgramData%\UpdateAgent` (`agent.json`, `agent.db`, `resume-marker.json`, `logs\`)
- MSI 배포에서 비밀값은 `agent.json`에만 두고, Program Files의 `appsettings.json`에는 넣지 않습니다.
- `UPDATEAGENT_STATE_PATH`로 Maestro 마커 보조 경로를 지정할 수 있습니다.

## 보안

- 에이전트 API는 `X-Agent-Token`, 관리자 API는 `X-Admin-Token` 헤더로 인증합니다.
  토큰 비교는 고정 시간 해시 비교를 사용합니다.
- `/api/health`만 인증 없이 공개됩니다.
- **기본 토큰은 파일럿 전 반드시 변경하세요.** 모든 학생 PC에 동일한 에이전트 토큰을 배포합니다.
- LabManager API와 대시보드는 랩 LAN에서만 사용하고 외부에 노출하지 마세요.
- 학생 PC가 로컬 관리자인 환경에서는 완전한 변조 방지가 불가능합니다.
- 배포 전 코드 서명 인증서 적용을 권장합니다.

## Maestro Network 7 수동 워크플로

이 앱은 Maestro의 설정/서비스/Windows Update 기능을 변경하지 않습니다.

1. 작업 전: Maestro에서 **매 부팅 자동 복구 → 수동 복구/해제**로 전환합니다.
2. 대시보드의 작업 시작 창에서 **Maestro 복원 보호 해제 확인**을 체크하고 작업을 시작합니다.
3. 업데이트/재부팅이 끝나면 Maestro에서 **현재 상태 저장 → 자동 복구 재설정**을 수행합니다.
4. 대시보드의 작업 상세에서 **보호 해제 확인**, **상태 저장 + 보호 재활성화 확인**을 기록합니다.

앱은 체크리스트와 마커 파일(`resume-marker.json` + 보조 마커)로 재부팅 후 상태를 검증합니다.
Maestro UI/API 자동화는 2단계로 미뤄져 있습니다.

## Phase 0 PoC 체크리스트

배포 전에 아래 항목을 순서대로 확인합니다. 상세 절차와 롤백 방법은 [`docs/PILOT.md`](docs/PILOT.md)에 있습니다.

- [ ] `dotnet build UpdateAgent.sln` 경고 0/오류 0, `dotnet test` 전체 통과
- [ ] 런처 실행: Edge/Chrome 감지, `--app=` 단독 창, 로컬 서버 자동 시작, 대시보드 로드, 토큰 저장/로드, 창 닫기 후 서버 유지 확인
- [ ] 대표 PC 1대에서 `Cli scan`: 보안/품질 업데이트가 선택되고 드라이버/정의 업데이트는 제외되는지 확인
- [ ] `Cli preflight --confirm-maestro`: 관리자 권한, 디스크, 전원, 네트워크, WSUS, Maestro, WU 사용 여부 점검 결과 확인
- [ ] `Cli run --dry-run`: 스캔 → 설치(모의) → 완료 전이가 정상인지 확인
- [ ] LabManager + Service(DRY RUN) 연동: 에이전트 온라인 등록, 작업 시작, 항목별 진행/감사 로그 확인
- [ ] 시작 명령이 거부되었을 때 서버 작업이 `pending`에 남지 않고 `failed`로 정리되는지 확인
- [ ] 실제 PC 1대 파일럿: Maestro 보호 중지 → 작업 → 재부팅/재개 → 마커 검증 → Maestro 복원
- [ ] 최대 재부팅 횟수(기본 5회)와 사용자 로그온 시 일시중지 동작 확인
- [ ] 오버레이: 전체화면 표시, 진행률 중앙 정렬, 커서 숨김, 단축키 억제, 원격 해제 확인
- [ ] Wake-on-LAN: 교사 PC에서 매직 패킷 전송 후 학생 PC 부팅 확인
- [ ] 자동 종료(`--shutdown`/정책) 동작 확인
- [ ] 학생 PC 2~3대에 서비스 배포, 재부팅 후 자동 시작 및 체크인 확인
- [ ] MSI 설치 → 업그레이드 → 제거 반복, `agent.json` 유지 및 데이터 보존 확인
- [ ] MSI 설치 로그에 평문 토큰이 없는지 확인
- [ ] 토큰 변경 후 기존 토큰이 거부되는지 확인
- [ ] 방화벽 5080 인바운드, 교사 PC 절전/종료 시 동작 정의
- [ ] 결과 기록 및 롤백 절차(서비스 중지/제거, Maestro 자동 복구 재설정) 문서화

## API 요약

인증: 에이전트 `/api/agents/*` → `X-Agent-Token`, 관리자 `/api/admin/*` → `X-Admin-Token`

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/health` | 상태 확인 (인증 없음) |
| POST | `/api/agents/checkin` | 에이전트 체크인/텔레메트리/명령 수신 |
| GET | `/api/admin/overview` | 요약 통계 |
| GET | `/api/admin/agents` | 에이전트 목록 |
| GET | `/api/admin/agents/{id}` | 에이전트 상세 |
| GET/POST | `/api/admin/agents/{id}/commands` | 명령 조회/발행 |
| POST | `/api/admin/agents/{id}/wake` | Wake-on-LAN |
| POST | `/api/admin/agents/{id}/reboot` | 재부팅 |
| POST | `/api/admin/agents/{id}/shutdown` | 종료 |
| POST | `/api/admin/agents/{id}/unlock` | 오버레이 해제 |
| GET | `/api/admin/jobs` | 작업 목록 |
| GET | `/api/admin/jobs/{jobId}` | 작업 상세 (항목/사전점검 포함) |
| POST | `/api/admin/jobs/start` | 작업 시작 |
| POST | `/api/admin/jobs/{jobId}/pause` | 일시중지 |
| POST | `/api/admin/jobs/{jobId}/resume` | 재개 |
| POST | `/api/admin/jobs/{jobId}/cancel` | 취소 |
| GET | `/api/admin/jobs/{jobId}/maestro` | Maestro 체크리스트 |
| POST | `/api/admin/jobs/{jobId}/maestro/protection-suspended` | 보호 중지 확인 |
| POST | `/api/admin/jobs/{jobId}/maestro/restored` | 상태 저장/보호 복원 확인 |
| GET/PUT | `/api/admin/policy` | 업데이트 정책 조회/변경 |
| GET | `/api/admin/audit` | 감사 로그 |
| GET | `/api/admin/update` | GitHub Releases 최신 버전 확인 (`force=true`로 캐시 무시) |

## 현재 제한 사항 / 2단계

- 기능 업데이트(OS 업그레이드)는 설치하지 않습니다. 승인 + WU/ISO 폴백은 2단계입니다.
- Maestro 연동은 수동 체크리스트 + 마커 검증만 지원합니다. 벤더 API/UI 자동화는 2단계입니다.
- MSI 설치 프로그램은 제공되지만, 상승된 VM/실 PC에서 설치·업그레이드·제거 검증이 아직 필요합니다. 랩 일괄 원격 배포는 2단계입니다.
- 다중 랩 통합 대시보드와 외부 원격 접속은 범위 밖입니다.
- 드라이버 업데이트는 기본 제외입니다.

## 라이선스

내부 사용 목적. 별도 라이선스 정책을 정하세요.
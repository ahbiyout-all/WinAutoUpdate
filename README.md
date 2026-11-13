# UpdateAgent

컴퓨터실(랩) 환경을 위한 Windows Update 자동화 에이전트 + 교사용 관리 서버입니다.

- 교사 PC에서 랩 전체(약 50대)의 업데이트 작업을 한 번에 시작합니다.
- 학생 PC는 서비스로 동작하며, 업데이트 설치·재부팅·재개를 스스로 이어서 수행합니다.
- 작업 중에는 선택적으로 전체화면 입력 잠금 오버레이와 진행률을 표시합니다.
- Maestro Network 7은 **수동 모드**로만 연동합니다. 앱이 Maestro 설정을 변경하지 않습니다.
- 기능 업데이트(OS 업그레이드)는 MVP에서 제외되어 있으며 2단계에서 다룹니다.

## 아키텍처

```
[교사 PC]                                   [학생 PC × N]
UpdateAgent.LabManager  <── HTTP 체크인 ──  UpdateAgent.Service (SYSTEM)
  - ASP.NET Core Minimal API                  - WUA 스캔/다운로드/설치
  - SQLite (작업 원본)                        - 재부팅/재개 상태 머신
  - 웹 대시보드 (한국어)                       - Maestro 마커 검증
  - Wake-on-LAN                               - 원격 명령 실행
  - 토큰 인증                                  └─ UpdateAgent.SessionHelper (WPF)
                                                    전체화면 오버레이 + 진행률
```

| 경로 | 설명 |
|---|---|
| `src/UpdateAgent.Shared` | DTO, 정책, 작업 상태, Maestro/오버레이 모델 |
| `src/UpdateAgent.Core` | WUA 연동, 정책 평가, 사전 점검, SQLite 저장소, 재부팅/재개, Maestro 수동 검증, 오버레이 파이프 |
| `src/UpdateAgent.Service` | 학생 PC Windows 서비스 (SYSTEM). 체크인/텔레메트리/원격 명령 |
| `src/UpdateAgent.SessionHelper` | 사용자 세션에 표시되는 전체화면 진행 오버레이 (WPF) |
| `src/UpdateAgent.LabManager` | 교사 PC 관리 서버 + 웹 대시보드 + WoL |
| `src/UpdateAgent.Cli` | 로컬 CLI (scan/preflight/run/status/maestro/pause/cancel) |
| `tests/UpdateAgent.Tests` | xUnit 단위 테스트 |
| `scripts/` | 파일럿 게시/설정/서비스 설치·제거/방화벽 스크립트 |
| `docs/PILOT.md` | Phase 0 실 PC 파일럿 런북 |

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

- .NET 10 ASP.NET Core Runtime (프레임워크 종속 배포 시)
- TCP 5080 인바운드 허용 (학생 PC → 교사 PC)
- 학생 PC의 MAC 주소 사전 등록 (Wake-on-LAN 사용 시)

## 빌드 및 테스트

```powershell
dotnet build UpdateAgent.sln
dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj
```

현재 기준: 빌드 경고 0 / 오류 0, 테스트 40개 통과.

## 파일럿 키트

실 PC 파일럿에 필요한 스크립트와 런북이 포함되어 있습니다. 상세 절차는 [`docs/PILOT.md`](docs/PILOT.md)를 참고하세요.

| 스크립트 | 실행 위치 | 설명 |
|---|---|---|
| `scripts/publish.ps1` | 개발 PC | LabManager/Service/SessionHelper/CLI 게시 |
| `scripts/configure-labmanager.ps1` | 교사 PC | 관리자/에이전트 토큰, 수신 URL 설정 |
| `scripts/allow-labmanager.ps1` | 교사 PC | TCP 5080 인바운드 방화벽 규칙 (Private/Domain) |
| `scripts/install-service.ps1` | 학생 PC | 서비스+오버레이 설치, 설정, ACL, 자동 시작 등록 |
| `scripts/configure-agent.ps1` | 학생 PC | 설치 후 URL/토큰/오버레이/DRY RUN 변경 |
| `scripts/uninstall-service.ps1` | 학생 PC | 서비스 제거, 필요 시 파일/데이터 삭제 |

```powershell
# 개발 PC
.\scripts\publish.ps1

# 교사 PC (관리자 PowerShell)
.\scripts\configure-labmanager.ps1 -AdminTokenFile <파일> -AgentTokenFile <파일>
.\scripts\allow-labmanager.ps1

# 학생 PC (관리자 PowerShell)
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content <에이전트-토큰-파일> -Raw
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
작업 제어(`run`/`resume`/`pause`/`cancel`)는 대시보드를 사용하세요.

## 실행

### 1. 교사 PC — LabManager

```powershell
dotnet run --project src/UpdateAgent.LabManager
# 기본 주소: http://0.0.0.0:5080
# 대시보드:  http://localhost:5080
```

- 데이터베이스: `%LOCALAPPDATA%\UpdateAgent\LabManager\labmanager.db`
- 로그: 같은 폴더의 `logs\`
- 관리자 토큰을 대시보드 우측 상단에 입력하면 localStorage에 저장됩니다.

실제 배포용으로는 파일럿 키트를 사용합니다.

```powershell
.\scripts\publish.ps1
# publish\labmanager\appsettings.json 토큰 설정
.\scripts\configure-labmanager.ps1 -AdminTokenFile <파일> -AgentTokenFile <파일>

Set-Location .\publish\labmanager
.\UpdateAgent.LabManager.exe
```

### 2. 학생 PC — Service

개발용(콘솔에서 바로 실행, DRY RUN):

```powershell
$env:Agent__LabManagerUrl = "http://192.168.0.10:5080"
$env:Agent__DryRun = "true"
$env:Agent__OverlayEnabled = "false"
dotnet run --project src/UpdateAgent.Service
```

운영용 설치는 관리자 PowerShell에서:

```powershell
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content <에이전트-토큰-파일> -Raw
.\scripts\install-service.ps1 -LabManagerUrl "http://<교사-PC-IP>:5080"
```

설치 스크립트가 서비스(`C:\ProgramData\UpdateAgent\app\service`)와 오버레이 헬퍼
(`...\app\sessionhelper`)를 분리 배치하고, `appsettings.json`에
`Agent:SessionHelperPath`를 자동으로 기록합니다. 수동으로 경로를 바꾸려면
`scripts\configure-agent.ps1 -SessionHelperPath <경로>`를 사용하세요.

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

## 설정

우선순위: 환경 변수 > `appsettings.json` > 코드 기본값.

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

- 학생 PC: `%ProgramData%\UpdateAgent` (`agent.db`, `resume-marker.json`, `logs\`)
- `UPDATEAGENT_STATE_PATH`로 Maestro 마커 보조 경로를 지정할 수 있습니다.

## 보안

- 에이전트 API는 `X-Agent-Token`, 관리자 API는 `X-Admin-Token` 헤더로 인증합니다.
  토큰 비교는 고정 시간 해시 비교를 사용합니다.
- `/api/health`만 인증 없이 공개됩니다.
- **기본 토큰은 파일럿 전 반드시 변경하세요.** 모든 학생 PC에 동일한 에이전트 토큰을 배포합니다.
- 대시보드는 랩 LAN에서만 사용하고 외부에 노출하지 마세요.
- 학생 PC가 로컬 관리자인 환경에서는 완전한 변조 방지가 불가능합니다.
- 배포 전 코드 서명 인증서 적용을 권장합니다.

## Maestro Network 7 수동 워크플로

이 앱은 Maestro의 설정/서비스/Windows Update 기능을 변경하지 않습니다.

1. 작업 전: Maestro에서 **매 부팅 자동 복구 → 수동 복구/해제**로 전환합니다.
2. 대시보드 작업 시작 창에서 **Maestro 복원 보호 해제 확인**을 체크하고 작업을 시작합니다.
3. 업데이트/재부팅이 끝나면 Maestro에서 **현재 상태 저장 → 자동 복구 재설정**을 수행합니다.
4. 대시보드 작업 상세에서 **보호 해제 확인**, **상태 저장 + 보호 재활성화 확인**을 기록합니다.

앱은 체크리스트와 마커 파일(`resume-marker.json` + 보조 마커)로 재부팅 후 상태를 검증합니다.
Maestro UI/API 자동화는 2단계로 미뤄져 있습니다.

## Phase 0 PoC 체크리스트

배포 전에 아래 항목을 순서대로 확인합니다. 상세 절차와 롤백 방법은 [`docs/PILOT.md`](docs/PILOT.md)에 있습니다.

- [ ] `dotnet build UpdateAgent.sln` 경고 0/오류 0, `dotnet test` 전체 통과
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

## 현재 제한 사항 / 2단계

- 기능 업데이트(OS 업그레이드)는 설치하지 않습니다. 승인 + WU/ISO 폴백은 2단계입니다.
- Maestro 연동은 수동 체크리스트 + 마커 검증만 지원합니다. 벤더 API/UI 자동화는 2단계입니다.
- MSI 설치 프로그램과 원격 일괄 배포는 아직 없습니다. Phase 0은 수동 게시/서비스 등록으로 진행합니다.
- 다중 랩 통합 대시보드와 외부 원격 접속은 범위 밖입니다.
- 드라이버 업데이트는 기본 제외입니다.

## 라이선스

내부 사용 목적. 별도 라이선스 정책을 정하세요.

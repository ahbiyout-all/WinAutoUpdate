# Phase 0 파일럿 런북 (실 PC 1대)

이 문서는 실제 Windows PC 1대에서 UpdateAgent MVP를 검증하는 절차입니다.
DRY RUN으로 확인할 수 없는 **실제 설치, 재부팅/재개, 오버레이, 서비스 배포, Maestro 마커 검증**을 목표로 합니다.

> 범위: 보안/품질 업데이트만. 드라이버와 기능 업데이트(OS 업그레이드)는 제외됩니다.
> 예상 소요: 1~2시간 (업데이트 다운로드/설치/재부팅 횟수에 따라 달라짐)

## 준비물

| 항목 | 설명 |
|---|---|
| 교사 PC | LabManager를 실행할 PC. 학생 PC와 같은 랩 LAN |
| 학생 PC 1대 | 실제 업데이트를 설치할 테스트 PC. 초기화/복구 가능한 PC 권장 |
| 관리자 계정 | 학생 PC와 교사 PC 모두 로컬 관리자 |
| Maestro Network 7 | 학생 PC에 설치되어 있고 보호가 동작 중 |
| 네트워크 | 학생 PC → 교사 PC TCP 5080 |
| 저장 매체 | publish 결과와 scripts 폴더를 학생 PC로 옮길 USB/공유 폴더 |
| 복구 수단 | 학생 PC 스냅샷/복원 지점 또는 재설치 이미지 |

## 0. 토큰 준비

관리자 토큰과 에이전트 토큰을 생성합니다. **기본값(`change-me-*`)은 파일럿에서도 사용하지 마세요.**
아래 예시는 임시 폴더에 저장합니다. 파일럿 후 삭제하고, 실제 랩에서는 암호 관리 도구를 사용하세요.

```powershell
function New-Token {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes).Replace('+','-').Replace('/','_').TrimEnd('=')
}

$secretDir = Join-Path $env:TEMP "updateagent-pilot"
New-Item -ItemType Directory -Force $secretDir | Out-Null

New-Token | Set-Content "$secretDir\admin-token.txt" -NoNewline
New-Token | Set-Content "$secretDir\agent-token.txt" -NoNewline

"Admin token file: $secretDir\admin-token.txt"
"Agent token file: $secretDir\agent-token.txt"
```

두 토큰은 서로 달라야 하며, 에이전트 토큰은 **모든 학생 PC에 동일하게** 배포합니다.

## 1. 빌드 및 테스트 (개발 PC)

```powershell
Set-Location "<repo>"
dotnet build UpdateAgent.sln
dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj
```

기대 결과: 빌드 경고 0/오류 0, 테스트 89개 통과.

## 2. 게시

```powershell
.\scripts\publish.ps1
```

결과:

```
publish\labmanager
publish\agent\service
publish\agent\sessionhelper
publish\cli
```

기본값은 self-contained(`win-x64`)이므로 학생 PC에 .NET 런타임이 없어도 실행됩니다.
런타임이 이미 설치되어 있고 크기를 줄이려면 `.\scripts\publish.ps1 -FrameworkDependent`를 사용합니다.

> **주의:** `publish.ps1`을 다시 실행하면 `publish\labmanager\appsettings.json`과
> `publish\agent\service\appsettings.json`이 기본값으로 돌아갑니다.
> 게시 후에는 `configure-labmanager.ps1` / `install-service.ps1`을 다시 실행하세요.
> MSI를 쓴다면 `publish.ps1` 직후 `build-msi.ps1`을 다시 실행합니다.
> 이미 설정한 토큰을 잃지 않도록 파일럿 중에는 게시를 반복하지 않는 것이 좋습니다.

## 3. 교사 PC 설정 및 LabManager 실행

`publish\labmanager`, `scripts` 폴더를 교사 PC로 복사합니다. 아래 명령은 복사한 폴더 기준입니다.

### 3-1. 토큰/주소 설정

```powershell
.\scripts\configure-labmanager.ps1 `
  -AdminTokenFile "<admin-token.txt 경로>" `
  -AgentTokenFile "<agent-token.txt 경로>" `
  -ListenUrl "http://0.0.0.0:5080"
```

### 3-2. 방화벽 허용 (관리자 PowerShell)

```powershell
.\scripts\allow-labmanager.ps1 -Port 5080
```

Private/Domain 프로필에서만 TCP 5080 인바운드를 허용합니다.

### 3-3. 교사 PC IP 확인

```powershell
Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.IPAddress -notlike '127.*' } |
  Select-Object IPAddress, InterfaceAlias
```

학생 PC에서 접근할 IP를 기록합니다. (예: `192.168.0.10`)

### 3-4. 실행 (브라우저 앱 모드)

3-1에서 설정한 토큰이 `publish\labmanager\appsettings.json`에 있는지 확인합니다.
기본 토큰(`change-me-*`)이면 서버가 시작되지 않습니다.

`appsettings.json`을 읽도록 **게시 폴더에서** 런처를 실행합니다.

```powershell
Set-Location .\publish\labmanager
.\UpdateAgent.LabManager.Desktop.exe
```

- 런처가 서버가 없으면 `UpdateAgent.LabManager.exe --headless`를 자동 시작합니다.
- Edge를 우선 감지하고, 없으면 Chrome을 찾아 주소창/툴바 없는 `--app=` 창을 엽니다.
- 전용 프로필: `%LOCALAPPDATA%\UpdateAgent\LabManager\browser-profile`
- 관리자 토큰은 해당 프로필의 `localStorage` 키 `ua_admin_token`에 저장됩니다.
- 업데이트 알림 닫기 상태는 `sessionStorage` 키 `ua_dismissed_update_version`에 저장됩니다.
- **앱 모드 창을 닫아도 서버는 계속 실행**되어 학생 PC 체크인을 유지합니다.
  서버를 종료하려면 `UpdateAgent.LabManager.exe` 프로세스를 별도로 종료하세요.
- 대시보드: `http://localhost:5080`
- 원격 접속: `http://<교사-PC-IP>:5080`
- 데이터: `%LOCALAPPDATA%\UpdateAgent\LabManager\labmanager.db`
- 로그: `%LOCALAPPDATA%\UpdateAgent\LabManager\logs\`

대시보드 우측 상단에 **관리자 토큰**을 입력합니다.
서버만 띄우려면 `.\UpdateAgent.LabManager.exe`를 직접 실행해도 됩니다.

## 4. 학생 PC 설치

설치 방법은 **Inno Setup(권장)**, **MSI(대안)**, 기존 스크립트(레거시) 세 가지입니다.
Phase 0 파일럿에서는 Inno Setup을 먼저 검증하고, 문제가 있으면 MSI나 스크립트 방식으로 되돌립니다.
설치 프로그램 전체 안내는 [`INSTALLER.md`](INSTALLER.md)를 참고하세요.

### 4-A. Inno Setup 설치 (권장)

개별 파일(Unpacked Multi-File) 구조로 설치하며, 서비스 등록과 방화벽 규칙
(기본 이름/버전 이름)을 설치 단계에서 조용히 처리합니다.

```powershell
# 개발 PC
.\scripts\publish.ps1
.\scripts\build-inno.ps1
# 산출물:
#   artifacts\inno\UpdateAgent.Agent-Setup-<버전>-x64.exe
#   artifacts\inno\UpdateAgent.LabManager-Setup-<버전>-x64.exe
```

학생 PC에서 **관리자 PowerShell**을 열고 토큰 파일과 함께 무인 설치합니다.

```powershell
Set-Location "<복사한 폴더>"

.\UpdateAgent.Agent-Setup-0.1.2-x64.exe `
  /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /LabManagerUrl=http://<교사-PC-IP>:5080 `
  /AgentTokenFile="<agent-token.txt 경로>"
```

- 토큰은 명령줄/로그에 남지 않도록 `/AgentTokenFile`로만 전달합니다.
- 기존 Inno 설치가 있으면 무인 설치에서는 자동으로 제거 후 설치합니다.
- `%ProgramData%\UpdateAgent\agent.json`과 로그/데이터는 업그레이드 후에도 유지됩니다.
- 방화벽 규칙은 `UpdateAgent Agent`, `UpdateAgent Agent <버전>` 두 개가 등록됩니다.
- 설치 후 서비스(`UpdateAgentService`)가 자동 시작됩니다.

### 4-B. MSI 설치 (대안)

개발 PC에서 MSI를 만들고, MSI와 `scripts` 폴더를 학생 PC로 복사합니다.

```powershell
# 개발 PC
.\scripts\publish.ps1
.\scripts\build-msi.ps1
# 산출물: artifacts\msi\UpdateAgent.Agent-<버전>-x64.msi
```

학생 PC에서 **관리자 PowerShell**을 열고:

```powershell
Set-Location "<복사한 폴더>"

$env:UPDATEAGENT_AGENT_TOKEN = Get-Content "<agent-token.txt 경로>" -Raw
.\scripts\install-agent-msi.ps1 `
  -MsiPath ".\UpdateAgent.Agent-0.1.2-x64.msi" `
  -LabManagerUrl "http://<교사-PC-IP>:5080" `
  -ConfirmMaestro
```

스크립트가 수행하는 작업:

1. MSI 설치 (최소 UI, `-Quiet`면 무인). 서비스는 `C:\Program Files\UpdateAgent\Service`
2. `%ProgramData%\UpdateAgent\agent.json`에 URL/토큰/SessionHelper 경로 기록
3. `agent.json` ACL을 Administrators(전체) + SYSTEM(읽기)로 제한
4. 서비스 실패 플래그(`failureflag`) 설정
5. 서비스 재시작 및 상태 확인

- 토큰은 `msiexec` 명령줄/로그에 남지 않습니다.
- 기존 `agent.json`이 있으면 URL/토큰을 재사용하므로 업그레이드 시 생략할 수 있습니다.
- 레거시 스크립트 설치가 있으면 그 URL/토큰을 자동으로 재사용합니다.

### 4-C. 스크립트 설치 (레거시)

`publish`와 `scripts` 폴더를 구조 그대로 학생 PC로 복사합니다.

```text
<USB 또는 C:\Temp\UpdateAgent>\
  scripts\
  publish\agent\service
  publish\agent\sessionhelper
  publish\cli                (선택: 사전 점검/상태 확인용)
```

학생 PC에서 **관리자 PowerShell**을 열고:

```powershell
Set-Location "<복사한 폴더>"

$env:UPDATEAGENT_AGENT_TOKEN = Get-Content "<agent-token.txt 경로>" -Raw
.\scripts\install-service.ps1 -LabManagerUrl "http://<교사-PC-IP>:5080"
```

스크립트가 수행하는 작업:

1. `C:\ProgramData\UpdateAgent\app\service`에 서비스 게시물 복사
2. `C:\ProgramData\UpdateAgent\app\sessionhelper`에 오버레이 게시물 복사
3. `appsettings.json`에 LabManager URL/토큰/SessionHelper 경로 기록
4. 설정 파일 ACL을 Administrators(전체) + SYSTEM(읽기)로 제한
5. `UpdateAgentService`를 자동 시작 서비스로 등록 (LocalSystem)
6. 서비스 시작 및 실패 시 자동 재시작 설정

### 4-1. 설치 확인

```powershell
Get-Service UpdateAgentService
Get-Content C:\ProgramData\UpdateAgent\logs\service-*.log -Tail 30
```

기대 로그:

```
UpdateAgent Service 시작: agent=<GUID>, machine=<PC 이름>
Content root path: C:\Program Files\UpdateAgent\Service\   (MSI)
Content root path: C:\ProgramData\UpdateAgent\app\service\  (레거시)
```

`Content root path`가 설치 방식에 맞는지 확인합니다.
대시보드에서 30~90초 안에 해당 PC가 **온라인**으로 나타나야 합니다.

> 오버레이를 끄고 먼저 연동만 확인하려면:
> MSI: `.\scripts\install-agent-msi.ps1 ... -DisableOverlay`
> 레거시: `.\scripts\install-service.ps1 ... -DisableOverlay`
> 나중에 `.\scripts\configure-agent.ps1 -EnableOverlay -RestartService`로 켤 수 있습니다.

## 5. 사전 점검 (선택)

학생 PC에서 관리자 PowerShell로 게시된 CLI를 실행하면 상세 점검 결과를 볼 수 있습니다.

```powershell
Set-Location "<복사한 폴더>"
.\publish\cli\UpdateAgent.Cli.exe preflight --confirm-maestro
.\publish\cli\UpdateAgent.Cli.exe scan
.\publish\cli\UpdateAgent.Cli.exe status
.\publish\cli\UpdateAgent.Cli.exe maestro
```

> `run`/`resume`/`pause`/`cancel`은 서비스가 실행 중일 때 사용하지 마세요.
> 작업 제어는 대시보드를 사용합니다. CLI는 `scan`, `preflight`, `status`, `maestro` 용도로만 쓰는 것이 안전합니다.

확인 항목:

- 관리자 권한, 디스크 여유 공간, AC 전원, 네트워크
- WSUS/그룹 정책으로 WU가 차단되지 않았는지
- Maestro 설치/보호 상태
- 다른 Windows Update 작업이 진행 중이지 않은지 (`WU 사용 중`)

## 6. Maestro 보호 중지 (수동)

학생 PC에서 Maestro Network 7을 열고:

1. **매 부팅 자동 복구 → 수동 복구/해제**로 전환
2. 현재 보호 상태/예외 설정을 기록하거나 스크린샷으로 남김
3. 대시보드의 **작업 시작** 창에서 **Maestro 복원 보호 해제 확인** 체크박스를 선택
4. 작업이 시작된 뒤 작업 상세의 **Maestro 확인** 영역에서 **보호 해제 확인** 버튼을 눌러 기록

> 이 체크박스/버튼은 에이전트가 Maestro 설치를 감지한 PC에서만 표시됩니다.
> 앱은 Maestro 설정을 변경하지 않습니다.

### 6-1. DRY RUN 연동 확인 (권장)

Maestro 보호를 중지한 상태에서 실제 업데이트 전에 네트워크/토큰/작업 흐름을 먼저 확인합니다.

```powershell
.\scripts\configure-agent.ps1 -EnableDryRun -RestartService
```

대시보드에서 PC가 **온라인**인지 확인한 뒤 **작업 시작**을 누르고,
**Maestro 복원 보호 해제 확인**을 체크합니다.
DRY RUN에서는 실제 다운로드/설치/재부팅 없이 작업 흐름만 시뮬레이션됩니다.

작업이 `completed`로 끝나면 실제 모드로 되돌립니다.

```powershell
.\scripts\configure-agent.ps1 -DisableDryRun -RestartService
```

> DRY RUN 중에는 사전 점검에서 관리자 권한 부족이 차단 대신 경고로 처리됩니다.
> 서비스는 LocalSystem으로 실행되므로 실제 파일럿에서는 관리자 권한 문제가 없어야 합니다.

## 7. 파일럿 작업 시작

사전 조건:

- 학생 PC가 대시보드에서 **온라인**
- 학생 사용자 로그오프 상태 (권장)
  - 시작 창의 **사용자 로그온 시 일시중지**가 기본으로 체크되어 있습니다.
  - 사용자가 로그온되어 있으면 작업이 일시중지되며, 대시보드의 **재개**로 계속할 수 있습니다.
  - 오버레이를 테스트하려면 작업 시작 후 학생 계정으로 로그온 → 일시중지 확인 → 대시보드에서 **재개**를 누릅니다.
  - 로그온 상태에서 처음부터 진행하려면 시작 창에서 **사용자 로그온 시 일시중지**를 해제합니다.
- 재부팅해도 되는 상태이고 복구 수단이 준비됨
- Maestro 보호 중지 완료
- 6-1에서 DRY RUN을 켰다면 `.\scripts\configure-agent.ps1 -DisableDryRun -RestartService`로 해제

대시보드에서 해당 PC를 선택하고 **작업 시작**을 누릅니다.
기본 정책:

- 보안/중요/품질 업데이트 포함
- 선택적 업데이트, 드라이버, 정의 업데이트, 기능 업데이트 제외
- 최대 재부팅 5회
- 재부팅 자동, 60초 지연
- 오버레이 사용

## 8. 관찰 체크리스트

작업이 진행되는 동안 확인합니다.

### 진행/상태

- [ ] 작업이 `pending → preflight → scanning → downloading → installing` 순서로 진행 (`pendingReboot`/`rebooting` 포함)
- [ ] 항목별 KB/제목/크기/상태가 대시보드에 표시
- [ ] 사전 점검 11개 항목이 모두 보고됨
- [ ] WU 사용 중이면 충돌하지 않고 대기/경고

### 재부팅/재개

- [ ] 재부팅이 필요한 경우 자동 재부팅
- [ ] 재부팅 후 서비스가 자동 시작되고 작업이 `resume`
- [ ] 재부팅 횟수가 5회를 넘지 않음
- [ ] 재부팅 후 Maestro 마커 검증(`restoreVerified`) 통과
- [ ] 최종 상태가 `completed` (또는 실패 항목이 있으면 `failed`와 사유 표시)

### 오버레이 (사용자 세션 존재 시)

- [ ] 전체화면 검은 오버레이 표시
- [ ] 중앙에 진행률/단계/현재 KB 표시
- [ ] 커서 숨김, 일반 단축키 억제
- [ ] 작업 완료/실패 후 설정된 시간(기본 20초) 뒤 자동 해제
- [ ] 대시보드 PC 목록의 **해제** 버튼으로 즉시 해제
- [ ] Ctrl+Alt+Del은 소프트 오버레이로 차단되지 않음(알려진 제한)

### Maestro

- [ ] 앱이 Maestro 서비스/설정을 변경하지 않음
- [ ] 앱이 WU 충돌을 감지하고 동시 실행을 피함
- [ ] 재부팅 후 마커 파일이 유지되고 검증됨
- [ ] 대시보드 작업 상세에서 **보호 해제 확인** 기록 가능
- [ ] 작업 종료 후 **상태 저장 + 보호 재활성화 확인** 기록 가능

### 보안/운영

- [ ] 잘못된 에이전트 토큰으로 체크인 시 401
- [ ] 잘못된 관리자 토큰으로 관리 API 접근 시 401
- [ ] 감사 로그에 `job-start`, `job-state`, `command-ok`, `job-failed` 기록
- [ ] 서비스 재시작 후에도 진행 중 작업이 재개됨
- [ ] (선택) Wake-on-LAN으로 PC 부팅
- [ ] (선택) 자동 종료 정책 동작

## 9. Maestro 복원 (수동)

업데이트/재부팅이 끝나면 학생 PC에서:

1. Maestro에서 **현재 상태 저장**
2. **자동 복구 재설정(매 부팅 자동 복구)** 으로 복원
3. 대시보드 작업 상세에서 **상태 저장 + 보호 재활성화 확인** 버튼을 눌러 기록

## 10. 합격 기준

| 항목 | 합격 기준 |
|---|---|
| 서비스 배포 | 재부팅 후 `UpdateAgentService` 자동 시작, 90초 내 온라인 |
| 업데이트 선택 | 보안/품질 선택, 드라이버/정의/기능 업데이트 제외 |
| 설치 | 최소 1개 이상 실제 설치 성공, 실패 시 항목별 사유 표시 |
| 재부팅/재개 | 재부팅 후 자동 재개, 최대 5회 제한 준수 |
| Maestro | 마커 검증 통과, 앱이 Maestro 설정을 변경하지 않음 |
| 오버레이 | 표시/진행률/해제 정상, Ctrl+Alt+Del 제외 |
| 관측성 | 작업 상세, 사전 점검, 감사 로그, 서비스 로그 확보 |
| 보안 | 토큰 오류 시 401, 설정 파일 ACL 제한 |

## 11. 실패 시 롤백

### 작업 중지

대시보드에서 **취소**를 누르거나:

```powershell
Stop-Service UpdateAgentService
```

### 서비스 제거 (데이터 유지)

```powershell
# MSI
.\scripts\uninstall-agent-msi.ps1 -Force

# 레거시
.\scripts\uninstall-service.ps1 -RemoveFiles -Force
```

### 서비스 + 데이터 완전 제거

```powershell
# MSI
.\scripts\uninstall-agent-msi.ps1 -RemoveData -Force

# 레거시
.\scripts\uninstall-service.ps1 -RemoveFiles -RemoveData -Force
```

### MSI에서 레거시로 되돌리기

```powershell
.\scripts\uninstall-agent-msi.ps1 -Force
.\scripts\install-service.ps1 -LabManagerUrl "http://<교사-PC-IP>:5080"
.\scripts\configure-agent.ps1 -RestartService
```

### 오버레이가 멈춘 경우

```powershell
Stop-Process -Name UpdateAgent.SessionHelper -Force -ErrorAction SilentlyContinue
```

또는 대시보드 PC 목록의 **해제** 버튼을 사용합니다. 사용자 세션에서 로그오프해도 종료됩니다.

### Maestro 복원

파일럿 중 문제가 생기면 즉시 Maestro에서 **현재 상태 저장 → 자동 복구 재설정**을 수행합니다.
업데이트로 인한 문제는 Windows 복원 지점/스냅샷으로 되돌립니다.

### LabManager 중지

교사 PC에서 `UpdateAgent.LabManager.exe` 프로세스를 종료합니다. 앱 모드 창을 닫아도 서버는 중지되지 않습니다.

## 12. 수집할 증적

| 증적 | 경로/방법 |
|---|---|
| 서비스 로그 | `C:\ProgramData\UpdateAgent\logs\service-*.log` |
| 에이전트 DB/마커 | `C:\ProgramData\UpdateAgent\agent.db`, `resume-marker.json` |
| LabManager DB | `%LOCALAPPDATA%\UpdateAgent\LabManager\labmanager.db` |
| LabManager 로그 | `%LOCALAPPDATA%\UpdateAgent\LabManager\logs\` |
| 작업 상세 JSON | `GET /api/admin/jobs/{jobId}` (관리자 토큰) |
| 감사 로그 | `GET /api/admin/audit` |
| 화면 | 대시보드 작업 상세, 오버레이, Maestro 설정, Windows Update 기록 |
| OS/빌드 | `winver`, `Get-ComputerInfo` |

## 13. 알려진 제한 및 다음 단계

- Ctrl+Alt+Del(보안 관련 시퀀스)은 소프트 오버레이로 차단할 수 없습니다.
- 기능 업데이트(OS 업그레이드)는 이번 파일럿 범위가 아닙니다.
- MSI는 제공되지만, 상승된 VM/실 PC에서 설치·업그레이드·제거 검증이 아직 필요합니다.
- 랩 전체 일괄 원격 배포는 아직 없습니다 (USB/공유 폴더 수동 배포).
- Maestro 연동은 수동 체크리스트 + 마커 검증만 지원합니다.
- 학생 계정이 로컬 관리자인 환경에서는 완전한 변조 방지가 불가능합니다.

파일럿 통과 후:

1. 2~3대에 동일 절차로 배포해 재현성 확인
2. MSI/원격 배포 자동화
3. 랩 전체(약 50대) 롤아웃
4. 기능 업데이트 승인 워크플로와 Maestro 자동화 검토

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

관리자 토큰과 에이전트 토큰을 생성합니다. **기본값(`change-me-*`)과 설치 파일에 들어 있는 샘플 값(`sample-*-token-replace-before-pilot`)은 파일럿에서도 그대로 사용하지 마세요.**
샘플 값은 서버가 바로 시작되도록 넣어 둔 예시일 뿐이며, 값 자체에 표시된 대로 배포 전 반드시 교체해야 합니다.
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

기대 결과: 빌드 경고 0/오류 0, 테스트 165개 통과.

## 2. 게시

```powershell
.\scripts\publish.ps1
```

결과:

```
publish\labmanager
publish\agent\service
publish\agent\sessionhelper
publish\agent\desktop
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
기본 토큰(`change-me-*`)이면 서버가 시작되지 않습니다. 설치 마지막 단계의 런처 자동 실행도
기본 토큰이 남아 있으면 건너뛰고 토큰 변경 안내를 표시합니다.
설치 파일 기본값인 샘플 토큰(`sample-*-token-replace-before-pilot`)으로는 서버가 시작되지만,
이 값도 배포 전에 반드시 실제 토큰으로 교체해야 합니다.

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

대시보드 우측 상단의 **토큰** 버튼을 누르면 설치 시 기본값인 샘플 관리자 토큰이 화면에 표시됩니다.
**이 값 사용**으로 입력란을 채워 서버에 연결한 뒤, 같은 창의 **서버 토큰 변경**에서 3-1에서 준비한 실제
토큰으로 교체하세요. 변경은 재시작 없이 즉시 적용되고 브라우저 localStorage도 새 값으로 갱신되며,
이전 토큰은 바로 거부됩니다. 서버만 띄우려면 `.\UpdateAgent.LabManager.exe`를 직접 실행해도 됩니다.

### 3-3. 실시간 작업 현황 모니터 열기

- 대시보드 우측 상단의 **작업 현황 모니터** 버튼을 누르면 전용 모니터 창(`http://localhost:5080/monitor.html`)이
  별도로 열립니다. 대시보드는 그대로 남아 있으므로 작업 제어와 현황 관찰을 동시에 할 수 있습니다.
- 같은 브라우저 프로필을 사용하므로 `ua_admin_token`이 공유됩니다. 새 창에 토큰 입력창이 나타나면
  대시보드에서 저장한 토큰과 같은 값을 저장하세요.
- 모니터는 SSE 스트림(`GET /api/admin/live`)으로 체크인·작업 변경을 즉시 반영하고, 변경이 없어도
  15초마다 최신 상태를 다시 받습니다. 상단의 연결 상태가 **실시간 연결**이면 정상입니다.
- 전용 바로가기를 만들려면 대상에 `UpdateAgent.LabManager.Desktop.exe --monitor`를 지정합니다.
  서버는 창을 닫아도 계속 실행됩니다.
- 진행 중인 작업의 **상세** 버튼은 대시보드의 작업 상세 화면(`/?job=<작업ID>`)을 엽니다.

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

학생 PC에서 **관리자 PowerShell**을 열고 같은 폴더의 설정 파일로 무인 설치합니다.
샘플은 [`scripts/agent-install.sample.ini`](../scripts/agent-install.sample.ini)를 복사해 사용하세요.

```ini
; agent-install.ini (설치 exe와 같은 폴더)
[Agent]
LabManagerUrl=http://<교사-PC-IP>:5080
AgentTokenFile=agent-token.txt
```

```powershell
Set-Location "<복사한 폴더>"

.\UpdateAgent.Agent-Setup-<버전>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

- 토큰 값은 설정 파일에 쓰지 않고 `AgentTokenFile`이 가리키는 파일의 첫 줄에만 둡니다.
- 명령줄 `/LabManagerUrl`, `/AgentTokenFile`도 계속 지원하지만 배포에는 INI를 권장합니다.
- 기존 Inno 설치가 있으면 무인 설치에서는 자동으로 제거 후 설치합니다.
- `%ProgramData%\UpdateAgent\agent.json`과 로그/데이터는 업그레이드 후에도 유지됩니다.
- 방화벽 규칙은 `UpdateAgent` 그룹의 `UpdateAgent Agent`, `UpdateAgent Agent <버전>` 두 개가 등록됩니다.
- 설치 후 서비스(`UpdateAgentService`)가 자동 시작됩니다.
- INI/BOM, URL, 토큰 검증은 기존 설치 제거 전에 수행되며 실패 시 설치가 중단됩니다.
  원인은 `%TEMP%\Setup Log <날짜>.txt`에서 확인하세요.
- 토큰 파일이 삭제된 뒤 재설치할 때는 `install-agent.cmd /Upgrade`를 사용하거나 새 패키지를 생성하세요.

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
  -MsiPath ".\UpdateAgent.Agent-<버전>-x64.msi" `
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
  publish\agent\desktop      (학생용 상태 확인 GUI)
  publish\cli                (선택: 고급 사전 점검용)
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
  - 시작 창의 **사용자 로그온 시 일시중지**는 기본적으로 **해제**되어 있습니다(50대 랩 기준).
    학생이 로그온해도 작업이 멈추지 않으므로 교사가 일일이 **재개**할 필요가 없습니다.
  - 일시중지 동작을 확인하려면 시작 창에서 해당 옵션을 체크한 뒤, 작업 시작 후 학생 계정으로
    로그온 → 일시중지 확인 → 대시보드에서 **재개**를 누릅니다.
- 재부팅해도 되는 상태이고 복구 수단이 준비됨
- Maestro 보호 중지 완료
- 6-1에서 DRY RUN을 켰다면 `.\scripts\configure-agent.ps1 -DisableDryRun -RestartService`로 해제

대시보드에서 해당 PC를 선택하고 **작업 시작**을 누릅니다.
작업 시작 전에 **작업 현황 모니터** 창을 열어 두면 진행률·재부팅 횟수·Maestro 확인 필요 상태가
새 탭/창에서 실시간으로 표시됩니다(대시보드의 5초 갱신과 별개).
기본 정책:

- 보안/중요/품질 업데이트 포함
- 선택적 업데이트, 드라이버, 정의 업데이트, 기능 업데이트 제외
- 최대 재부팅 5회
- 재부팅 자동, 60초 지연
- 오버레이 사용

### 7-1. 학생용 앱에서 수동 시작

교사 대시보드 대신 학생 PC에서 직접 시작하려면 `UpdateAgent 상태 확인` 앱의 **수동 업데이트 옵션** 버튼을 사용합니다.
기존 **업데이트 시작** 버튼을 눌러도 같은 옵션 창이 열립니다.

1. 서비스가 **실행 중**이고 진행 중인 작업이 없는지 확인합니다.
2. Maestro를 '매 부팅 자동 복구'에서 '수동 복구/해제' 모드로 전환합니다.
3. **수동 업데이트 옵션**을 누르면 **수동 업데이트 옵션** 창이 열립니다.
   - 프리셋(표준/보안 중심/서버 안전/전체)을 고르거나 세부 항목을 직접 조정합니다.
   - 세부 항목: 보안/중요/권장/선택/드라이버/Defender 정의 업데이트, 전체화면 오버레이, 자동 재부팅, 최대 재부팅 횟수(0~10).
   - 기능 업데이트는 2차 승인 전용이라 선택할 수 없습니다.
   - Server Core에서는 오버레이가 비활성화되고, 교사 정책이 허용하지 않은 항목은 선택해도 적용되지 않습니다.
   - 업데이트 범위를 모두 해제하면 시작할 수 없습니다.
4. 옵션 창에서 Maestro 복원 보호 해제 확인을 체크하고 **업데이트 시작**을 누릅니다.
5. 사전 점검을 통과하면 작업이 시작되고, 상태 앱과 교사 대시보드에 진행 상황이 표시됩니다.
   - 체크인 주기(기본 30초)와 무관하게 시작 직후 한 번 즉시 체크인합니다.
   - 대시보드 작업 상세의 Maestro 체크리스트는 `student`가 보호 해제를 확인한 것으로 기록됩니다.
6. 이미 작업이 진행 중이면 버튼이 비활성화됩니다. 재개/취소는 대시보드에서 수행합니다.

> 학생은 로컬 관리자이므로 상태 앱의 로컬 제어 파이프(`UpdateAgent.LocalControl.v1`)로 작업을 시작할 수 있습니다.
> 토큰은 앱에 저장하지 않으며, 선택 옵션은 `start-update` 요청의 `options`로 전달됩니다.
> 서비스는 캐시된 교사 정책을 상한으로 사용해 학생이 고른 범위·재부팅·오버레이를 반영합니다.
> 학생은 항목을 줄이거나 자동 재부팅을 끌 수 있지만, 교사가 금지한 항목·재부팅·오버레이를 다시 켜거나
> 기능 업데이트·Maestro 확인 요구·로그온 일시중지·종료 등 교사/2차 승인 항목을 바꿀 수 없습니다.
> CLI `run`으로 직접 실행한 작업은 로컬에만 남고 대시보드에 보고되지 않습니다.

## 8. 관찰 체크리스트

작업이 진행되는 동안 확인합니다.

### 진행/상태

- [ ] 학생용 앱의 수동 업데이트 옵션 창에서 프리셋/세부 항목을 선택할 수 있음
- [ ] 선택한 옵션이 시작된 작업의 정책에 반영되고 기능 업데이트는 꺼져 있음
- [ ] 학생용 앱에서 시작한 작업도 대시보드 작업 목록에 표시됨
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
- [ ] 작업 목록의 **강제 중단**으로 해당 PC만 중단되고 감사 로그에 `force-stop` 기록
- [ ] 오버레이에서 `Ctrl+Shift+U`를 2초 안에 두 번 누르면 중단 요청 전송(한 번만 누르면 안내만 표시)
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
- [ ] 대시보드 토큰 창에 샘플 관리자 토큰이 표시되고 **이 값 사용**으로 로그인 가능
- [ ] 서버 토큰 변경 후 기존 토큰은 401, 새 토큰은 200 (재시작 없이 적용, localStorage 갱신 확인)
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

대시보드에서 **취소** 또는 **강제 중단**을 누르거나(학생은 오버레이에서 `Ctrl+Shift+U` 두 번):

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
- 강제 중단도 이미 시작된 Windows Update 설치 자체는 멈추지 못합니다. 설치가 끝나는 시점에 중단되고,
  남은 항목은 설치되지 않습니다(시스템 손상 방지를 위한 안전 체크포인트 유지).
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

# 50대 랩 배포 런북 (학생 PC ≈50대 + 교사 PC 1대)

이 문서는 설계한 **허브-스포크 연결 방식**을 실제로 배포하는 절차입니다.
학생 PC는 교사 PC를 향해서만 폴링하고, 교사 PC만 TCP 5080 인바운드를 엽니다.
학생 PC에는 인바운드 방화벽 규칙이 필요하지 않습니다.

- 개발자: AhBiYout / GitHub: <https://github.com/ahbiyout-all> / 블로그: <https://ahbiyoutvibe.blogspot.com/>
- 1대 파일럿은 [`PILOT.md`](PILOT.md), 설치 프로그램 옵션은 [`INSTALLER.md`](INSTALLER.md),
  공통 Guard DLL 규칙은 [`PURE-DLL.md`](PURE-DLL.md) 참고

## 0-A. 50대 부하 리허설 런북 (`scripts/load-rehearsal.ps1`)

`scripts/load-rehearsal.ps1`은 **실제 LabManager 인스턴스**에 대해 실제
`POST /api/agents/checkin` 요청(`X-Agent-Token` 헤더, camelCase DTO)을 보내
체크인 경로의 동시성·지연·실패 복구를 측정합니다. 기본 50개 가상 에이전트 ID는
스크립트가 생성합니다. **합성 리허설이며 실 PC 50대 증명이 아닙니다.**
학생 PC 설치, WUA 스캔/설치, 재부팅/재개, 오버레이, Maestro 마커는 이 스크립트가
확인하지 않으므로 [파일럿 체크리스트](PILOT.md)와 7장 검증을 별도로 수행해야 합니다.
기능 업데이트 정책은 시작하지 않습니다.

### 실행 명령

토큰 **값**은 명령줄에 쓰지 않고 토큰 **파일 경로**만 넘깁니다. 파일의 첫 줄만 읽습니다.

```powershell
# 기본: 50대, 30초, 5초 간격, 동시성 10
.\scripts\load-rehearsal.ps1 `
  -LabManagerUrl "http://127.0.0.1:5080" `
  -AgentTokenFile "<agent-token.txt 경로>" `
  -OutputPath ".\deploy\load-rehearsal\load-rehearsal-report.json"

# 관리자 API까지 확인: 소수의 합성 작업 시작/취소 + SSE 재연결 확인
.\scripts\load-rehearsal.ps1 `
  -LabManagerUrl "http://<교사-PC-IP>:5080" `
  -AgentTokenFile "<agent-token.txt 경로>" `
  -AdminTokenFile "<admin-token.txt 경로>" `
  -AgentCount 50 -DurationSeconds 60 -IntervalSeconds 5 -MaxConcurrency 10

# 네트워크 사전 점검만 생략(토큰 검증은 그대로 수행)
.\scripts\load-rehearsal.ps1 `
  -AgentTokenFile "<agent-token.txt 경로>" -SkipPreflight
```

`-SkipPreflight`는 `GET /api/health`만 건너뜁니다. 서버가 꺼져 있거나
토큰 파일이 없/비었음/너무 짧음/플레이스홀더이면 즉시 중단됩니다.
Ctrl+C로 취소할 수 있고, 취소 신호를 정상 취소로 변환해 보고서를 기록합니다.
하드 타임아웃은 `DurationSeconds + RequestTimeoutSeconds + 30`초이며,
요청별 제한은 `RequestTimeoutSeconds`(기본 10초)입니다.

가상 에이전트 ID는 실행마다 바뀌지 않는 `rehearsal-pc-000`~`049`를 사용합니다.
서버는 등록 가능한 PC를 200대로 제한하므로, 실행마다 새 ID를 만들면 반복
리허설에서 429가 발생합니다. 같은 서버에 대해 **한 번에 하나의 리허설만 실행**하고,
병렬 리허설이 필요하면 서버 인스턴스를 분리하세요.

### 보고서 해석

`OutputPath`에 UTF-8(BOM 없음) JSON을 기록합니다. 주요 필드:

| 필드 | 의미 |
|---|---|
| `generatedAtUtc`, `version` | UTC 생성 시각, 제품 버전(`Directory.Build.props`)과 스크립트 버전 |
| `parameters` | URL/대수/기간/간격/동시성/토큰 파일 경로. **토큰 값은 없음** |
| `preflight` | `/api/health` 결과(2xx 여부) 또는 `skipped` |
| `counts` | `completedRequests`, `success`, `httpFailure`, `transportFailure`, `serverError5xx`, `unauthorized`, `throttled`, 비율 |
| `statusDistribution` | HTTP 상태 코드별 건수(전송 실패는 `transport`) |
| `latencyMs` | `minMs`/`avgMs`/`p95Ms`/`maxMs`, `sampleCount`(완료된 모든 시도) |
| `recovery` | 연속 실패 후 성공으로 회복된 가상 PC 수, 최대 연속 실패, 미회복 ID |
| `failures` | 최대 50건의 정제된 실패 메시지. 토큰/원시 헤더 없음. 50건을 넘어 생략된 건수만 `truncatedFailureCount`에 명시 |
| `liveStream` | 관리자 토큰이 있으면 SSE 2회 연결/재연결 확인. 없으면 `skipped` + 사유 |
| `jobChecks` | 관리자 토큰이 있으면 소수 작업 시작 후 취소 결과. 없으면 `skipped` + 사유 |
| `artifacts` | 리허설이 만든 입력 JSON의 SHA-256. 보고서 자체 해시는 기록 직후 콘솔에 출력(자기 참조 방지) |
| `cancellation` | 하드 타임아웃 도달 여부, 사용자 취소 여부, 취소 시점의 미완료 요청 수 |

`liveStream`/`jobChecks`가 `skipped`인 것은 실패가 아니라 **확인 불가**입니다.
관리자 토큰을 넘겼는데 `reconnectVerified=false`거나 `cancelledJobs`가
`startedJobs`보다 적으면 원인을 확인해야 합니다.

### 합격 기준(종료 코드)

| 조건 | 결과 |
|---|---|
| 사전 점검 실패, 토큰 파일/플레이스홀더 오류, 취소·치명 오류 | 종료 코드 1 |
| HTTP 5xx 1건 이상 | 종료 코드 2 (0건 요구) |
| 전송 실패율 > 5% | 종료 코드 3 |
| 성공률 < 95%, 또는 401/403/429 1건 이상 | 종료 코드 4 |
| 위 조건 미해당 | 종료 코드 0 |

`liveStream`/`jobChecks`의 경고는 현재 종료 코드에 반영되지 않는 **참고 항목**입니다.
합격 기준을 통과해도 실 PC 파일럿의 대체 증적으로 쓰지 마세요.

## 0. 토폴로지

```text
학생 PC 01..50  ──(아웃바운드 GET/POST, 30초 간격)──>  교사 PC :5080
     │                                                   │
     └─ UpdateAgentService (LocalSystem)                  ├─ LabManager API (headless, 자동 시작)
        └─ SessionHelper → 소프트 입력 잠금 오버레이       ├─ 대시보드 + 전용 모니터
                                                          └─ labmanager.db / logs

학생 → 학생 통신 없음. 학생 PC 인바운드 규칙 없음.
방화벽: 교사 PC Domain/Private 프로필에서만 TCP 5080 허용.
```

> 교사용 콘솔은 시스템 Edge/Chrome의 `--app=` 단독 창만 사용합니다. 과거 독립 실험용
> Tauri 스캐폴드는 제거되었고, 내장 WebView2 런타임이나 Node/Rust 도구는 릴리스에
> 포함되지 않습니다(추가 릴리스 자산: 없음). 로컬에 남은 실험 폴더는 Git에서 무시되며
> 배포 경로가 아닙니다.

| 항목 | 값 |
|---|---|
| 이름 | DNS 별칭 `labmanager`는 선택 사항 (기본 배포는 IP 사용) |
| 주소 | `http://<교사-PC-IP>:5080` (권장) 또는 `http://labmanager:5080` |
| 교사 PC IP | 고정 IP 또는 DHCP 예약 (예: `192.168.0.10`) |
| 포트 | TCP 5080 |
| 폴링 주기 | 30초 (최소 15초), 온라인 판정 90초 |
| 에이전트 토큰 | 모든 학생 PC에 **동일**하게 배포 |
| 관리자 토큰 | 교사 PC에만 보관 (대시보드/API 로그인) |

> 기본 배포는 IP 방식입니다. `-LabManagerUrl http://<교사-PC-IP>:5080`으로 명시하거나
> `-DiscoverLabManager`로 같은 서브넷에서 TCP 5080 헬스체크에 응답하는 교사 PC를 찾으면
> `agent-install.ini`에 `http://<검색된-IP>:5080`이 자동으로 기록됩니다.
> 검색 대상이 0대거나 2대 이상이면 수동 지정을 요구하고 패키지를 만들지 않습니다.
> DNS 별칭 `labmanager`를 쓰려면 `-LabManagerUrl http://labmanager:5080`을 명시하세요.
> 명시한 URL은 항상 자동 검색보다 우선합니다.

## 1. 사전 확인

- [ ] 교사 PC에 UpdateAgent LabManager 설치 완료 (또는 `publish\labmanager` 준비)
- [ ] 학생 PC 50대가 같은 LAN에 있고 교사 PC IP로 TCP 5080 접근 가능
- [ ] 교사 PC 네트워크 프로필이 **Domain 또는 Private** (Public이면 방화벽 규칙이 적용되지 않음)
- [ ] 학생 PC는 로컬 관리자 권한으로 설치 가능
- [ ] 학생 로그인 계정에 암호가 있어도 됩니다. **암호는 수집하지 않습니다.** 서비스는 LocalSystem으로
      실행되며 `WTSQueryUserToken`으로 사용자 세션 토큰을 얻습니다. 재부팅 후 재개는 로그온 전에
      이루어지고, 오버레이는 로그온 뒤 표시됩니다(8-A 참고).
- [ ] 복구 수단(스냅샷/이미지) 확보

## 2. 토큰 준비 (개발 PC 또는 교사 PC)

기본값(`change-me-*`)과 샘플 값(`sample-*-replace-before-pilot`)은 서버가 거부하므로
절대 사용할 수 없습니다. 실제 토큰 두 개를 준비하고 서로 다르게 설정하세요.

```powershell
function New-Token {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes).Replace('+','-').Replace('/','_').TrimEnd('=')
}

$secretDir = Join-Path $env:TEMP "updateagent-lab"
New-Item -ItemType Directory -Force $secretDir | Out-Null
New-Token | Set-Content "$secretDir\admin-token.txt" -NoNewline
New-Token | Set-Content "$secretDir\agent-token.txt" -NoNewline
```

`admin-token.txt`는 교사 PC에서만, `agent-token.txt`는 배포 도구에서만 사용합니다.
실제 운영에서는 암호 관리 도구에 보관하고 파일럿 후 임시 폴더를 삭제하세요.

## 3. 교사 PC 구성 (한 번)

교사 PC에서 **관리자 PowerShell**으로 실행합니다.

```powershell
Set-Location "<복사한 저장소 폴더>"

.\scripts\setup-labmanager-lab.ps1 `
  -AdminTokenFile "$env:TEMP\updateagent-lab\admin-token.txt" `
  -AgentTokenFile "$env:TEMP\updateagent-lab\agent-token.txt" `
  -Port 5080
```

스크립트가 하는 일:

1. 설치된 `C:\Program Files\UpdateAgent\LabManager\appsettings.json`
   (없으면 `publish\labmanager\appsettings.json`)에 실제 토큰과 `Urls=http://0.0.0.0:5080` 기록
2. `UpdateAgent LabManager` / `UpdateAgent LabManager <버전>` 방화벽 규칙(Domain/Private, TCP 5080) 정리
3. 로그온 시 `UpdateAgent.LabManager.exe --headless`를 실행하는 예약 작업 등록
   (`UpdateAgent LabManager (headless)`, 가장 높은 권한)
4. 서버가 없으면 지금 즉시 백그라운드로 시작하고 `GET /api/health` 확인

자동 시작 예약 작업은 스크립트를 실행한 계정으로 등록되므로, **교사가 실제로 로그온하는 계정으로**
로그인한 상태에서 실행하세요. 미리 확인만 하려면 `-DryRun`을 붙이세요. 이미 수동으로 설정했다면
`-SkipConfigure`, `-SkipFirewall`, `-SkipAutoStart`, `-SkipStart`로 원하는 단계만 건너뛸 수 있습니다.
`-ConfigPath`로 다른 `appsettings.json`을 지정할 수도 있습니다.

정상이면 아래가 표시됩니다.

```text
Health check OK: http://127.0.0.1:5080/api/health
Teacher PC setup complete.
```

브라우저에서 `http://<교사-PC-IP>:5080`을 열고 관리자 토큰으로 로그인하세요.
대시보드의 **작업 현황 모니터** 버튼으로 별도 창(`/monitor.html`)을 띄워 두면
작업 제어와 실시간 관찰을 동시에 할 수 있습니다. 창을 닫아도 서버는 계속 실행됩니다.

## 4. 학생 PC 목록 (인벤토리)

개발 PC에서 `scripts\agents.sample.csv`를 복사해 실제 자산 목록으로 수정합니다.
`ComputerName`은 필수, `MAC`/`IP`는 선택입니다(WoL/점검용으로 기록).

```csv
ComputerName,MAC,IP
STUDENT-01,AA:BB:CC:DD:EE:01,192.168.0.101
STUDENT-02,AA:BB:CC:DD:EE:02,192.168.0.102
```

50대를 한 번에 넣어도 되고, 반별로 나눠 여러 CSV로 운용해도 됩니다.

## 5. 학생 PC 배포 패키지 생성 (개발 PC)

설치 파일을 먼저 만듭니다(이미 릴리스 자산을 내려받았다면 생략).

```powershell
.\scripts\publish.ps1 -AgentOnly
.\scripts\build-inno.ps1 -AgentOnly
```

기본 패키지는 self-contained(`win-x64`)입니다. 모든 학생 PC에 Microsoft .NET Desktop
Runtime(win-x64)이 설치되어 있다는 전제에서만 경량 패키지를 쓸 수 있습니다. 이때는
위 두 명령과 `deploy-agents.ps1`에 `-FrameworkDependent`를 함께 전달하고,
`build-info.json`의 `selfContained:false`를 확인하세요. 런타임 모드를 알 수 없으면
`확인 불가`이고, 모드가 다르면 생성/배포가 중단됩니다. 토큰·방화벽 규칙은 바뀌지
않습니다(자세한 절차는 [`INSTALLER.md`](INSTALLER.md), [`PILOT.md`](PILOT.md)).

패키지를 생성합니다.

```powershell
# IP 방식: 교사 PC 주소를 명시해 기본 agent-install.ini에 기록
.\scripts\deploy-agents.ps1 `
  -InventoryCsv ".\inventory.csv" `
  -UseEmbeddedToken `
  -LabManagerUrl "http://192.168.0.10:5080"

# IP 자동 검색: 같은 서브넷의 TCP 5080 헬스체크로 교사 PC를 찾아 INI에 기록
.\scripts\deploy-agents.ps1 `
  -InventoryCsv ".\inventory.csv" `
  -UseEmbeddedToken `
  -DiscoverLabManager

# 외부 토큰 파일을 쓰는 경우
.\scripts\deploy-agents.ps1 `
  -InventoryCsv ".\inventory.csv" `
  -AgentTokenFile "$env:TEMP\updateagent-lab\agent-token.txt" `
  -LabManagerUrl "http://192.168.0.10:5080"
```

`-UseEmbeddedToken`을 쓰면 패키지에 `agent-token.txt`가 없고 `agent-install.ini`에는 주소만 들어갑니다.
첫 설치는 설치 exe의 내장 토큰을 사용합니다. 업데이트의 재사용/무인 설치는 내장 토큰을 무시하고 기존
토큰과 URL을 유지하며, `기존 토큰을 삭제하고 새 토큰 기록`을 선택하면 릴리스 내장 토큰으로 교체합니다
(명시한 외부 토큰 파일이 있으면 그것이 우선하고, URL을 지정하지 않으면 기존 URL을 유지합니다).
두 옵션(`-AgentTokenFile`, `-UseEmbeddedToken`)은 동시에 쓸 수 없습니다.

기본적으로 패키지를 만들기 **전에** 사전 점검을 합니다. 토큰 파일 첫 줄이 8자 이상이고 기본값/샘플이 아닌지
확인하고, LabManager `http://주소:5080/api/health`에 5초 제한으로 요청해 2xx 응답을 요구합니다.
하나라도 실패하면 패키지와 리포트를 만들지 않고 중단합니다. 네트워크 점검만 생략하려면
`-SkipPreflight`를 사용하세요. 토큰 검증은 이 옵션을 써도 생략되지 않습니다.

결과 (`deploy\agents\` — Git에서 제외됨):

```text
deploy\agents\
  README.txt
  manifest.csv
  deployment-report.json
  STUDENT-01\
    UpdateAgent.Agent-Setup-<버전>-x64.exe
    agent-install.ini
    agent-token.txt
    install-agent.cmd
  STUDENT-02\ ...
```

`agent-install.ini`는 설치 exe와 같은 폴더에 두며, 설치 프로그램이 자동으로 읽습니다.

```ini
[Agent]
LabManagerUrl=http://192.168.0.10:5080
AgentTokenFile=agent-token.txt
```

상대 경로는 설치 exe가 있는 폴더 기준입니다. **토큰 값은 설정 파일에 쓰지 않고** 토큰 파일의 첫 줄에만 둡니다.
INI는 BOM 없이 저장하세요. 값 뒤의 `; 주석`은 설치 프로그램이 무시하므로 사용해도 됩니다.
토큰 파일이 없는 업그레이드는 기존 설치가 감지되고 기존 `agent.json`이 유효한 경우에만 설정을 유지하고,
새 설치에서 누락되거나 명령줄 값이 있으면 중단됩니다. 단, 릴리스 빌드에 `UPDATEAGENT_AGENT_TOKEN` secret이
포함되어 있으면 첫 설치에서 내장 토큰을 사용하므로 외부 토큰 파일 없이도 설치됩니다.
명시적 토큰 파일은 항상 우선합니다. 업데이트의 재사용/무인 설치는 내장 토큰을 읽지 않고 기존 설정을 유지하며,
교체 선택 시에만 릴리스 내장 토큰(또는 명시한 외부 토큰 파일)을 사용합니다. 새 URL을 지정하지 않으면 기존 URL을 유지합니다.

`install-agent.cmd`는 관리자 권한을 확인한 뒤 다음을 수행합니다.

1. `agent-token.txt`에 관리자/SYSTEM만 접근 가능하도록 ACL 적용(실패 시 중단)
2. `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`로 조용히 설치
3. 같은 폴더의 `agent-install.ini`에서 주소와 토큰 파일 경로를 자동으로 읽음 (**명령줄 인자 없음**)
4. 성공하면 토큰 파일 즉시 삭제하고 삭제를 확인, 실패하면 재시도 위해 보관(`agent-install.ini`는 유지)

패키지 생성 시 복사되는 토큰 파일에는 관리자/SYSTEM 및 현재 사용자 ACL이 적용되며, ACL 적용 실패는
패키지 생성을 중단합니다. 설치 전 검증(URL, 토큰, INI)은 기존 설치 제거 전에 수행되므로 실패 시 PC를 변경하지 않습니다.
설치 실패 시 오류 원인은 `%TEMP%\Setup Log <날짜>.txt`에서 확인하세요.

`deployment-report.json`에는 생성 시각(UTC), 설치 파일 이름·버전·SHA-256, LabManager 주소, 주소 출처
(`DiscoveryMode`: `explicit`/`discovered`/`default`), 사전 점검 결과, PC 수와 각 패키지의 설치 파일/INI/토큰 파일
SHA-256이 들어갑니다. **토큰 원문은 기록하지 않습니다.**
패키지를 복사한 뒤에는 다음 명령으로 파일이 변조되지 않았는지 검증할 수 있습니다. 이 모드는 파일을 쓰지 않습니다.

```powershell
.\scripts\deploy-agents.ps1 -Verify -OutputDir ".\deploy\agents"
```

보고서가 없거나 파일이 없거나 해시가 다르면 오류 코드로 종료합니다.

토큰 파일이 삭제된 뒤 같은 폴더로 업그레이드하려면 `install-agent.cmd /Upgrade`를 실행하세요.
이 모드는 토큰 파일 검사를 건너뛰고, 기존 설치가 감지되면 `agent.json`을 유지합니다.
또는 `deploy-agents.ps1`로 새 토큰 파일이 포함된 패키지를 다시 생성하는 방법을 권장합니다.

IP는 `-LabManagerUrl "http://192.168.0.10:5080"`으로 지정하거나 `-DiscoverLabManager`로 자동 검색하세요.
교사 PC가 여러 개/0개 검색되면 `-LabManagerUrl`을 명시해야 합니다. 네트워크가 서브넷 간 라우팅으로
분리돼 있으면 자동 검색할 대상이 없으므로 명시적 IP가 필요합니다.
다른 설치 파일을 쓰면 `-InstallerPath`로 경로를 넘깁니다.

## 6. 실제 설치 실행

생성만으로는 아무 PC도 변경되지 않습니다. 환경에 맞는 방법을 선택하세요.

### 6-A. USB / 수동 (가장 안전)

해당 PC 폴더 전체를 USB에 담아 이동한 뒤, 학생 PC에서 **관리자 명령 프롬프트**로
`install-agent.cmd`를 실행합니다. 50대면 반 단위로 나눠 진행하세요.

### 6-B. 한 대만 로컬에서 확인 (파일럿 재검증)

개발 PC 또는 테스트 PC에서:

```powershell
.\scripts\deploy-agents.ps1 `
  -InventoryCsv ".\inventory.csv" `
  -AgentTokenFile "$env:TEMP\updateagent-lab\agent-token.txt" `
  -ComputerName STUDENT-01 -ExecuteLocal
```

`-ExecuteLocal`은 대상이 한 대일 때만 동작하며 관리자 권한이 필요합니다.

### 6-C. PsExec / WinRM (WORKGROUP)

도메인이 없으므로 각 PC의 로컬 관리자 자격 증명이 필요합니다. 자격 증명은 명령줄에
쓰지 말고 프롬프트로 입력하세요. 패키지 폴더를 각 PC의 같은 경로(예: `C:\UADeploy`)에
복사한 뒤 실행합니다.

```powershell
# PsExec: -u만 주고 암호는 프롬프트에서 입력
psexec \\STUDENT-01 -u STUDENT-01\Admin -h cmd /c "C:\UADeploy\install-agent.cmd"

# WinRM: CredSSP가 필요하면 Enable-PSRemoting 및 대상 설정을 먼저 확인
Invoke-Command -ComputerName STUDENT-01 -Credential (Get-Credential) `
  -ScriptBlock { & "C:\UADeploy\install-agent.cmd" }
```

원격 실행 방식은 환경에 따라 달라지므로 스크립트는 특정 방식에 묶이지 않고
패키지만 생성합니다. 일괄 실행 전 2~3대로 먼저 검증하세요.

## 7. 검증

1. 교사 PC 대시보드에서 에이전트 수가 설치 대수만큼 증가하는지 확인
2. 각 PC가 **90초 이내** 온라인으로 표시되는지 확인 (폴링 30초)
3. 전용 모니터 창에서 체크인/작업 이벤트가 즉시 반영되는지 확인
4. 오프라인 PC 점검:

```powershell
# 교사 PC
Get-NetFirewallRule -Group "UpdateAgent LabManager" |
  Select-Object DisplayName, Profile, Enabled
Invoke-WebRequest "http://127.0.0.1:5080/api/health" -UseBasicParsing

# 학생 PC
Get-Service UpdateAgentService | Select-Object Name, Status, StartType
Get-Content "C:\ProgramData\UpdateAgent\agent.json"
Test-NetConnection -ComputerName labmanager -Port 5080
```

`Test-NetConnection`이 실패하면 DNS 별칭/IP, 교사 PC 방화벽 프로필, 스위치/VLAN을 확인하세요.
TCP는 성공하는데 대시보드에 안 보이면 에이전트 토큰 불일치 또는 서비스 로그를 확인하세요.

## 8. 운영

- **업데이트 배포**: 대시보드에서 작업 생성 → 학생 PC의 기존 서비스가 작업을 수신하고
  소프트 입력 잠금 오버레이(전체 화면, 진행 표시, 커서 숨김, 단축키 억제)를 표시합니다.
- **개별 중단**: 작업 목록의 **취소**(협력적) 또는 **강제 중단** 버튼으로 특정 PC만 중지할 수 있습니다.
  오버레이에서는 **Ctrl+Shift+U를 2초 안에 두 번** 눌러 긴급 중단할 수 있습니다(아래 8-B 참고).
- **잠금 화면**: 오버레이는 로그온 데스크톱에만 표시됩니다. 잠금/secure desktop 위에는
  나타나지 않지만 작업은 계속 진행되고, 잠금 해제 후 다시 표시됩니다.
- **토큰 교체**: 관리자 토큰은 대시보드의 **서버 토큰 변경**에서 즉시 적용됩니다.
  에이전트 토큰을 바꾸면 모든 학생 PC에 같은 값을 다시 배포해야 합니다.
- **재부팅/절전**: 서비스는 자동 시작되고 `ResumeOnStartup=true`라 **로그온 여부와 무관하게 부팅 직후** 작업을 재개합니다. 오버레이는 사용자가 로그온한 뒤 표시됩니다(아래 8-A 참고).
- **WoL**: 같은 서브넷에서 동작합니다. VLAN을 넘으려면 라우터에서 UDP 9/7 전달/릴레이가 필요합니다.
- **버전 업그레이드**: `deploy-agents.ps1`로 새 패키지를 생성해 설치하세요. 토큰 파일이 없는
  기존 패키지라면 `install-agent.cmd /Upgrade`로 실행합니다. 기존 설치가 감지되면
  `agent.json`의 나머지 설정을 유지한 채 주소/토큰/헬퍼 경로만 갱신합니다.

### 8-A. 재부팅 후 로그온 암호가 있는 경우

학생 PC에 로그온 암호가 있어도 업데이트 재개에는 **암호가 필요하지 않습니다**.
UpdateAgent는 암호를 저장하거나 요청하지 않습니다.

```text
재부팅 예약(기본 60초) → 자동 재부팅
  → UpdateAgentService(LocalSystem, 자동 시작)가 부팅 직후 작업 재개   ← 로그온 불필요
  → 로그온 화면: 오버레이 없음, 작업은 백그라운드에서 계속
  → 학생이 평소 계정으로 로그온 → 5초 이내에 SessionHelper가 오버레이 재표시
```

| 상황 | 동작 |
|---|---|
| 로그온 화면에서 멈춰 있음 | 정상. 서비스가 작업을 계속하므로 암호 입력만 하면 됨 |
| 잠금 화면 | 오버레이는 secure desktop 위에 표시되지 않고, 잠금 해제 후 다시 나타남 |
| 재부팅 직후 Maestro 마커 검증 실패 | 작업이 `복원 위험` 상태로 실패 처리됨. Maestro 확인 후 재실행 |
| 로그온 후 작업이 `일시중지`로 바뀜 | 작업 시작 창의 **사용자 로그온 시 일시중지**를 교사가 직접 체크했을 때만 발생(프리셋 기본은 해제) |

**배포 전 체크리스트**

- [ ] 작업 시작 창의 **사용자 로그온 시 일시중지**가 **해제**되어 있는지 확인하세요.
      프리셋/기본 정책은 해제 상태입니다. 체크하면 학생이 로그온하는 순간 작업이 멈추고
      교사가 50대를 일일이 **재개**해야 합니다.
      특별히 필요한 PC에서만 개별 옵션으로 켜고, 자세한 내용은 [`PILOT.md`](PILOT.md)의 해당 항목을 참고하세요.
- [ ] 학생에게 "재부팅 후 평소 계정으로 로그온하면 작업이 이어진다"고 안내하세요.
      암호를 UpdateAgent 화면이나 대시보드에 입력하지 않습니다.
- [ ] 대시보드/모니터에서 **사용자 로그온** 칩과 작업 상태를 확인하세요.
      칩이 없는데 작업이 진행 중이면 로그온 화면에서 서비스만 실행 중인 상태입니다.
- [ ] `C:\ProgramData\UpdateAgent\agent.json`에서 `ResumeOnStartup=true`,
      `OverlayEnabled=true`인지 확인하세요.

**의도적으로 구현하지 않는 항목**

- 학생 계정 암호 수집·저장, 자동 로그온(저장 암호), `runas` / `schtasks /RP`로 암호 전달.
- 로그온(secure desktop) 위에 오버레이를 강제로 띄우는 방식.
- 공용 계정 자동 로그온이 꼭 필요하면 별도 정책 결정 후 검토하세요. 현재 저장소에는 포함되어 있지 않습니다.

### 8-B. 개별 수동 업데이트 강제 중단

학생이 시작한 수동 업데이트도 대시보드 작업 목록에 표시됩니다. 학생용 앱 메인 화면의
**수동 업데이트 옵션** 버튼(또는 **업데이트 시작** 버튼)에서 옵션 창을 열어 업데이트 범위·재부팅·오버레이를
고를 수 있지만, 교사 정책이 상한으로 적용되어 금지된 항목·재부팅·오버레이와 기능 업데이트를 다시 켤 수는 없습니다.
해당 작업 행에서 중단할 수 있습니다.

| 경로 | 동작 | 감사 로그 |
|---|---|---|
| 대시보드 **취소** | 기존 협력적 취소. 예약 재부팅을 취소하고 다음 체크포인트에서 중단 | `cancel` |
| 대시보드 **강제 중단** | 확인 창 경고 후 예약 재부팅·남은 업데이트·오버레이 잠금을 즉시 해제 | `force-stop` |
| 오버레이 **Ctrl+Shift+U ×2** | 2초 안에 두 번 입력하면 로컬 제어 파이프로 중단 요청(force) 전송 | 학생 요청(체크인에 `취소됨`으로 반영) |

**강제 중단 시 실제로 일어나는 일**

1. 예약된 재부팅(`shutdown /a`)이 취소됩니다.
2. 입력 잠금이 즉시 해제되고, 오버레이에 **`긴급 중단으로 업데이트가 중지되었습니다.`**
   안내가 약 4초 표시된 뒤 사라집니다.
3. 작업 상태는 `취소됨`, 단계는 `강제 중단됨`으로 저장되고 다음 체크인에 대시보드/모니터에 반영됩니다.
4. **이미 설치를 시작한 Windows Update 항목은 안전한 지점까지 완료될 수 있습니다.**
   설치 도중 프로세스를 강제 종료하면 시스템이 손상될 수 있어 중단하지 않습니다.
   남은 항목은 설치되지 않습니다.

**학생 PC 운영 주의**

- 오버레이에는 긴급 중단 안내와 함께 `Ctrl+Shift+U를 2초 안에 두 번` 힌트가 표시됩니다.
- 서비스가 중단 요청을 받으면 오버레이를 숨기므로, 학생은 평소 화면으로 돌아갑니다.
- 강제 중단은 **해당 작업만** 중지합니다. 다른 PC의 작업에는 영향이 없습니다.
- 설치가 진행 중이어서 상태가 `설치 중`으로 잠시 유지될 수 있습니다. 설치가 끝나면 `취소됨`으로 바뀝니다.

**확인 방법**

```powershell
# 교사 PC: 강제 중단 감사 기록
Invoke-WebRequest "http://127.0.0.1:5080/api/admin/audit" -Headers @{ "X-Admin-Token" = "<관리자 토큰>" } `
  -UseBasicParsing | Select-Object -ExpandProperty Content
```

### 8-C. Windows 종류 구분과 업데이트 옵션 선택

학생 PC 목록에서 Windows 종류를 구분해 필터링하고, OS에 맞는 업데이트 옵션을 선택할 수 있습니다.

| OS 그룹 | 판단 근거 | 대시보드 표시 |
|---|---|---|
| Windows 11 | 빌드 22000 이상 / ProductName | 파란 칩 |
| Windows 10 (ESU) | 빌드 10240~21999 | 주황 칩 + ESU 안내 |
| LTSC / IoT LTSC | EditionID `EnterpriseS`/`IoTEnterpriseS` 등 | 보라 칩 |
| Windows Server | InstallationType/ProductName | 초록 칩 + 빌드 기준 릴리스(2016/2019/2022/2025) |
| Server Core | InstallationType `Server Core` | 회색 칩 + 헤드리스 안내 |
| 미확인 OS (`unknown`) | 위 조건에 모두 해당하지 않음 | 회색 칩 + 정보 수집 안내 |

**목록/필터 사용**

- `학생 PC` 패널 상단의 OS 칩을 누르면 해당 종류만 남습니다. `전체`로 돌아갈 수 있습니다.
- 운영체제 열에 OS 그룹 칩과 상세 이름/빌드를 함께 표시합니다.
- PC별로 저장된 옵션이 있으면 **개별 옵션** 칩이 표시됩니다.
- 행의 **옵션** 버튼으로 해당 PC의 정책을 조회·저장·초기화할 수 있습니다.
- 체크박스 선택 방식은 그대로이며, 필터는 보이는 목록만 좁힙니다. `전체 선택`도 현재 필터에 적용됩니다.

**업데이트 시작 창의 OS별 프리셋**

| 프리셋 | 포함 항목 | 비고 |
|---|---|---|
| 표준 | 보안·중요·권장 | 드라이버 제외, 일반 기본값 |
| 보안 중심 | 보안·중요 | 재부팅 상한 3회 |
| 서버 안전 | 보안·중요·권장·Defender 정의 | 자동 재부팅 없음, 오버레이 없음 |
| 전체 | 선택·드라이버·정의까지 포함 | 기능 업데이트는 여전히 제외 |

- 선택한 PC의 OS 분포가 칩으로 표시되고, 프리셋을 고르면 체크박스가 일괄 설정됩니다.
- 체크박스를 직접 바꾸면 `사용자 설정`으로 표시됩니다.
- 모든 프리셋은 **사용자 로그온 시 일시중지**를 꺼두므로, 50대 랩에서 로그온 후 교사가 일일이
  **재개**할 필요가 없습니다. 드물게 필요한 경우에만 체크하세요.
- **선택한 PC에 이 옵션을 기본값으로 저장**을 체크하면 해당 PC들의 개별 정책으로 저장됩니다.
- **선택한 PC에 저장된 개별 옵션 사용**을 체크하면 위 폼 대신 서버에 저장된 정책을 적용합니다.
  저장된 값이 없는 PC는 서버 기본 정책이 적용되며, 이 옵션을 켜면 옵션 저장 체크박스는 비활성화됩니다.
  선택한 PC 중 개별 옵션 보유 대수는 체크박스 아래 안내에 표시됩니다.
- 기능 업데이트(OS 업그레이드)는 2차 승인 워크플로 전용이므로 모든 프리셋에서 비활성화됩니다.

**PC별 옵션 저장/초기화 API**

| 메서드 | 경로 | 동작 |
|---|---|---|
| GET | `/api/admin/agents/policies` | 개별 옵션이 저장된 PC 목록 |
| GET | `/api/admin/agents/{id}/policy` | 해당 PC의 유효 정책과 출처(기본/PC별) |
| PUT | `/api/admin/agents/{id}/policy` | OS에 맞게 정리한 뒤 개별 정책 저장 |
| DELETE | `/api/admin/agents/{id}/policy` | 개별 정책 삭제 → 서버 기본 정책 사용 |

저장/초기화는 감사 로그 `agent-policy` / `agent-policy-reset`에 기록됩니다.
개별 정책은 LabManager DB의 `settings` 테이블에 `agent-policy:{agentId}` 키로 저장되며,
기존 설치를 업그레이드해도 그대로 유지됩니다.

**OS 안전 규칙(자동 적용)**

- Windows 10 / LTSC / Windows Server: 기능 업데이트 옵션과 승인 릴리스를 자동으로 제거합니다.
- Server Core: GUI가 없으므로 전체화면 오버레이를 자동으로 비활성화합니다.
- 규칙은 ① 대시보드 프리셋, ② 작업 배정 시 서버, ③ 에이전트 실행 직전(작업/검색/정책 적용)에 각각 적용됩니다.
- 서버가 옵션을 조정하면 작업 배정 응답의 `warnings`에 사유가 담겨 대시보드에 표시됩니다.
- 체크인 응답의 `Policy`도 해당 PC의 유효 정책(개별 > 기본)으로 전달됩니다.

> Windows 10 22H2는 ESU 라이선스가 필요하고 기능 업데이트 대상이 아닙니다.
> Windows Server의 릴리스 표기는 `DisplayVersion`(예: `21H2`) 대신 빌드 번호를 사용합니다.

### 8-D. 단계적 롤아웃과 유지보수 창 (선택)

기본 동작은 그대로입니다. 아래 값을 설정하지 않으면 선택한 모든 PC에 대해 작업과
`StartJob` 명령을 즉시 만들고, 시간 제한도 두지 않습니다. 값은 **작업 시작 요청의
정책**(`Policy`) 또는 서버 기본 정책(`PUT /api/admin/policy`)에 넣어 적용합니다.
PC별 정책에 같은 필드가 있어도 일괄 시작의 롤아웃 설정으로 읽지 않습니다.

| 필드 | 기본값 | 의미 |
|---|---|---|
| `RolloutBatchSize` | `0` | `0`은 전부 즉시 시작. `N>0`이면 요청 순서대로 N대씩 묶어 시작 |
| `RolloutIntervalSeconds` | `0` | 배치 사이 대기 시간(초). `0`이면 예약 배치를 바로 전송 |
| `MaintenanceWindowStart` | `null` | 유지보수 시작 시각(교사 PC 로컬 `HH:mm`) |
| `MaintenanceWindowEnd` | `null` | 유지보수 종료 시각(교사 PC 로컬 `HH:mm`) |

유지보수 시작/종료는 **반드시 함께** 설정하고, 종료 시각은 포함하지 않습니다
(예: `22:00~06:00`이면 22:00부터 다음 날 05:59까지). 자정을 넘기는 창도 지원합니다.
형식이 다르거나 한쪽만 있거나 시작과 종료가 같으면 요청을 거부합니다. 현재 로컬
시각이 창 밖이면 작업·명령·감사 생성 전에 `errors`에 한국어 사유를 담아 400으로
거부합니다. 잘못된 배치 크기/간격(음수)도 같은 방식으로 거부하며, 일부 작업만
생성되지 않습니다.

서버 기본 정책에 적용할 때의 예시입니다. 토큰 **값**은 화면에 출력하거나 파일에
기록하지 말고 토큰 파일에서 읽어 헤더에만 사용하세요.

```powershell
$adminToken = (Get-Content "<관리자 토큰 파일>" -Raw).Trim()
$policy = @{
  includeSecurity = $true
  includeCritical = $true
  includeImportant = $true
  rolloutBatchSize = 10
  rolloutIntervalSeconds = 300
  maintenanceWindowStart = "22:00"
  maintenanceWindowEnd = "06:00"
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Put `
  -Uri "http://127.0.0.1:5080/api/admin/policy" `
  -Headers @{ "X-Admin-Token" = $adminToken } `
  -ContentType "application/json" -Body $policy
```

**배치 동작**

- 대시보드에 표시할 모든 대상 작업을 먼저 `Pending`으로 만듭니다. 따라서 아직
  시작되지 않은 PC도 작업 목록에서 바로 확인할 수 있습니다.
- 배치 1의 `StartJob`만 요청 중에 명령 큐에 넣습니다. 나머지 배치는 작업 ID, 순번,
  예약 시각, 명령 본문을 LabManager DB의 `job_rollout` 테이블에 저장합니다.
- 예약 시각은 `작업 시작 시각 + (배치 순번 - 1) × RolloutIntervalSeconds`입니다.
  LabManager의 백그라운드 서비스가 약 1초 간격으로 도래한 배치를 읽어 명령을
  발급하고, 모니터에 변경을 알립니다.
- 응답의 `warnings`에는 `2/3개 배치가 서버에 대기 중입니다.`와 같은 안내가 담기고,
  `totalBatches`/`dispatchedBatches`/`pendingBatches` 필드로 배치 진행 상황을
  확인할 수 있습니다. 토큰 값은 포함되지 않습니다.

**재시작과 중복 방지**

명령 삽입과 예약의 `dispatched_at` 기록은 같은 SQLite 트랜잭션에서 처리합니다.
서버 재시작, 틱 중복, 같은 시각의 재요청이 있어도 이미 발급된 배치는 다시
전송되지 않습니다. 서버가 꺼져 있어 예약 시각을 놓친 배치는 다음 시작 시 첫 틱에
발급됩니다(지연 시작). 이미 발급된 명령이 에이전트에 아직 전달되지 않았다면
기존 만료 시간(12시간) 안에 체크인 시 전달됩니다.

**남은 배치 중단**

아직 시작되지 않은 배치도 작업이 미리 만들어져 있으므로, 해당 작업 행의
**취소** 또는 **강제 중단**을 누르면 됩니다. 서버는 아직 발송되지 않은 예약에
`cancelled_at`을 기록해 이후 `StartJob`이 나가지 않게 하고, 감사 로그에
`남은 예약 배치 중단`을 남깁니다. 이미 발급된 배치는 되돌릴 수 없으므로 해당
작업의 취소/강제 중단 명령으로 에이전트에서 중지하세요. 취소된 작업은 다음 틱에서
건너뛰며, 취소 전에 이미 시작된 다른 PC의 작업에는 영향이 없습니다.

> 기능 업데이트는 계속 비활성이고, Server Core의 전체화면 오버레이도 계속
> 비활성입니다. 예약 명령 본문에는 토큰이 저장되지 않고, 감사 로그에도 토큰을
> 기록하지 않습니다. 기본/샘플 토큰은 여전히 서버 시작 단계에서 거부됩니다.

### 8-E. 작업 타임라인·관측 (교사용)

작업 시작 요청에 `correlationId`를 넣지 않으면 LabManager가 서버에서 상관관계 ID를
발급합니다. 같은 값이 작업, 감사 로그, `StartJob`/취소 명령 payload의 `correlationId`,
작업 상세 화면에 함께 기록되므로 한 작업의 흐름을 추적할 수 있습니다. 요청 값은
`[A-Za-z0-9-_]` 형식만 허용되고, 형식이 잘못되면 서버가 새로 발급합니다.

교사 대시보드의 작업 상세에는 `작업 타임라인`과 `상관관계 ID`가 표시됩니다.
타임라인은 작업 생성, 첫 명령 발급, 예약 배치 발급, 교사의 일시중지·재개·취소,
에이전트가 보고한 상태 변화, 시작 명령 거부 실패를 UTC 시각과 함께 기록합니다.
같은 전이는 `job_transitions`의 중복 키로 한 번만 저장됩니다. 기록이 없으면
화면에 `없음`이라고 표시하며, 빈 상태를 실제 상태로 오인하지 않습니다.

기간 리포트는 관리자 토큰이 필요한 `/api/admin/reports/jobs`에서 조회합니다.

```powershell
$adminToken = (Get-Content "<관리자 토큰 파일>" -Raw).Trim()
Invoke-RestMethod `
  -Uri "http://127.0.0.1:5080/api/admin/reports/jobs?from=2026-11-27T00:00:00Z&to=2026-11-28T00:00:00Z&limit=100" `
  -Headers @{ "X-Admin-Token" = $adminToken }
# CSV는 동일 경로에 &format=csv를 붙입니다.
```

`from`/`to`는 UTC ISO-8601이고 날짜만 주면 `to`는 그 날의 마지막 순간으로
처리됩니다. `limit`은 1~500으로 제한되며 기본값은 100입니다. 응답의
`totalCount`는 기간 내 전체 작업 수이고 `returnedCount`는 실제 반환 건수입니다.
목록이 잘리면 `truncated=true`와 `omittedCount`(생략된 건수)가 함께 내려와
일부 목록을 전체로 오인하지 않게 합니다. 없는 값은 빈 문자열이 아니라
`없음`/`확인 불가`로 표시하고, 에이전트 보고값이 있으면 추정값보다 우선합니다.

관측 로그는 `%LOCALAPPDATA%\UpdateAgent\LabManager\logs\observability.jsonl`에
JSONL 한 줄 이벤트(UTC 시각, 수준, 이벤트명, 작업/에이전트 ID, 상관관계 ID)로
기록됩니다. 현재 파일은 256 KiB를 넘으면 회전되어 `observability.1.jsonl`부터
최대 5개까지 보관되고 기존 콘솔/일반 파일 로깅은 그대로 유지됩니다. 디렉터리에
쓰지 못하면 한 번만 경고하고 서버는 계속 동작합니다.

> 토큰 값, 요청 본문, 원시 헤더는 관측 로그·리포트·화면·감사에 남기지 않습니다.
> 리포트 호출도 관리자 토큰이 필요한 `/api/admin` 경로에 있으며, 토큰은 헤더로만
> 전달하고 응답에는 포함하지 않습니다.

### 8-F. 에이전트 변조·이탈 탐지 (교사용)

서비스는 체크인마다 허용 목록에 있는 비밀성이 없는 설정(주소, 에이전트 ID, 오버레이
옵션 등)과 실행 중 유효 설정으로 구성 지문을 계산해 보냅니다. LabManager는 지문을
읽을 수 있는 첫 체크인의 값을 기준선으로 저장하고, 이후 값이 달라지면 `설정 변경`을
표시합니다. 지문을 못 읽은 체크인은 기준선을 만들지 않습니다.
지문을 읽거나 만들지 못하면 `무결성 확인 불가`로 표시하며 정상으로 단정하지 않습니다.
기대 버전은 `LabManager:ExpectedAgentVersion`으로 선택적으로 설정합니다. 설정하지
않거나 에이전트 보고값이 없으면 `버전 확인 불가`이고, 값이 다르면 `버전 불일치`입니다.
90초를 넘게 체크인이 없으면 이 화면의 다른 상태와 무관하게 `이탈 · 체크인 없음`으로
표시합니다. 모니터와 대시보드의 PC 목록에서 네 가지 표시를 확인할 수 있습니다.

**탐지할 수 있는 것**: 허용 목록 설정의 변경, 기대 버전과 다른 에이전트 버전,
체크인 공백(서비스 정지·PC 종료·네트워크 차단·설치 제거 등으로 발생), 그리고
지문 자체를 확인할 수 없는 상태입니다.

**탐지할 수 없는 것**: 학생은 로컬 관리자이므로 서비스를 멈추거나 제거하고,
설정·에이전트 파일·체크인 보고 자체를 바꿀 수 있습니다. 이 기능은 이를 막지
않고, 다음 체크인이 도착하거나 체크인이 끊긴 뒤에만 알려줍니다. 첫 체크인 이전의
변조는 기준선 오염을 막을 수 없으므로 기준선 시각과 감사 로그를 함께 확인하세요.
토큰 값은 지문·기준선·감사·응답에 포함되지 않습니다. 토큰처럼 보이는 값을 만나면
원문을 남기지 않고 `확인 불가`로 처리합니다.

변경을 교사가 확인했다면 관리자 토큰으로 기준선을 재설정합니다. 현재 지문이 새
기준선이 되고 `설정 변경` 플래그가 해제됩니다. 현재 지문이 확인 불가이면 기준선을
지우고 다시 확인 불가로 남습니다.

```powershell
$adminToken = (Get-Content "<관리자 토큰 파일>" -Raw).Trim()
Invoke-RestMethod `
  -Uri "http://127.0.0.1:5080/api/admin/agents/<PC ID>/integrity/baseline" `
  -Method Post -Headers @{ "X-Admin-Token" = $adminToken }
```

### 8-G. 관리자 토큰 회전 (선택, 옵트인)

자동 회전은 **기본값이 사용 안 함**입니다. 오래된 설치가 업그레이드만으로 90일이 지났다고
토큰을 바꾸면 교사 대시보드에 저장된 토큰이 갑자기 무효해지므로, 반드시 교사가 명시적으로
켜야 합니다. `C:\Program Files\UpdateAgent\LabManager\appsettings.json`의 `LabManager` 섹션에
아래 값을 추가한 뒤 LabManager를 다시 시작하세요. **토큰 값은 여기에 쓰지 않습니다.**

```json
{
  "LabManager": {
    "TokenRotation": {
      "Enabled": true,
      "CheckIntervalMinutes": 60
    }
  }
}
```

- `Enabled`: `true`일 때만 백그라운드 서비스가 조건을 확인합니다. 기본값 `false`.
- `CheckIntervalMinutes`: 확인 주기(분). 기본값 60, 최소 5, 최대 1440으로 제한됩니다.
  서비스가 시작된 직후에는 확인하지 않고 첫 주기가 지난 뒤에만 확인합니다.
- 회전 조건: 자동 회전이 켜져 있고, 마지막 변경 시각을 알 수 있으며, **90일을 넘었고**,
  런타임 override 파일에 유효하고 기본값이 아닌 토큰이 있을 때만 한 번 회전합니다.
  조건이 하나라도 없으면 `확인 불가`/`없음`으로 두고 추정하지 않습니다.
- 90일 기준은 마지막 변경 시각부터입니다. 시작 시각이나 설치 시각으로 추정하지 않습니다.

교사가 대시보드의 **지금 회전** 버튼(또는 관리자 토큰이 필요한
`POST /api/admin/token/rotate`)을 누르면 자동 설정과 무관하게 즉시 회전합니다.
새 토큰은 교사 PC의 `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.json`
**첫 줄**에 원자적 이동과 ACL 제한을 유지하며 기록됩니다. 서버 응답에는 새 토큰이
없으므로, 교사는 이 설정 파일에서 새 토큰을 안전하게 확인한 뒤 대시보드에 **다시 입력**해야
합니다. 이전 토큰은 브라우저 저장소에서 지워지며 그대로 계속 사용되지 않습니다.
수동으로 정한 토큰을 GitHub 시크릿/보관소에 다시 등록할 때도 값 자체를 로그나 이슈에
붙여넣지 마세요.

감사 기록은 두 곳에 남습니다. `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.audit.log`에는
`token-rotated` 이벤트와 SHA-256 지문 앞 12자리만 기록되고, LabManager DB의 감사에는
토큰 값 없이 `token-rotate`가 기록됩니다. 실패하면 이전 토큰을 같은 원자적 경로로 되돌리고
`rolled-back`을 남기며, 복구까지 실패하면 인증을 중단합니다(fail-closed).

```powershell
$adminToken = (Get-Content "<관리자 토큰 파일>" -TotalCount 1).Trim()
Invoke-RestMethod `
  -Uri "http://127.0.0.1:5080/api/admin/token/rotate" `
  -Method Post -Headers @{ "X-Admin-Token" = $adminToken }
```

> 토큰 원문은 로그·리포트·명령줄·저장소·화면·감사에 남기지 않습니다. 로그에는 길이만
> 기록하고, API/대시보드 상태에는 회전 사용 여부·마지막 변경 시각·회전 필요 여부만
> 포함합니다. 값이 없으면 `없음`/`확인 불가`로 표시합니다.

### 8-H. 필터 URL 공유 / 다중 모니터 동기화

대시보드에서 OS 필터 칩을 누르면 주소에 `?os=server`처럼 **허용된 OS 식별자만** 남습니다.
전체 필터(`all`)는 파라미터를 만들지 않으므로 쿼리 없는 기존 주소와 동하게 유지됩니다.
작업 상세 딥 링크 `?job=<작업 ID>`는 그대로 유지되며 필터를 바꿔도 사라지지 않습니다.
뒤로/앞으로 가기는 필터 상태를 URL에서 다시 읽어 복원합니다(필터만 바꿀 때는 히스토리에 새 항목을 남기지 않습니다).

모니터(`/monitor.html`)는 검색어를 `?q=`, 상태 필터를 `?state=`에 담습니다.
상태 값은 `all`·`busy`·`attention`·`offline`만 허용하고, 검색어는 64자 이하의 문자만
허용합니다. `필터 링크 복사` 버튼은 현재 필터가 담긴 주소만 클립보드에 복사합니다.
클립보드 API가 없거나 복사가 거부되면 성공한 것처럼 표시하지 않고 `확인 불가`와
주소창 수동 복사 안내를 보여 줍니다.

여러 모니터 창/탭은 같은 브라우저 프로필에서 `BroadcastChannel`로 필터를 공유합니다.
창을 열거나 검색어/상태를 바꾸면 다른 창의 표와 URL이 함께 갱신됩니다.
채널을 쓸 수 없는 브라우저에서는 `storage` 이벤트로 동일하게 동기화하며,
다른 창이 없으면 아무 메시지도 보내지 않습니다. 같은 탭이 보낸 메시지는 보낸 탭 식별자
(source id)로 무시하고, 원격에서 받은 필터는 다시 브로드캐스트하지 않으므로 메아리 루프가
생기지 않습니다.

> **토큰은 절대 URL·쿼리·클립보드·브로드캐스트·필터 저장 값에 넣지 않습니다.**
> 관리자 토큰은 대시보드에서 기존처럼 브라우저 로컬 저장소에만 보관하고, 각 창에서
> 따로 입력합니다. 링크를 받은 사람이 토큰을 함께 전달받지 않으므로 다른 프로필에서는
> 토큰 입력 화면이 나타날 수 있습니다. 이는 정상 동작입니다.

공유 링크에 알 수 없는 필터 값이 있으면 `확인 불가` 안내를 보여 주고 해당 값은 무시하며
`없음`/`전체`로 표시합니다. 정상 데이터라고 추측하지 않습니다. 복구 방법은 다음과 같습니다.

1. 주소창에서 알 수 없는 `os=`, `q=`, `state=` 값을 지우거나 올바른 값으로 바꿉니다.
2. 대시보드는 OS 칩의 `전체`, 모니터는 상태 선택의 `전체`와 검색어 지우기를 누릅니다.
3. 그래도 필터가 이상하면 새로고침 후 필터 링크를 다시 복사해 공유합니다.

## 9. 보안 주의

- `agent-token.txt`는 **공유 에이전트 토큰**입니다. 배포 폴더와 USB를 보호하고
  설치 확인 후 반드시 삭제하세요.
- 설치 exe와 같은 폴더의 `agent-install.ini`에는 토큰 **파일 경로만** 두고, 토큰 값은 별도 파일의
  첫 줄에만 두세요. 명령줄을 쓰더라도 `/AgentTokenFile`만 사용하고 토큰 값을 명령줄/로그에 남기지 마세요.
- 학생이 로컬 관리자이므로 `agent.json`과 서비스 설정을 직접 볼 수 있습니다.
  ACL은 관리자/SYSTEM으로 제한되어 있지만, 관리자 권한 자체는 막을 수 없습니다.
- 학생 계정 암호는 저장하지 않고, 자동 로그온 설정이나 대시보드 암호 필드를 두지 않습니다.
- 샘플 토큰/기본 토큰으로 실 배포하지 마세요.

## 10. 변경 이력

- 2026-11-21: 교사 PC 구성/검증(`setup-labmanager-lab.ps1`), 인벤토리 기반 패키지 생성
  (`deploy-agents.ps1`), 샘플 CSV, 이 런북 추가. 제품 코드 변경 없음(버전 유지 `0.1.13`).
- 2026-11-21: Windows 종류 구분/필터, OS별 프리셋, PC별 옵션 저장, OS 안전 규칙 추가(`v0.1.15`).
- 2026-11-21: 혼합 OS 선택 시 오버레이 옵션이 불필요하게 꺼지던 대시보드 문제 수정(`v0.1.16`).
- 2026-11-21: 5회 자아성찰 결과 반영 — 저장된 PC별 옵션 재사용, 프리셋 로그온 일시중지 기본 해제,
  OS 안내 문구/정책 해석 정확도 개선(`v0.1.17`).
- 2026-11-22: 설치 exe와 같은 폴더의 `agent-install.ini` 자동 읽기와 PC별 패키지 생성 추가(`v0.1.23`).
- 2026-11-22: 자아성찰 심층 감사 반영 — 설치 전 검증, 그룹 방화벽 규칙 실제 등록, 토큰 ACL/삭제 확인,
  업그레이드 시 기존 설정 보존, `/Upgrade` 래퍼 옵션(`v0.1.26`).
- 2026-11-22: 자아성찰 6~10회 결함 수정 — LabManager 첫 설치 fail-closed, 기능 업데이트 2차 승인 강제,
  명령 결과 소유자 검증, 서버 정책 영속화/페일클루즈, 체크인 직렬화, 수동 일시정지 유지(`v0.1.27`).
- 2026-11-23: 자아성찰 20회 결함 수정 — 학생 인바운드 방화벽 제거, 실행 정책 일원화,
  명령 결과 수락 검증, check-in ID 상한, 릴리스 노트 자동화(`v0.1.31`).
- 2026-11-24: 업데이트 설치 시 기존 토큰 재사용/교체 선택 화면 추가(`v0.1.32`).
- 2026-11-24: 교체 선택 시 릴리스 내장 토큰 사용, 외부 토큰 파일은 선택 사항으로 변경(`v0.1.33`).
- 2026-11-24: 자아성찰 25회 결함 수정 — 재사용/교체 토큰 규칙 정리, 체크인 결과·감사 제한·UTC 일관성,
  시크릿 검증과 설치 안전성 보강, 문서 정합성 개선(`v0.1.34`).
- 2026-11-27: 개선 1 — `scripts/load-rehearsal.ps1` 추가, 50대 합성 체크인 부하 리허설 런북과
  합격 기준 정리(제품 버전 변경 없음, 실 PC 증적과 구분).
- 2026-11-24: 순수 창작 공통 Guard DLL 도입 — 토큰·URL·UTC 규칙 일원화, 런타임 관리자 토큰 충돌 차단,
  브라우저 앱 모드 주소 검증(`v0.1.35`).
- 2026-11-24: 순수 DLL 5회 반복 감사 — Guard 규칙 보강과 공유 적용, Core UTC/정책/ID 일원화,
  LabManager 체크인 검증·ack·캐시 정리, UI 실패 처리·주소/창 크기 검증, 문서·패키징 보강(`v0.1.36`).
- 2026-11-24: LabManager 토큰 교체 경로 대소문자 무관 키 검색과 단계별 진단 로그 보강(`v0.1.37`).
- 2026-11-27: 개선 3 — 옵트인 단계적 롤아웃/유지보수 창, 배치 예약 영속화와 중복 방지,
  남은 배치 취소 전파·회귀 테스트 추가(제품 버전 변경 없음).
- 2026-11-28: 개선 5 — 구성 지문 기준선·버전 불일치·이탈 탐지와 교사 기준선 재설정,
  비밀값 비포함 규칙 및 회귀 테스트 추가(제품 버전 변경 없음).

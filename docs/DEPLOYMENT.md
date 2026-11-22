# 50대 랩 배포 런북 (학생 PC ≈50대 + 교사 PC 1대)

이 문서는 설계한 **허브-스포크 연결 방식**을 실제로 배포하는 절차입니다.
학생 PC는 교사 PC를 향해서만 폴링하고, 교사 PC만 TCP 5080 인바운드를 엽니다.
학생 PC에는 인바운드 방화벽 규칙이 필요하지 않습니다.

- 개발자: AhBiYout / GitHub: <https://github.com/ahbiyout-all> / 블로그: <https://ahbiyoutvibe.blogspot.com/>
- 1대 파일럿은 [`PILOT.md`](PILOT.md), 설치 프로그램 옵션은 [`INSTALLER.md`](INSTALLER.md) 참고

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

| 항목 | 값 |
|---|---|
| 이름 | 교사 PC를 `labmanager` 이름으로 확인 (없으면 IP 직접 사용) |
| 주소 | `http://labmanager:5080` 또는 `http://<교사-PC-IP>:5080` |
| 교사 PC IP | 고정 IP 또는 DHCP 예약 (예: `192.168.0.10`) |
| 포트 | TCP 5080 |
| 폴링 주기 | 30초 (최소 15초), 온라인 판정 90초 |
| 에이전트 토큰 | 모든 학생 PC에 **동일**하게 배포 |
| 관리자 토큰 | 교사 PC에만 보관 (대시보드/API 로그인) |

> 위 표의 첫 항목은 DNS 별칭을 뜻합니다. 내부 DNS가 없으면 라우터에서 `labmanager`를
> 교사 PC IP로 매핑하거나, 각 학생 PC의 hosts 파일에 같은 항목을 추가하세요.
> DNS를 쓰기 어렵다면 `-LabManagerUrl http://<교사-PC-IP>:5080`로 IP를 직접 지정하면 됩니다.

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

기본값(`change-me-*`)과 설치 파일에 들어 있는 샘플 값(`sample-*-token-replace-before-pilot`)은
절대 그대로 사용하지 마세요. 두 토큰은 서로 달라야 합니다.

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

패키지를 생성합니다.

```powershell
.\scripts\deploy-agents.ps1 `
  -InventoryCsv ".\inventory.csv" `
  -AgentTokenFile "$env:TEMP\updateagent-lab\agent-token.txt" `
  -LabManagerUrl "http://labmanager:5080"
```

결과 (`deploy\agents\` — Git에서 제외됨):

```text
deploy\agents\
  README.txt
  manifest.csv
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
LabManagerUrl=http://labmanager:5080
AgentTokenFile=agent-token.txt
```

상대 경로는 설치 exe가 있는 폴더 기준입니다. **토큰 값은 설정 파일에 쓰지 않고** 토큰 파일에만 둡니다.

`install-agent.cmd`는 관리자 권한을 확인한 뒤 다음을 수행합니다.

1. `agent-token.txt`에 관리자/SYSTEM만 접근 가능하도록 ACL 적용
2. `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`로 조용히 설치
3. 같은 폴더의 `agent-install.ini`에서 주소와 토큰 파일 경로를 자동으로 읽음 (**명령줄 인자 없음**)
4. 성공하면 토큰 파일 즉시 삭제, 실패하면 재시도 위해 보관(`agent-install.ini`는 유지)

DNS 별칭이 없다면 `-LabManagerUrl "http://192.168.0.10:5080"`로 IP를 지정하세요.
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
- **버전 업그레이드**: 새 인스톨러를 같은 패키지 경로에 넣고 다시 실행하면
  기존 설치를 제거한 뒤 설치하고 `agent.json`은 유지됩니다.

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
  선택한 PC 중 개별 옵션 보유 대수는 체크box 아래 안내에 표시됩니다.
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

## 9. 보안 주의

- `agent-token.txt`는 **공유 에이전트 토큰**입니다. 배포 폴더와 USB를 보호하고
  설치 확인 후 반드시 삭제하세요.
- `/AgentTokenFile`만 사용하고 토큰 값을 명령줄/로그에 남기지 마세요.
- 학생이 로컬 관리자이므로 `agent.json`과 서비스 설정을 직접 볼 수 있습니다.
  ACL은 관리자/SYSTEM으로 제한되어 있지만, 관리자 권한 자체는 막을 수 없습니다.
- 학생 계정 암호는 저장하지 않고, 자동 로그온 설정이나 대시보드 암호 필드를 추가하지 마세요.
- 샘플 토큰/기본 토큰으로 실 배포하지 마세요.

## 10. 변경 이력

- 2026-11-21: 교사 PC 구성/검증(`setup-labmanager-lab.ps1`), 인벤토리 기반 패키지 생성
  (`deploy-agents.ps1`), 샘플 CSV, 이 런북 추가. 제품 코드 변경 없음(버전 유지 `0.1.13`).
- 2026-11-21: Windows 종류 구분/필터, OS별 프리셋, PC별 옵션 저장, OS 안전 규칙 추가(`v0.1.15`).
- 2026-11-21: 혼합 OS 선택 시 오버레이 옵션이 불필요하게 꺼지던 대시보드 문제 수정(`v0.1.16`).
- 2026-11-21: 5회 자아성찰 결과 반영 — 저장된 PC별 옵션 재사용, 프리셋 로그온 일시중지 기본 해제,
  OS 안내 문구/정책 해석 정확도 개선(`v0.1.17`).

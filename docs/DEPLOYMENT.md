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
- [ ] 학생 로그인 계정의 **암호는 수집하지 않습니다.** 서비스는 LocalSystem으로 실행되며
      `WTSQueryUserToken`으로 사용자 세션 토큰을 얻습니다.
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
    agent-token.txt
    install-agent.cmd
  STUDENT-02\ ...
```

`install-agent.cmd`는 관리자 권한을 확인한 뒤 다음을 수행합니다.

1. `agent-token.txt`에 관리자/SYSTEM만 접근 가능하도록 ACL 적용
2. `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`로 조용히 설치
3. `/LabManagerUrl=<주소>`와 `/AgentTokenFile=<파일>` 전달 (**토큰 값은 명령줄에 없음**)
4. 성공하면 토큰 파일 즉시 삭제, 실패하면 재시도 위해 보관

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
- **잠금 화면**: 오버레이는 로그온 데스크톱에만 표시됩니다. 잠금/secure desktop 위에는
  나타나지 않지만 작업은 계속 진행되고, 잠금 해제 후 다시 표시됩니다.
- **토큰 교체**: 관리자 토큰은 대시보드의 **서버 토큰 변경**에서 즉시 적용됩니다.
  에이전트 토큰을 바꾸면 모든 학생 PC에 같은 값을 다시 배포해야 합니다.
- **재부팅/절전**: 서비스는 자동 시작되고 `ResumeOnStartup=true`라 재로그온 후 작업을 재개합니다.
- **WoL**: 같은 서브넷에서 동작합니다. VLAN을 넘으려면 라우터에서 UDP 9/7 전달/릴레이가 필요합니다.
- **버전 업그레이드**: 새 인스톨러를 같은 패키지 경로에 넣고 다시 실행하면
  기존 설치를 제거한 뒤 설치하고 `agent.json`은 유지됩니다.

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

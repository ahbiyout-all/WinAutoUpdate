# UpdateAgent 설치 프로그램 (Inno Setup)

이 문서는 Windows PC에 UpdateAgent를 배포하는 공식 설치 프로그램 설계와 사용법을 정리합니다.
Inno Setup 6 기반 설치 프로그램이 **권장 방식**이며, 기존 WiX MSI는 대안으로 유지됩니다.

- 개발자: AhBiYout
- GitHub: <https://github.com/ahbiyout-all>
- 공식 블로그: <https://ahbiyoutvibe.blogspot.com/>

## 1. 산출물

`scripts\build-inno.ps1`이 만드는 설치 파일은 두 개입니다.

| 설치 파일 | 대상 PC | 설치 위치 |
|---|---|---|
| `UpdateAgent.Agent-Setup-<버전>-x64.exe` | 학생 PC | `C:\Program Files\UpdateAgent` |
| `UpdateAgent.LabManager-Setup-<버전>-x64.exe` | 교사 PC | `C:\Program Files\UpdateAgent\LabManager` |

두 파일 모두 **개별 파일(Unpacked Multi-File)** 형태로 설치됩니다. 설치 후 앱은 폴더 안의
실행 파일과 DLL을 그대로 사용하며, 포터블 단일 실행 파일로 묶지 않습니다.

빌드 결과:

```text
artifacts\inno\
  UpdateAgent.Agent-Setup-<버전>-x64.exe
  UpdateAgent.Agent-Setup-<버전>-x64.exe.sha256
  UpdateAgent.LabManager-Setup-<버전>-x64.exe
  UpdateAgent.LabManager-Setup-<버전>-x64.exe.sha256
  build-info.json
```

## 2. 고유 AppID

업그레이드와 제거가 올바르게 연결되도록 제품별 고유 AppID를 사용합니다.
**AppID는 절대 변경하지 않습니다.**

| 제품 | Inno AppID |
|---|---|
| UpdateAgent Agent | `{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}` |
| UpdateAgent LabManager | `{A4D7F1C3-2E85-4B60-9C1A-7F3E5D8B2A94}` |

설치/제거 정보는 Windows **설정 → 앱 → 설치된 앱(프로그램 추가/제거)** 에
`UpdateAgent Agent <버전>`, `UpdateAgent LabManager <버전>` 이름으로 등록됩니다.

## 3. 설치 폴더 구조

### 학생 PC (Agent)

```text
C:\Program Files\UpdateAgent\
  Service\            UpdateAgent.Service.exe + 런타임 DLL
  SessionHelper\      UpdateAgent.SessionHelper.exe + 런타임 DLL
  Desktop\            UpdateAgent.Agent.Desktop.exe + 런타임 DLL (상태 확인 창)
  Cli\                UpdateAgent.Cli.exe + 런타임 DLL (고급 점검용)
  unins000.exe        제거 프로그램

C:\ProgramData\UpdateAgent\
  agent.json          LabManager URL / 에이전트 토큰 (ACL 제한)
  logs\               서비스 로그
  (작업 상태/감사 데이터)
```

- `agent.json`과 `C:\ProgramData\UpdateAgent`는 업그레이드/제거 후에도 **보존**됩니다.
- 서비스 이름: `UpdateAgentService` (자동 시작, LocalSystem, 실패 시 자동 재시작).

### 교사 PC (LabManager)

```text
C:\Program Files\UpdateAgent\LabManager\
  UpdateAgent.LabManager.Desktop.exe  브라우저 앱 모드 런처 (바로가기 대상)
  UpdateAgent.LabManager.exe          HTTP API 서버 + 런타임 DLL
  wwwroot\                            대시보드/모니터(index.html, app.js, monitor.html, monitor.js, styles.css)
  appsettings.json                    관리자/에이전트 토큰, 수신 URL
  unins000.exe
```

- 교사용 화면은 시스템의 Edge/Chrome을 `--app=` 단독 창 모드로 실행합니다. WebView2는 사용하지 않습니다.
- 런처는 창 없이 동작하며, 서버가 없으면 `UpdateAgent.LabManager.exe --headless`를 자동 실행합니다.
  이미 실행 중인 서버는 재사용합니다. 앱 모드 창을 닫아도 서버는 계속 실행됩니다.
- 관리자 토큰은 전용 브라우저 프로필(`%LOCALAPPDATA%\UpdateAgent\LabManager\browser-profile`)의
  localStorage에 저장됩니다. 런처 설정은 같은 폴더의 `desktop-settings.json`에 저장됩니다.
- 대시보드의 **작업 현황 모니터** 버튼 또는 런처 `--monitor` 인자로 전용 모니터 창(`/monitor.html`)을 열 수 있습니다.
  모니터는 `GET /api/admin/live` SSE 스트림으로 작업/에이전트 변경을 실시간 수신하며, 같은 브라우저 프로필의 토큰을 공유합니다.
- `appsettings.json`은 신규 설치 시에만 생성되며, 업그레이드 시 기존 설정을 덮어쓰지 않습니다.
- API/대시보드 기본 포트: **5080/TCP**

## 4. 설치 진행 순서

요구된 순서를 그대로 따릅니다.

1. **기존 앱 강제 종료**
   - `taskkill /F`로 SessionHelper/CLI/Service 프로세스를 종료합니다.
   - LabManager는 `UpdateAgent.LabManager.Desktop.exe`와 `UpdateAgent.LabManager.exe`를 모두 종료합니다.
   - Agent는 `sc stop UpdateAgentService`를 먼저 실행합니다.
2. **라이선스 고지**
   - `installer\UpdateAgent.Agent.Installer\payload\notice.ko.rtf`를 표시합니다.
3. **기존 설치 자동 제거 (설치 직전)**
   - Inno Setup 설치 키와 MSI 등록 정보를 모두 검사합니다.
   - 기존 설치가 있으면 **묻지 않고** 이전 제거 프로그램을 무인 실행해 먼저 제거합니다.
   - Agent: 레지스트리 등록 정보만 남고 제거 프로그램 파일이 없거나, MSI가 `1605`(이미 설치되지 않은 제품)를
     반환하면 이미 제거된 설치로 보고 경고(로그) 후 **계속 설치합니다.**
   - LabManager: 제거 프로그램을 찾지 못하면 **설치를 중단합니다**(fail-closed).
   - 제거 전 `appsettings.json`을 임시 폴더에 백업하고, 제거 후 복원합니다.
     백업 삭제가 실패하면 이전 토큰이 임시 폴더에 남으므로 설치를 중단합니다.
   - 실제 제거 실행이 실패하면 새 설치를 중단하고 오류를 표시합니다.
     종료 대상 앱을 끝내지 못하면 방화벽 규칙이나 서비스 복구 설정을 바꾸지 않고 중단합니다.
4. **설치**
   - 개별 파일을 `Program Files`에 복사합니다.
   - Agent는 `[InstallDelete]`로 이전 버전의 Desktop/Service/SessionHelper/Cli 폴더를 정리한 뒤 복사합니다.
      따라서 이전 버전의 `UpdateAgent.Agent.Desktop.exe/DLL`이 남아 새 버튼이 보이지 않는 일이 없습니다.
5. **바탕화면 바로가기 선택**
   - `바로가기:` 작업에서 체크/해제할 수 있습니다(기본 체크).
6. **서비스·방화벽·설정 적용** (Agent)
   - 서비스 등록/갱신, 이전 버전 방화벽 규칙 정리, 선택적 `agent.json` 작성.
7. **앱 실행**
   - Agent: 서비스를 시작한 뒤 `sc.exe query` 결과로 실제 `RUNNING` 상태를 확인합니다(v0.1.43).
     확인하지 못하면 설치를 중단합니다. 마지막 화면에서 `UpdateAgent 상태 확인` 데스크톱 앱을 실행할 수 있습니다.
     시작 메뉴/바탕화면 바로가기도 같은 앱(`{app}\Desktop\UpdateAgent.Agent.Desktop.exe`)을 엽니다.
      이 앱은 상태 조회와 함께 메인 화면의 **수동 업데이트 옵션** 버튼을 제공하며, 기존 **업데이트 시작** 버튼도 같은 옵션 창을 엽니다.
       로컬 제어 파이프(`UpdateAgent.LocalControl.v1`)로
      실행 중인 서비스에 작업을 요청합니다. 시작 전 **수동 업데이트 옵션** 창에서 프리셋과 세부 항목을 고를 수 있고,
      선택 값은 파이프 요청의 `options`로 전달됩니다. 학생 계정(UAC 필터링 토큰 포함)이 연결할 수 있도록 파이프 ACL에
      인증 사용자가 포함됩니다.
   - LabManager: 첫 설치에는 **실제 AdminToken/AgentToken 두 개가 내장되어 있어야 합니다**.
     값이 비어 있거나 `change-me-*`, `sample-*-replace-before-pilot`이면 설치가 중단됩니다(fail-closed).
     공개된 샘플 값으로 서버가 뜨는 일은 없으므로, 토큰 secret을 설정해 빌드한 설치 파일을 사용하세요.
     업데이트의 재사용/무인 설치는 내장 토큰을 읽지 않고 기존 `appsettings.json`을 그대로 유지합니다.
     교체를 선택하면 릴리스 내장 두 줄(또는 명시한 외부 토큰 파일)로 교체합니다.
     바로가기는 **런처**(`UpdateAgent.LabManager.Desktop.exe`)를 가리키며, 런처가 Edge/Chrome 앱 모드 창으로 대시보드를 엽니다.

## 5. 기존 설치 처리

| 기존 상태 | 동작 |
|---|---|
| Inno Setup Agent/LabManager 설치됨 | 이전 제거 프로그램을 무인 실행해 제거한 뒤 새로 설치 |
| WiX MSI Agent 설치됨 | MSI 제품을 `msiexec /x ... /qn`으로 자동 제거한 뒤 Inno 설치 |
| Agent: 등록 정보만 남고 제거 프로그램 파일이 없음 / MSI `1605` | 이미 제거된 설치로 보고 건너뛴 뒤 새로 설치(경고/로그만 기록) |
| LabManager: 등록 정보만 남고 제거 프로그램 파일이 없음 | 설치 중단(fail-closed). 정리 후 다시 실행 |
| 레거시 스크립트 설치(`C:\ProgramData\UpdateAgent\app`) | 감지 후 안내만 표시, 필요 시 수동 제거 |
| 모든 경우(대화형/무인) | 제거가 필요한 경우 묻지 않고 제거한 뒤 설치(설정 데이터는 보존) |

- 설치 순서는 **실행 중인 앱 종료 → 이전 버전 언인스톨 → 새 파일 설치**입니다.
  - 일반(대화형) 설치에서는 각 단계 직전에 안내 화면을 표시하고 **5초 후 자동으로 다음 단계로 진행**합니다.
    안내 단계는 `기존 앱 종료 → 기존 설치 제거 → 새 버전 설치`이며, 사용자가 버튼을 누를 필요가 없습니다.
    대기 시간은 `.iss` 상단의 `NoticeDelaySeconds` 값으로 조정할 수 있습니다.
  - `/VERYSILENT /SUPPRESSMSGBOXES` 무인 설치에서는 안내 화면과 대기 시간을 완전히 생략합니다.
  - 종료 대상: Agent `UpdateAgent.SessionHelper.exe`, `UpdateAgent.Agent.Desktop.exe`,
    `UpdateAgent.Cli.exe`, `UpdateAgent.Service.exe` / LabManager
    `UpdateAgent.LabManager.Desktop.exe`, `UpdateAgent.LabManager.exe`.
  - `sc stop`으로 서비스를 먼저 내린 뒤 남은 프로세스를 강제 종료하고,
    `tasklist`로 실제 종료를 확인할 때까지 기다립니다.
  - 언인스톨 직전과 파일 교체 직전에 한 번씩 다시 확인하며, 종료 실패 시 설치를 중단합니다.
  - 서비스 자동 재시작은 설치 중 비활성화하고 등록 단계에서 복원합니다.
- 제거 직전 `appsettings.json`을 `%TEMP%`에 백업하고, 제거가 끝나면 복원합니다.
  - Agent: `C:\Program Files\UpdateAgent\Service\appsettings.json`
  - LabManager: `C:\Program Files\UpdateAgent\LabManager\appsettings.json`
  - 설치 과정에서 생성된 기본 `appsettings.json`은 백업본으로 덮어씁니다.
  - LabManager(v0.1.27 이상): 재사용/무인 업데이트에서는 복원된 설정의 토큰을 바꾸지 않습니다. `change-me-*` 또는
    샘플 토큰(`sample-*-replace-before-pilot`)이 남아 있으면 서버가 시작을 거부하므로
    대시보드에서 실제 토큰으로 교체해야 합니다(자동 치환 없음). 교체 선택 시에는 언인스톨 완료 직후
    백업본의 **기존 토큰 값만 비우고** 설정 파일 자체는 복원합니다. 주소·로깅 등 다른 설정은 유지되고,
    이후 새 토큰만 기록됩니다. 런타임 관리자 토큰 재정의 파일 삭제가 실패하면 설치를 중단합니다(fail-closed).
- `C:\ProgramData\UpdateAgent\agent.json`은 제거 대상이 아니므로 그대로 유지됩니다.
- 이전 버전 제거가 **실제로 실패하면** 오류를 표시하고 **새 설치를 중단합니다.** 덮어쓰기로 이어가지 않습니다.
  설치 관리자별 차이:
  - **Agent**: 등록 정보만 남고 `unins000.exe`가 없거나 MSI `1605`이면 경고/로그 후 계속합니다.
  - **LabManager**: `unins000.exe`를 찾지 못하면 중단합니다(v0.1.38 이상, fail-closed).
    정리 후 다시 실행하세요.

## 6. 무인 설치

### 학생 PC

```powershell
# 가장 단순한 무인 설치(v0.1.30 이상: labmanager DNS 별칭 + 내장 토큰 자동 사용)
.\UpdateAgent.Agent-Setup-<버전>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# 설치와 동시에 LabManager URL/토큰 적용(토큰은 파일로 전달)
.\UpdateAgent.Agent-Setup-<버전>-x64.exe `
  /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /LabManagerUrl=http://192.168.0.10:5080 `
  /AgentTokenFile=C:\pilot-agent-token.txt
```

- 토큰은 **명령줄에 직접 쓰지 않습니다.** `/AgentTokenFile`은 첫 줄을 읽어
  `agent.json`에 기록하고 ACL을 제한합니다.
- 토큰 파일은 UTF-8(BOM 허용) 또는 ASCII로 저장하세요. 첫 줄만 읽습니다.
- `/LabManagerUrl`과 `/AgentTokenFile`은 **키별로** 명령줄 값이 우선하고, 없으면 같은 폴더의
  `agent-install.ini`에서 읽습니다. **첫 설치**에서 둘 다 없으면 `http://labmanager:5080`과
  내장 토큰을 사용합니다(v0.1.30 이상). **업그레이드의 재사용/무인 설치**에서는 내장 토큰을 무시하고
  기존 `agent.json`을 유지합니다. **교체 선택** 시에는 릴리스 내장 토큰(또는 명시한 외부 토큰 파일)으로
  교체하며, 새 URL을 지정하지 않으면 기존 URL을 유지합니다.

#### 설치 파일과 같은 폴더의 설정 파일 사용 (학생 PC 배포 권장)

명령줄 인자 대신 설치 exe와 **같은 폴더**에 `agent-install.ini`를 두면 인스톨러가 자동으로 읽습니다.
토큰 값은 쓰지 않고 토큰 **파일 경로만** 기록합니다.
복사용 샘플은 [`scripts/agent-install.sample.ini`](../scripts/agent-install.sample.ini)를 사용하세요.

```ini
[Agent]
LabManagerUrl=http://labmanager:5080
AgentTokenFile=agent-token.txt
```

- `agent-token.txt`도 같은 폴더에 두면 됩니다. 상대 경로는 설치 exe가 있는 폴더 기준으로 해석되며,
  `C:\...`, `\\서버\공유\...`, `//서버/공유/...` 같은 절대 경로도 그대로 사용할 수 있습니다.
- INI는 **UTF-8 BOM 없이** 저장하세요. 원본 바이트로 BOM을 검사해 BOM이 있으면 명령줄 인자가 있어도
  설치가 중단됩니다(v0.1.43). 값 뒤의 `; 주석`은 무시됩니다.
- 설정 파일이 없으면 명령줄 인자만 사용합니다. 설정 파일도 명령줄 인자도 없으면 **첫 설치**에서는
  기본 URL(`http://labmanager:5080`)과 내장 토큰을 쓰고, **기존 설치의 재사용/무인 설치**에서는
  `agent.json`을 그대로 유지합니다. 다른 DNS 이름/IP를 쓰는 실습실은 반드시 INI나 명령줄로 지정하세요.
- **키별로 명령줄 값이 우선**합니다. 예를 들어 `/LabManagerUrl`만 명령줄로 주고 토큰 파일은 INI에서
  읽을 수도 있습니다. 업그레이드의 재사용/무인 설치에서 토큰 파일을 지정하지 않으면 기존 `agent.json`의
  토큰을 유지하고 요청된 URL만 반영합니다(내장 토큰은 사용하지 않습니다). 교체를 선택하고 토큰 파일이
  없으면 릴리스 내장 토큰으로 교체하며, 새 URL을 지정하지 않으면 기존 URL을 유지합니다.
- 업그레이드 시 토큰 파일을 삭제한 뒤 같은 INI로 다시 설치하면, **기존 설치가 감지되고** 기존
  `agent.json`이 유효할 때만 설정을 유지합니다. 설치 이력이 없거나 `agent.json`이 손상되었으면
  토큰 파일 누락으로 설치가 중단됩니다.
- 두 값 중 하나만 해석되면 설치가 중단됩니다. 토큰 파일의 첫 줄만 읽어 `agent.json`에 기록합니다.
- `LabManagerUrl`은 `http://` 또는 `https://`여야 합니다. `change-me-*`,
  `sample-*-replace-before-pilot` 형식의 토큰은 설치가 중단됩니다.
- 기존 `agent.json`이 있으면 `LabManagerUrl`, `AgentToken`, `SessionHelperPath`만 갱신하고
  `DryRun`, `PollIntervalSeconds` 등 나머지 설정은 유지합니다.
- `agent.json`은 임시 파일에 토큰을 쓴 뒤 ACL을 적용하고 최종 경로로 **원자적으로 교체**하므로 일반
  사용자가 토큰을 읽는 창이 없습니다. ACL 제한 실패도 설치 실패로 처리합니다.

`scripts\deploy-agents.ps1`이 PC별 패키지에 이 설정 파일을 자동으로 생성합니다.

#### 첫 설치 및 교체용 내장 토큰 (v0.1.25 이상, 교체 선택 v0.1.33 이상)

릴리스 빌드에서 `UPDATEAGENT_AGENT_TOKEN`(학생 PC)과
`UPDATEAGENT_LABMANAGER_ADMIN_TOKEN`/`UPDATEAGENT_LABMANAGER_AGENT_TOKEN`(교사 PC) secret이
설정되면 설치 exe 안에 **첫 설치와 교체 선택 업데이트에서 읽는 토큰 파일**이 들어갑니다.
설치 폴더에는 남기지 않습니다.

| 제품 | 상황 | 외부 토큰 지정 | 결과 |
|---|---|---|---|
| Agent | 첫 설치 | 없음 | `http://labmanager:5080` + 내장 `agent-token.txt` 첫 줄(v0.1.30 이상) |
| Agent | 첫 설치 | 있음(`/AgentTokenFile` 또는 INI) | 외부 토큰 우선 |
| Agent | 업데이트, 재사용/무인 | 없음 | 내장 토큰 무시, 기존 토큰 유지 + 요청된 URL 반영 |
| Agent | 업데이트, 재사용/무인 | `/AgentTokenFile` 또는 INI | 명시한 외부 토큰으로 교체 |
| Agent | 업데이트, 교체 선택 | 없음 | 릴리스 내장 `agent-token.txt` 첫 줄로 교체, 새 URL이 없으면 기존 URL 유지(v0.1.33 이상) |
| Agent | 업데이트, 교체 선택 | `/AgentTokenFile` 또는 INI | 외부 토큰으로 교체, 새 URL이 없으면 기존 URL 유지 |
| LabManager | 첫 설치 | 해당 없음 | 내장 두 줄(1행 AdminToken, 2행 AgentToken)을 `appsettings.json`에 반영 |
| LabManager | 업데이트, 재사용/무인 | 없음 | 내장 토큰 무시, 기존 `appsettings.json` 유지 |
| LabManager | 업데이트, 재사용/무인 | `/LabManagerTokenFile` | 명시한 외부 두 줄 토큰으로 교체(v0.1.43: 무인 설치에서도 교체로 처리) |
| LabManager | 업데이트, 교체 선택 | 없음 | 릴리스 내장 두 줄로 교체, 런타임 관리자 토큰 재정의 제거(v0.1.33 이상) |
| LabManager | 업데이트, 교체 선택 | `/LabManagerTokenFile` | 외부 두 줄 토큰 파일로 교체, 런타임 관리자 토큰 재정의 제거 |

#### 업데이트 시 기존 토큰 처리 선택 (v0.1.32 이상)

기존 설치가 감지되면 설치 마법사에 **기존 토큰 처리** 화면이 나타납니다.

- `기존 토큰을 백업해 재사용 (권장)`: 기본값이며 `/VERYSILENT` 무인 설치도 이 규칙을 따릅니다.
  기존 설정을 백업·복원해 **기존 토큰과 URL을 유지**하고 설치 경로만 갱신합니다. 내장 토큰은 무시합니다.
- `기존 토큰을 삭제하고 새 토큰 기록`: **릴리스 exe에 내장된 토큰**으로 교체합니다(v0.1.33 이상).
  별도 명령줄 인자나 파일 선택 없이도 진행됩니다. 다른 토큰을 쓰려면 아래 외부 파일을 지정하면
  그것이 내장 토큰보다 우선합니다. URL을 새로 지정하지 않으면 기존 URL을 유지합니다.
  - Agent: `/AgentTokenFile=<파일>` 또는 `agent-install.ini`의 `AgentTokenFile`
  - LabManager: `/LabManagerTokenFile=<파일>` (첫 줄 AdminToken, 둘째 줄 AgentToken, 설치 exe 기준 상대 경로 허용)

교체용 토큰이 비어 있거나 샘플/기본 값이면, 기존 설치를 제거하거나 파일을 교체하기 **전에**
설치가 중단됩니다. 대시보드에서 변경한 토큰이 있다면 교체 선택 시 릴리스 빌드 시점의 값으로
되돌아갑니다. URL이 따로 지정되지 않으면 기존 설정의 URL을 유지합니다(Agent).
첫 설치와 `/VERYSILENT` 무인 설치에서는 이 화면을 건너뛰고 기존 규칙(첫 설치 및 교체용 내장 토큰,
무인 설치 기존 토큰 재사용)을 적용합니다. 설정 백업본은 토큰 적용이 성공한 뒤에만 삭제합니다.

- 내장 토큰이 비어 있거나 `change-me-*`, `sample-*-replace-before-pilot`이면 첫 설치와 교체 선택
  업데이트가 중단됩니다.
- 재사용을 선택한 업데이트는 내장 파일을 꺼내지도 않으므로 결과에 영향을 주지 않습니다.
  단, 명시적으로 지정한 외부 토큰 파일은 재사용 선택에서도 우선합니다.
- 내장 토큰은 설치 파일에서 추출할 수 있습니다. 파일럿 전용으로 취급하고 설치 직후
  대시보드의 **서버 토큰 변경**으로 관리자 토큰을 교체하세요.
- secret이 없는 로컬/사설 빌드에서는 내장 파일이 비어 있습니다. 에이전트 첫 설치는 외부 토큰 파일을
  지정해야 하고, LabManager 첫 설치도 실제 토큰 secret을 설정해 다시 빌드해야 합니다(v0.1.27 fail-closed).
  재사용 업데이트는 내장 토큰이 없어도 정상 동작하지만, 교체를 선택하려면 실제 내장 토큰이 필요합니다.

#### 실패 모드

모든 입력 검증은 기존 설치를 제거하거나 파일을 교체하기 **전에** 수행됩니다. 아래 경우에는
PC를 변경하지 않고 설치가 중단됩니다(`/VERYSILENT`에서는 오류 창이 표시되지 않으므로
`%TEMP%\Setup Log <날짜>.txt`를 확인하세요).

- 업그레이드에서 유효한 기존 설정이 없는데 INI/명령줄 값이 없거나 한쪽만 있음,
  첫 설치에서 INI가 BOM 포함/잘못된 형식(명령줄 키가 있어도 중단)
- `LabManagerUrl`이 절대 `http`/`https` URL이 아님
- 토큰 파일이 없거나 비었거나 샘플/기본 토큰임(첫 설치에서 내장 토큰도 동일하게 검사)
- `agent.json` 생성/ACL 제한 실패, 방화벽 규칙 처리 실패

방화벽 규칙은 `netsh`가 지원하지 않는 그룹 정리를 PowerShell `Remove-NetFirewallRule`로 처리합니다.
학생용 에이전트는 수신 대기 구성 요소가 없어 인바운드 규칙을 만들지 않고, `UpdateAgent` 그룹에 남아 있는
이전 버전 규칙만 제거합니다. 교사 PC는 `UpdateAgent LabManager` 그룹에 TCP 5080 Domain/Private 규칙을
등록하며, 규칙 처리 실패 시 설치를 중단합니다.

### 교사 PC

```powershell
.\UpdateAgent.LabManager-Setup-<버전>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

릴리스 빌드에는 `UPDATEAGENT_LABMANAGER_ADMIN_TOKEN`과 `UPDATEAGENT_LABMANAGER_AGENT_TOKEN` secret이
설정되어 있어야 하며, 두 값은 **서로 달라야** 합니다. 두 값이 모두 내장되어 있으면 첫 설치에서
`appsettings.json`에 반영되어 서버가 바로 시작됩니다. 하나라도 비었거나 샘플/기본 값이면 설치가 중단됩니다.
업데이트의 재사용/무인 설치는 내장 토큰을 무시하고 기존 설정을 유지합니다. 교체를 선택하면 릴리스
내장 두 줄을 사용하고, `/LabManagerTokenFile`을 지정하면 그것이 우선합니다.
내장 토큰은 설치 파일에서 추출할 수 있으므로 **파일럿 직후 대시보드의 서버 토큰 변경으로
관리자 토큰을 교체**하세요. `C:\Program Files\UpdateAgent\LabManager\appsettings.json`의
`AdminToken`과 `AgentToken`을 모두 실제 값으로 바꿔야 서버가 시작됩니다.
`AgentToken`은 에이전트와 동일한 값으로 설정합니다. 관리자 토큰(`AdminToken`)은 서버 설정이면서
대시보드 첫 실행 시 입력하는 값이기도 합니다. 대시보드의 **토큰** 버튼으로 열리는 창에는
샘플 값이 표시되지 않으므로 실제 토큰을 직접 입력하세요. 입력한 값은 앱 모드 브라우저 프로필의
localStorage에 저장됩니다.

같은 창의 **서버 토큰 변경**에서 새 관리자 토큰을 입력하면 서버에 즉시 반영됩니다.
변경된 값은 `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.json`에 저장되어
Program Files의 `appsettings.json`보다 우선하며 재시작 후에도 유지됩니다. 재시작이 필요 없고
브라우저에 저장된 토큰도 새 값으로 갱신되지만, 이전 토큰은 변경 직후부터 거부됩니다.
에이전트 토큰(`AgentToken`)은 이 화면에서 변경되지 않으므로
`configure-labmanager.ps1`로 별도 교체해야 합니다.
`appsettings.json`과 `runtime-admin-token.json`은 관리자/SYSTEM 및 현재 사용자만 접근할 수 있도록
ACL이 제한되며, 교체 선택 업데이트에서 런타임 재정의 파일을 삭제하지 못하면 설치를 중단합니다(fail-closed).

토큰 변경 이력은 `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.audit.log`에 기록됩니다.
UTC 시각, 이벤트(`changed`, `rolled-back`, `loaded`, `rejected`)와 토큰 지문(SHA-256 앞 12자리)만 남고
**토큰 원문은 기록되지 않습니다.** 저장에 실패하면 이전 토큰을 복구하고 `rolled-back`을 남기며,
복구도 실패하면 인증을 fail-closed로 유지합니다. `GET /api/admin/token` 응답의 `LastChangedAt`,
`RotationDue`(90일 경과), `AuditAvailable`로 회전 시점을 확인할 수 있습니다.

설치 프로그램도 같은 파일에 관리자/SYSTEM/현재 사용자 ACL을 적용합니다. 현재 사용자 SID 확인 또는
`icacls`가 실패하면 오류 창과 Setup 로그에 원인(종료 코드)을 표시하고 설치를 중단합니다.

### 설치 로그

`SetupLogging=yes`가 설정되어 있어 `%TEMP%\Setup Log <날짜>.txt`에 기록됩니다.
무인 배포에서는 `/LOG="C:\경로\install.log"`로 위치를 지정할 수 있습니다.

## 7. 방화벽 규칙

방화벽 규칙은 PowerShell `NetSecurity` 모듈로 처리합니다(창 숨김).
`netsh`는 그룹 단위 정리를 지원하지 않으므로 사용하지 않습니다.
교사 PC는 **기본 이름과 버전이 붙은 이름을 모두** 등록하고, 학생 PC는 새 규칙 없이 `UpdateAgent` 그룹의 이전 규칙만 제거합니다.

| 제품 | 규칙 이름 | 대상 | 프로필 |
|---|---|---|---|
| Agent | (신규 규칙 없음) | `UpdateAgent` 그룹의 이전 버전 규칙만 제거 | 해당 없음 |
| LabManager | `UpdateAgent LabManager` | 인바운드 TCP 5080(포트 규칙) | Domain/Private |
| LabManager | `UpdateAgent LabManager <버전>` | 동일 | Domain/Private |

- LabManager는 프로그램 경로가 아니라 **TCP 포트 규칙**으로 등록합니다. 인스톨러와
  `scripts\allow-labmanager.ps1` 모두 설치된 `appsettings.json`의 `Urls`/`ListenUrl`에서
  포트를 읽되 없으면 5080을 사용합니다. Public 프로필은 차단 상태로 유지합니다.
- 설치 후 포트를 변경했다면 `scripts\allow-labmanager.ps1`을 다시 실행해 규칙을 갱신하세요.
- 교사 PC에 새로 등록하는 규칙은 그룹(`UpdateAgent LabManager`)으로 묶습니다.
- 설치/업그레이드 시 그룹 단위로 이전 규칙을 먼저 삭제하므로, 이전 버전의
  `UpdateAgent Agent 0.0.9` 같은 규칙이 남지 않습니다.
- 제거 시에도 그룹 단위로 규칙을 삭제합니다.
- SessionHelper와 CLI는 네트워크 수신 포트가 없어 등록하지 않습니다(불필요한 노출 방지).
  네트워크가 필요한 앱만 등록하는 것이 원칙입니다.

## 8. 포함 파일 점검

`scripts\build-inno.ps1`은 스테이징 단계에서 다음을 제거하고, 남아 있으면 빌드를 실패시킵니다.

- 확장자: `.pdb`, `.xml`, `.md`, `.txt`, `.rtf`, `.pdf`, `.doc`, `.docx`,
  `.log`, `.tmp`, `.msi`, `.cab`, `.zip`, `.7z`, `.rar`, `.lnk`, `.url`, `.br`, `.gz`
- 폴더: `docs`, `doc`, `samples`, `examples`, `portable`
- 파일: `web.config`, `appsettings.Development.json`,
  `UpdateAgent.LabManager.staticwebassets.endpoints.json`(`UseStaticFiles`가 사용하지 않는 빌드 매니페스트)
  및 `.br`/`.gz` 압축 정적 자산

검증 항목:

- 위 패턴이 하나라도 남아 있으면 오류.
- Agent 필수 파일: `Service\UpdateAgent.Service.exe`, `Service\appsettings.json`,
  `SessionHelper\UpdateAgent.SessionHelper.exe`, `Desktop\UpdateAgent.Agent.Desktop.exe`,
  `Cli\UpdateAgent.Cli.exe`
- LabManager 필수 파일: `UpdateAgent.LabManager.exe`, `UpdateAgent.LabManager.Desktop.exe`,
  `wwwroot\index.html`, `appsettings.json`

최근 빌드 기준 포함 파일(WPF 런타임/리소스 포함으로 크기 증가):

| 제품 | 파일 수 | 스테이징 크기 |
|---|---|---|
| Agent | 853 | 304.9 MB |
| LabManager | 데스크톱 앱 포함 | WPF 런타임 DLL/리소스 포함 |

## 9. 빌드 방법

준비물:

- .NET SDK 10
- Inno Setup 6 (`ISCC.exe`, 기본 경로 또는 `-IsccPath`)

```powershell
# 1) 실행 파일 게시
.\scripts\publish.ps1

# 2) Inno Setup 설치 파일 빌드
.\scripts\build-inno.ps1

# 게시를 건너뛰고 기존 publish\를 사용
.\scripts\build-inno.ps1 -NoPublish

# 스테이징 보존(포함 파일 확인용)
.\scripts\build-inno.ps1 -NoPublish -KeepStaging

# 에이전트만 게시/빌드
.\scripts\publish.ps1 -AgentOnly
.\scripts\build-inno.ps1 -AgentOnly -NoPublish
```

`build-inno.ps1`은 `Directory.Build.props`의 `<Version>`을 읽어 파일 이름과
`AppVersion`을 맞춥니다. 버전은 `scripts\github-push.ps1`이 올립니다.

### 에이전트만 한 번에 빌드 (`build.bat`)

저장소 루트의 `build.bat`을 더블클릭하거나 실행하면 다음을 순서대로 수행합니다.

1. `Directory.Build.props`에서 버전을 읽습니다.
2. `publish.ps1 -AgentOnly`로 Service/SessionHelper/Desktop/Cli만 게시합니다.
3. `build-inno.ps1 -AgentOnly`로 학생 PC용 설치 파일만 빌드합니다.
4. 결과를 버전별 폴더에 모으고 탐색기를 엽니다.

```text
build\agent\<버전>\
  UpdateAgent.Agent-Setup-<버전>-x64.exe
  UpdateAgent.Agent-Setup-<버전>-x64.exe.sha256
  build-info.json
```

중간 산출물은 `build\.staging\`에 만들고 빌드가 끝나면 삭제합니다.
실패하면 원인 확인을 위해 `build\.staging\`을 남깁니다.

> `.NET` apphost(`UpdateAgent.Service.exe`, `UpdateAgent.SessionHelper.exe`,
> `UpdateAgent.Cli.exe`)의 파일 이름은 직접 바꾸지 않습니다. apphost는 실행 시
> 같은 이름의 DLL(예: `UpdateAgent.Cli.dll`)을 찾기 때문에, 이름을 바꾸면
> `The application to execute does not exist` 오류가 납니다.
> 버전이 붙은 실행 파일 이름은 Inno Setup 설치 파일이 담당합니다.

## 10. 업그레이드와 제거

- 업그레이드: 새 설치 파일을 실행하면 AppID/MSI 등록 정보를 기준으로 기존 설치를 찾아
  묻지 않고 먼저 제거한 뒤 설치합니다. 실패하면 설치를 중단하고 오류를 표시합니다.
- 제거: 설정 → 앱 → `UpdateAgent Agent <버전>` / `UpdateAgent LabManager <버전>` → 제거.
- Agent 제거 시 서비스와 `UpdateAgent` 그룹에 남아 있는 이전 방화벽 규칙을 제거합니다.
- `C:\ProgramData\UpdateAgent`는 제거 후에도 남습니다. 완전 삭제는 수동으로 폴더를 지웁니다.
- LabManager의 `appsettings.json`은 업그레이드 시 백업/복원으로 유지됩니다.
- 앱 모드 브라우저 프로필(`browser-profile`)과 `desktop-settings.json`은
  `%LOCALAPPDATA%`에 있어 설치/제거 영향을 받지 않습니다.
- `wwwroot` 대시보드/모니터 파일(`index.html`, `app.js`, `monitor.html`, `monitor.js`, `styles.css`)은 설치 시 항상 최신 파일로 덮어써집니다.

## 11. MSI와의 관계

- Inno Setup 설치 파일이 **권장**입니다.
- WiX MSI(`artifacts\msi\UpdateAgent.Agent-<버전>-x64.msi`)는 대안으로 유지됩니다.
- MSI가 설치된 PC에 Inno 설치 파일을 실행하면 MSI 제품을 자동 제거한 뒤 Inno로 전환합니다.
- MSI 설계와 제약은 [`MSI.md`](MSI.md)를 참고하세요.

## 12. 파일럿 테스트 체크리스트

관리자 권한이 있는 실제 PC/VM에서 아래를 확인합니다.

- [ ] 신규 설치: Program Files에 개별 파일로 설치됨
- [ ] 설치된 앱이 설정 → 앱에 `UpdateAgent Agent <버전>`으로 표시됨
- [ ] 바탕화면 바로가기 체크/해제가 동작함
- [ ] 설치 완료 후 서비스가 `Running`
- [ ] 학생 PC에는 새 인바운드 규칙이 없고 이전 `UpdateAgent` 그룹 규칙만 제거됨
- [ ] 교사 PC에는 기본/버전 규칙 2개가 `UpdateAgent LabManager` 그룹으로 등록됨
- [ ] 기존 설치 위에 실행 시 묻지 않고 이전 버전이 자동 제거된 뒤 설치됨
- [ ] `/VERYSILENT` 설치가 프롬프트 없이 완료됨
- [ ] 이전 제거가 실패하면 오류가 표시되고 설치가 중단됨
- [ ] 업그레이드 후 `ProgramData\UpdateAgent\agent.json`과 `appsettings.json`이 유지됨
- [ ] 제거 시 서비스/방화벽 규칙/파일이 제거되고 ProgramData는 남음
- [ ] MSI 설치 후 Inno 설치 시 MSI가 자동 제거됨
- [ ] LabManager 설치 후 바탕화면/시작 메뉴 바로가기가 런처를 실행함
- [ ] 런처가 Edge/Chrome을 감지해 주소창/툴바 없는 `--app=` 창을 엽니다
- [ ] 대시보드 첫 실행 시 토큰 입력창이 표시되고 브라우저 프로필에 저장됨
- [ ] 서버가 없을 때 런처가 헤드리스 서버를 자동 시작하고, 앱 창을 닫아도 서버가 유지됨
- [ ] 설치 폴더에 `wwwroot\index.html`, `wwwroot\monitor.html`, `wwwroot\monitor.js`가 있음
- [ ] 대시보드의 **작업 현황 모니터** 버튼과 런처 `--monitor`로 별도 창이 열리고 실시간 연결되는지 확인

## 13. 알려진 제약

- 설치 파일은 파일럿 단계에서 **코드 서명이 없습니다.** SmartScreen 경고가 표시될 수 있습니다.
- Inno Setup은 Windows 전용입니다(macOS/Linux/모바일 설치 파일을 만들지 않습니다).
- `UpdateAgent.Agent-Setup`의 지원 범위는 Windows 11 / Windows 10 22H2(ESU) / Windows Server 2016–2025입니다. Inno Setup의 `MinVersion=10.0.14393`은 설치 프로그램 실행의 최소 요건일 뿐 지원 정책을 의미하지 않습니다.
- 서비스 시작은 설치 직후 수행되지만, 그룹 정책/보안 프로그램이 차단하면
  `services.msc`에서 수동으로 시작해야 할 수 있습니다.

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
  wwwroot\                            대시보드(index.html, app.js, styles.css)
  appsettings.json                    관리자/에이전트 토큰, 수신 URL
  unins000.exe
```

- 교사용 화면은 시스템의 Edge/Chrome을 `--app=` 단독 창 모드로 실행합니다. WebView2는 사용하지 않습니다.
- 런처는 창 없이 동작하며, 서버가 없으면 `UpdateAgent.LabManager.exe --headless`를 자동 실행합니다.
  이미 실행 중인 서버는 재사용합니다. 앱 모드 창을 닫아도 서버는 계속 실행됩니다.
- 관리자 토큰은 전용 브라우저 프로필(`%LOCALAPPDATA%\UpdateAgent\LabManager\browser-profile`)의
  localStorage에 저장됩니다. 런처 설정은 같은 폴더의 `desktop-settings.json`에 저장됩니다.
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
   - 제거 전 `appsettings.json`을 임시 폴더에 백업하고, 제거 후 복원합니다.
   - 제거가 실패하면 새 설치를 중단하고 오류를 표시합니다.
4. **설치**
   - 개별 파일을 `Program Files`에 복사합니다.
   - Agent는 `[InstallDelete]`로 이전 버전의 Service/SessionHelper/Cli 폴더를 정리한 뒤 복사합니다.
5. **바탕화면 바로가기 선택**
   - `바로가기:` 작업에서 체크/해제할 수 있습니다(기본 체크).
6. **서비스·방화벽·설정 적용** (Agent)
   - 서비스 등록/갱신, 방화벽 규칙 등록, 선택적 `agent.json` 작성.
7. **앱 실행**
   - Agent: 서비스를 시작하고 마지막 화면에서 `UpdateAgent 상태 확인` 데스크톱 앱을 실행할 수 있습니다.
     시작 메뉴/바탕화면 바로가기도 같은 앱(`{app}\Desktop\UpdateAgent.Agent.Desktop.exe`)을 엽니다.
      이 앱은 상태 조회와 함께 **업데이트 시작** 버튼을 제공하며, 로컬 제어 파이프(`UpdateAgent.LocalControl.v1`)로
      실행 중인 서비스에 작업을 요청합니다. 학생 계정(UAC 필터링 토큰 포함)이 연결할 수 있도록 파이프 ACL에
      인증 사용자가 포함됩니다.
   - LabManager: 설치 파일에는 샘플 토큰(`sample-*-token-replace-before-pilot`)이 들어 있어 서버와 대시보드가 바로 시작됩니다.
     다만 샘플 값은 공개된 예시이므로 **파일럿 전에 반드시 실제 토큰으로 교체**해야 합니다.
     `change-me-*`가 남아 있으면 서버가 시작되지 않고 토큰 변경 안내가 표시됩니다.
     바로가기는 **런처**(`UpdateAgent.LabManager.Desktop.exe`)를 가리키며, 런처가 Edge/Chrome 앱 모드 창으로 대시보드를 엽니다.

## 5. 기존 설치 처리

| 기존 상태 | 동작 |
|---|---|
| Inno Setup Agent/LabManager 설치됨 | 이전 제거 프로그램을 무인 실행해 제거한 뒤 새로 설치 |
| WiX MSI Agent 설치됨 | MSI 제품을 `msiexec /x ... /qn`으로 자동 제거한 뒤 Inno 설치 |
| 레거시 스크립트 설치(`C:\ProgramData\UpdateAgent\app`) | 감지 후 안내만 표시, 필요 시 수동 제거 |
| 모든 경우(대화형/무인) | 묻지 않고 항상 제거 후 설치(설정 데이터는 보존) |

- 설치 순서는 **실행 중인 앱 종료 → 이전 버전 언인스톨 → 새 파일 설치**입니다.
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
  - LabManager(v0.1.9 이상): 복원된 설정에 `change-me-*`가 남아 있으면 샘플 토큰(`sample-*-token-replace-before-pilot`)으로 자동 교체합니다. 이미 설정한 토큰은 그대로 유지됩니다.
- `C:\ProgramData\UpdateAgent\agent.json`은 제거 대상이 아니므로 그대로 유지됩니다.
- 이전 버전 제거가 실패하면 오류를 표시하고 **새 설치를 중단합니다.** 덮어쓰기로 이어가지 않습니다.

## 6. 무인 설치

### 학생 PC

```powershell
# 가장 단순한 무인 설치(설정은 나중에 configure-agent.ps1로 적용)
.\UpdateAgent.Agent-Setup-0.1.10-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# 설치와 동시에 LabManager URL/토큰 적용(토큰은 파일로 전달)
.\UpdateAgent.Agent-Setup-0.1.10-x64.exe `
  /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /LabManagerUrl=http://192.168.0.10:5080 `
  /AgentTokenFile=C:\pilot-agent-token.txt
```

- 토큰은 **명령줄에 직접 쓰지 않습니다.** `/AgentTokenFile`은 첫 줄을 읽어
  `agent.json`에 기록하고 ACL을 제한합니다.
- 토큰 파일은 UTF-8(BOM 없음) 또는 ASCII로 저장하세요.
- `/LabManagerUrl`과 `/AgentTokenFile`은 함께 지정해야 합니다.

### 교사 PC

```powershell
.\UpdateAgent.LabManager-Setup-0.1.10-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

설치 후에는 샘플 토큰(`sample-*-token-replace-before-pilot`)으로 서버가 바로 시작되지만,
이 값은 공개된 예시이므로 **파일럿 전에 반드시 실제 토큰으로 교체**해야 합니다.
`C:\Program Files\UpdateAgent\LabManager\appsettings.json`의 `AdminToken`과 `AgentToken`을 모두 변경하세요.
`change-me-*`가 남아 있으면 서버가 시작되지 않습니다.
`AgentToken`은 에이전트와 동일한 값으로 설정합니다. 관리자 토큰(`AdminToken`)은 서버 설정이면서
대시보드 첫 실행 시 입력하는 값이기도 합니다. 대시보드의 **토큰** 버튼으로 열리는 창에는
설치 시 기본값인 샘플 관리자 토큰(`sample-admin-token-replace-before-pilot`)이 화면에 표시되고,
**이 값 사용** 버튼으로 입력란을 채울 수 있습니다. 입력한 값은 앱 모드 브라우저 프로필의
localStorage에 저장됩니다.

같은 창의 **서버 토큰 변경**에서 새 관리자 토큰을 입력하면 서버에 즉시 반영됩니다.
변경된 값은 `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.json`에 저장되어
Program Files의 `appsettings.json`보다 우선하며 재시작 후에도 유지됩니다. 재시작이 필요 없고
브라우저에 저장된 토큰도 새 값으로 갱신되지만, 이전 토큰은 변경 직후부터 거부됩니다.
에이전트 토큰(`AgentToken`)은 이 화면에서 변경되지 않으므로
`configure-labmanager.ps1`로 별도 교체해야 합니다.

### 설치 로그

`SetupLogging=yes`가 설정되어 있어 `%TEMP%\Setup Log <날짜>.txt`에 기록됩니다.
무인 배포에서는 `/LOG="C:\경로\install.log"`로 위치를 지정할 수 있습니다.

## 7. 방화벽 규칙

설치 단계에서 조용히(`netsh advfirewall`, 창 숨김) 등록합니다.
**기본 이름과 버전이 붙은 이름을 모두** 등록합니다.

| 제품 | 규칙 이름 | 대상 | 프로필 |
|---|---|---|---|
| Agent | `UpdateAgent Agent` | `...\UpdateAgent\Service\UpdateAgent.Service.exe` | 전체 |
| Agent | `UpdateAgent Agent <버전>` | 동일 | 전체 |
| LabManager | `UpdateAgent LabManager` | 인바운드 TCP 5080(포트 규칙) | Domain/Private |
| LabManager | `UpdateAgent LabManager <버전>` | 동일 | Domain/Private |

- LabManager는 프로그램 경로가 아니라 **TCP 포트 규칙**으로 등록합니다. 인스톨러와
  `scripts\allow-labmanager.ps1` 모두 설치된 `appsettings.json`의 `Urls`/`ListenUrl`에서
  포트를 읽되 없으면 5080을 사용합니다. Public 프로필은 차단 상태로 유지합니다.
- 설치 후 포트를 변경했다면 `scripts\allow-labmanager.ps1`을 다시 실행해 규칙을 갱신하세요.
- 모든 규칙은 그룹(`UpdateAgent`, `UpdateAgent LabManager`)으로 묶습니다.
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
2. `publish.ps1 -AgentOnly`로 Service/SessionHelper/Cli만 게시합니다.
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
- Agent 제거 시 서비스와 방화벽 규칙을 함께 제거합니다.
- `C:\ProgramData\UpdateAgent`는 제거 후에도 남습니다. 완전 삭제는 수동으로 폴더를 지웁니다.
- LabManager의 `appsettings.json`은 업그레이드 시 백업/복원으로 유지됩니다.
- 앱 모드 브라우저 프로필(`browser-profile`)과 `desktop-settings.json`은
  `%LOCALAPPDATA%`에 있어 설치/제거 영향을 받지 않습니다.
- `wwwroot` 대시보드는 설치 시 항상 최신 파일로 덮어써집니다.

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
- [ ] 방화벽 규칙 2개(기본/버전)가 그룹과 함께 등록됨
- [ ] 기존 설치 위에 실행 시 묻지 않고 이전 버전이 자동 제거된 뒤 설치됨
- [ ] `/VERYSILENT` 설치가 프롬프트 없이 완료됨
- [ ] 이전 제거가 실패하면 오류가 표시되고 설치가 중단됨
- [ ] 업그레이드 후 `ProgramData\UpdateAgent\agent.json`과 `appsettings.json`이 유지됨
- [ ] 제거 시 서비스/방화벽 규칙/파일이 제거되고 ProgramData는 남음
- [ ] MSI 설치 후 Inno 설치 시 MSI가 자동 제거됨
- [ ] LabManager 설치 후 바탕화면/시작 메뉴 바로가기가 런처를 실행함
- [ ] 런처가 Edge/Chrome을 감지해 주소창/툴바 없는 `--app=` 창을 염
- [ ] 대시보드 첫 실행 시 토큰 입력창이 표시되고 브라우저 프로필에 저장됨
- [ ] 서버가 없을 때 런처가 헤드리스 서버를 자동 시작하고, 앱 창을 닫아도 서버가 유지됨
- [ ] 설치 폴더에 `wwwroot\index.html`이 있음

## 13. 알려진 제약

- 설치 파일은 파일럿 단계에서 **코드 서명이 없습니다.** SmartScreen 경고가 표시될 수 있습니다.
- Inno Setup은 Windows 전용입니다(macOS/Linux/모바일 설치 파일을 만들지 않습니다).
- `UpdateAgent.Agent-Setup`의 지원 범위는 Windows 11 / Windows 10 22H2(ESU) / Windows Server 2016–2025입니다. Inno Setup의 `MinVersion=10.0.14393`은 설치 프로그램 실행의 최소 요건일 뿐 지원 정책을 의미하지 않습니다.
- 서비스 시작은 설치 직후 수행되지만, 그룹 정책/보안 프로그램이 차단하면
  `services.msc`에서 수동으로 시작해야 할 수 있습니다.

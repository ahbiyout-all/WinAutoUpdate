# 작업 로그

이 문서는 UpdateAgent 프로젝트의 주요 작업 이력을 날짜별로 기록합니다.
빌드/테스트 결과와 산출물 해시는 작업 시점 기준이며, 이후 빌드에서 달라질 수 있습니다.

---

## 2026-11-20 — LabManager 샘플 토큰 기본값 제공 (v0.1.8)

### 배경

- 교사용 LabManager 설치 후 `appsettings.json`에 `change-me-*`가 남아 있으면 서버가 즉시 종료되어 대시보드 런처가 오류를 표시했다.
- 사용자가 설치 직후 바로 실행할 수 있도록 기본값에 샘플 토큰을 넣어 달라고 요청했다.

### 변경

| 항목 | 내용 |
|---|---|
| `src\UpdateAgent.LabManager\appsettings.json` | `AdminToken`/`AgentToken`을 `sample-*-token-replace-before-pilot`으로 변경 |
| `configure-labmanager.ps1` | 실제 토큰으로 교체할 때 샘플 값도 거부하도록 검사 확장 |
| `Program.cs` | 기존대로 `change-me-*`만 거부 → 샘플 값으로 서버가 정상 시작 |
| `docs\PILOT.md` | 샘플 값도 파일럿에서 그대로 쓰지 말고 반드시 교체한다는 경고 추가 |

- 값 자체에 `replace-before-pilot`이 들어 있어 설정 파일을 열면 교체 필요성을 알 수 있다.

### 검증

- 샘플 토큰으로 `--headless` 서버 기동 후 `/api/health`가 `200` 반환 확인.
- 빌드 경고 0/오류 0, 테스트 89개 통과.
- 로컬 빌드 SHA-256(CI 재빌드 시 달라질 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.8-x64.exe` `1dde1047334eb6d9b3e25d83b205fcc2b896441f711f29b08ce3fb6802d52009`
  - `UpdateAgent.LabManager-Setup-0.1.8-x64.exe` `db96f87cb75d75bd3d5c630c827ae043b87d4b0a0079353a7ec5c7860abc3f45`
  - `UpdateAgent.Agent-0.1.8-x64.msi` `3a7fba24e25cfc02379ad6bff98bed97a50fa9f1fe188d17000add12b17a647b`
- 공개된 `v0.1.8` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.8-x64.exe` `fce2ff097c4e644cc8924d0dcb1c8e994f71313f79cc25da41342b77cf336b9e`
  - `UpdateAgent.LabManager-Setup-0.1.8-x64.exe` `09fe89e8fbe27e3098ddac89d8289201d9394f1c0498567651ee75832c79323b`
  - `UpdateAgent.Agent-0.1.8-x64.msi` `faba1673c3fdd738ee6ceadae77669e23c1f13d20865c8c322305cf8cbb8adc4`
- 릴리스 워크플로 실행 #9 성공. GitHub 자동 생성 소스 코드 2개를 제외한 업로드 자산은 설치 파일 3개뿐이며 기존 릴리스와 동일한 형태다.
- 기존 `v0.1.7` 이하 릴리스는 변경하지 않음.

### 보안 주의

- 샘플 토큰은 공개된 값이므로 실제 배포/파일럿 전 반드시 `configure-labmanager.ps1`로 교체해야 한다.

---

## 2026-11-20 — 학생 데스크톱 앱 창이 표시되지 않는 결함 수정 (v0.1.7)

### 발견한 오류

- 학생용 `UpdateAgent.Agent.Desktop.exe`가 실행되면 프로세스는 살아 있지만 창이 전혀 나타나지 않았다.
- `src\UpdateAgent.Agent.Desktop\App.xaml`에 `StartupUri="MainWindow.xaml"`이 누락되어 WPF가 `MainWindow`를 생성하지 않았다.
- `App.xaml.cs`가 비어 있어 대체 시작 코드도 없었다. (SessionHelper는 코드에서 창을 만들고, LabManager Desktop은 의도적으로 창이 없다.)
- 이전 검증은 `tasklist`로 프로세스 존재만 확인하고 `MainWindowHandle`을 확인하지 않아 놓쳤다.

### 수정

| 항목 | 내용 |
|---|---|
| 변경 | `App.xaml`에 `StartupUri="MainWindow.xaml"` 추가 |
| 검증 | 재빌드 후 프로세스 실행 시 `MainWindowHandle` ≠ 0, 창 제목 `UpdateAgent 상태` 확인 |

### 영향

- `v0.1.6` 이하에서 학생용 상태 앱을 실행해도 창이 보이지 않는다. `v0.1.7` 사용을 권장한다.

### 검증

- `dotnet build -c Release`: 경고 0/오류 0, 테스트 89개 통과, 정적 검사 통과.
- Inno Setup 6.7.1 재컴파일 성공, MSI 재빌드 성공, UTF-8 BOM 유지.
- 로컬 빌드 SHA-256(CI 재빌드 시 달라질 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.7-x64.exe` `756543b52f556c8b29df0a0fb416f9ff182800ecd2949ff1bae924da0c987cdb`
  - `UpdateAgent.LabManager-Setup-0.1.7-x64.exe` `e95901327a40b6dbc39a719db5ebc117de2daed94bfa7eb342d96d32e5988a35`
  - `UpdateAgent.Agent-0.1.7-x64.msi` `65dfee01b2b9cdbebabc6bb6fef6fd18d4ab5c0bdce655b7041ea0882e7b4a77`
- 공개된 `v0.1.7` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.7-x64.exe` `5880448dc1f2e81035a7e4628ffcf6f163c4ea0f2a0f047a4023ae700007863f`
  - `UpdateAgent.LabManager-Setup-0.1.7-x64.exe` `2c4ad498c612b94be6a5628a76ea2f950a3bfb6c7e264431350a481e0265d2ff`
  - `UpdateAgent.Agent-0.1.7-x64.msi` `d4177455d5ec64ec19026d73269b8c558afea91e363a1aab1a01032d626af0e5`
- 릴리스 워크플로 실행 #8 성공. GitHub 자동 생성 소스 코드 2개를 제외한 업로드 자산은 설치 파일 3개뿐이며 기존 릴리스와 동일한 형태다.
- 기존 `v0.1.6` 이하 릴리스는 변경하지 않음.

---

## 2026-11-19 — 프로세스 종료 감지 결함 수정 (v0.1.6)

### 자아성찰 — 발견한 오류

- v0.1.4/v0.1.5의 `IsProcessRunning`은 `tasklist /NH` 기본 표 출력에서 이미지명을 찾았다.
- 실제 Windows `tasklist`는 이미지명을 25자로 잘라 표시한다. 예: `UpdateAgent.Agent.Desktop.exe` → `UpdateAgent.Agent.Desktop`, `UpdateAgent.LabManager.exe` → `UpdateAgent.LabManager`.
- 따라서 **학생 GUI, 세션 도우미, 교사 런처/서버가 실행 중이어도 감지하지 못해 종료하지 않는다.** `종료 → 언인스톨 → 설치` 순서를 보장한다고 주장했지만 실제로는 보장되지 않았다.
- 원인은 컴파일 성공과 단위 테스트만으로 설치 시나리오를 검증하지 않고, 프로세스 감지 로직을 실제 실행 중인 프로세스로 확인하지 않은 데 있다.

### 수정

| 항목 | 내용 |
|---|---|
| 감지 방식 | `tasklist ... /NH /FO CSV`로 전체 이미지명을 얻고 `"IMAGENAME"` 문자열로 비교 |
| 임시 파일 | 제품별 파일명(`UpdateAgent.Agent.process.check.txt`, `UpdateAgent.LabManager.process.check.txt`)으로 분리 |
| 정리 | 리팩터링 후 사용되지 않는 `KillProcess` 함수 제거 |
| 검증 | 학생 GUI를 실제 실행한 상태에서 감지 `True`, 강제 종료 후 감지 `False` 확인 |

### 영향

- `v0.1.4`, `v0.1.5` 설치 프로그램으로 덮어쓰기 설치 시 위 프로세스가 남아 있으면 파일 잠금으로 실패할 수 있다. `v0.1.6` 사용을 권장한다.

### 검증

- `dotnet build -c Release`: 경고 0/오류 0, 테스트 89개 통과.
- PowerShell/YAML 파싱, PII 스캔, 버전 동기화 검사 통과.
- Inno Setup 6.7.1 컴파일 성공, UTF-8 BOM 유지.
- 로컬 빌드 SHA-256(CI 재빌드 시 달라질 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.6-x64.exe` `df3e1b5667d1c3ee5ec0eaa03ac1086a5a3378927f875e691c2e8f3a45034a76`
  - `UpdateAgent.LabManager-Setup-0.1.6-x64.exe` `89dc454dfbced1583c5f1e8df74ec5dccd4deadaf549c25ef35b4c096d61a64e`
  - `UpdateAgent.Agent-0.1.6-x64.msi` `8479da3cc7a0c49d065a4293086bc5dab96350f55c180206245d5ae066849b60`
- 공개된 `v0.1.6` 릴리스 에셋 3개의 다운로드 해시가 릴리스 노트와 일치함을 GitHub REST API로 확인:
  - `UpdateAgent.Agent-Setup-0.1.6-x64.exe` `8de997701cebb6b539f6ac120031d1b8cc719116c475d435708f66aff9632ba9`
  - `UpdateAgent.LabManager-Setup-0.1.6-x64.exe` `24e23689a5c07765494f7a8885a7a688af4defd5cbe35bb35a9a58c1c5d3b1e1`
  - `UpdateAgent.Agent-0.1.6-x64.msi` `14cb13ecfa898ea53b9944850c977674c90b56db800375f70d57b6057a57caab`
- 기존 `v0.1.5` 이하 릴리스는 변경하지 않음.

---

## 2026-11-19 — 앱별 아이콘 추가 (v0.1.5)

### 내용

| 대상 | 아이콘 | 적용 위치 |
|---|---|---|
| 학생용(`UpdateAgent.Agent.Desktop`) | 모니터 + 초록색 상태 체크(파랑~청록 배경) | WPF `ApplicationIcon`, 바로가기, Agent 인스톨러, MSI `ARPPRODUCTICON` |
| 교사용(`UpdateAgent.LabManager.Desktop`) | 중앙 대시보드 + 관리 대상 PC 연결(남보라~보라 배경) | WPF `ApplicationIcon`, 바로가기, LabManager 인스톨러 |

- 이미지 생성 서비스 인증 오류로 로컬(Pillow)에서 벡터 스타일로 직접 생성했다.
- `assets/icons/`에 16~256px 다중 해상도 ICO로 저장했고, 16px에서도 실루엣이 유지됨을 확인했다.
- Agent 인스톨러의 `UninstallDisplayIcon`도 학생용 GUI exe로 바꿔 추가/제거 항목 아이콘을 통일했다.

### 검증

- `dotnet build -c Release`: 경고 0/오류 0, 테스트 89개 통과.
- PowerShell/YAML 파싱, PII 스캔, 버전 동기화 검사 통과.
- exe 및 설치 프로그램에서 추출한 아이콘이 원본 ICO와 일치(렌더링 차이만 존재).
- Inno Setup 6.7.1 컴파일 성공, MSI `Icon` 테이블과 `ARPPRODUCTICON=AgentIcon` 확인.
- 로컬 빌드 SHA-256(CI 재빌드 시 달라질 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.5-x64.exe` `d6e7e343c01afb74911e673687933fa88ea1a99a0bcf34d64c9682a81ac40302`
  - `UpdateAgent.LabManager-Setup-0.1.5-x64.exe` `d1145a87dc8d8ec89bcb55f5a13dbae9f3fab95848a54adc1912da02d82bfd4e`
  - `UpdateAgent.Agent-0.1.5-x64.msi` `6772c6e7be0bc3fff635eda82ec156644810b527b984755cb5644c76838b2af7`
- 공개된 `v0.1.5` 릴리스 에셋 3개의 다운로드 해시가 릴리스 노트와 일치함을 GitHub REST API로 확인:
  - `UpdateAgent.Agent-Setup-0.1.5-x64.exe` `4af569f5d84a4370c6dfcc7c3d26e8280eac53091a0ec2cb97460a34d6e583a6`
  - `UpdateAgent.LabManager-Setup-0.1.5-x64.exe` `7c2e5a0058a3571c72236f54ea63e96921596b15153f02d7ae6765a51ee79ec3`
  - `UpdateAgent.Agent-0.1.5-x64.msi` `1edcc290c4bbc33f5bc8b4c64666f39c3977c9f437064169c9d6ae2d28d3ef3d`
- 기존 `v0.1.4`, `v0.1.3` 릴리스는 변경하지 않음.

---

## 2026-11-19 — Inno Setup 덮어쓰기 설치 시 앱 종료 순서 보장 (v0.1.4)

### 문제

- Agent 인스톨러는 세션 도우미/CLI/서비스만 종료하고 새 학생용 GUI(`UpdateAgent.Agent.Desktop.exe`)는 종료하지 않았다. 창이 열린 상태에서 덮어쓰기 설치하면 파일이 잠겨 언인스톨/설치가 실패할 수 있었다.
- `taskkill`이 반환되는 것만 기다리고 프로세스가 실제로 사라졌는지는 확인하지 않아, 종료 전에 언인스톨이 시작될 수 있었다.

### 수정

| 영역 | 내용 |
|---|---|
| 종료 대상 | Agent: `UpdateAgent.SessionHelper.exe`, `UpdateAgent.Agent.Desktop.exe`, `UpdateAgent.Cli.exe`, `UpdateAgent.Service.exe` / LabManager: `UpdateAgent.LabManager.Desktop.exe`, `UpdateAgent.LabManager.exe` |
| 종료 확인 | `tasklist /FI "IMAGENAME eq ..." /NH` 결과를 임시 파일로 읽어 프로세스가 사라질 때까지 최대 5초 폴링 |
| 순서 강제 | `InitializeSetup`(사전 정리) → `PrepareToInstall`(언인스톨 전 종료, 실패 시 중단) → `ssInstall`(파일 교체 전 재확인, 실패 시 중단) → 실제 설치 |
| 서비스 | `sc stop` 후 강제 종료, SCM 자동 재시작은 설치 중 비활성화하고 `EnsureService`에서 복원 |
| 독립 제거 | `CurUninstallStepChanged(usUninstall)`에서도 동일한 종료 절차 사용 |
| 문서 | `docs/INSTALLER.md`에 종료 → 언인스톨 → 설치 순서와 종료 대상 기록 |

### 검증

- `dotnet build -c Release`: 경고 0/오류 0, 테스트 89개 통과.
- PowerShell 10개 파일 파싱, YAML 파싱, PII/email 스캔, 버전 동기화 검사 통과.
- Inno Setup 6.7.1로 두 설치 파일 모두 컴파일 성공, UTF-8 BOM 유지. MSI 빌드 및 COM 검증에서 `DesktopDir`와 `UpdateAgent.Agent.Desktop.exe` 포함 확인(파일 1264개).
- 로컬 빌드 SHA-256(CI가 소스에서 다시 빌드하므로 공개 에셋과 다를 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.4-x64.exe` `26a736cda9007fb204aafa1ef34e97ad9e4fa8a00cbf3e23b09984aff912d484`
  - `UpdateAgent.LabManager-Setup-0.1.4-x64.exe` `a7a94f2a78bc1be190ef998accd6c5d4c0e1e95fa8e61157c855fbb1bcd2615f`
  - `UpdateAgent.Agent-0.1.4-x64.msi` `b049ac9974186dc49552e97f31c3879d7433a030ba9ea10bfb97fb4675e620b1`
- 공개된 `v0.1.4` 릴리스 에셋 3개의 다운로드 해시가 릴리스 노트와 일치함을 GitHub REST API로 확인:
  - `UpdateAgent.Agent-Setup-0.1.4-x64.exe` `1de677d4952e84eb184722337f2f97757063b34f3cf216c392630114cf70b2ef`
  - `UpdateAgent.LabManager-Setup-0.1.4-x64.exe` `098235330938183cbd490db66cb6ed6b975e6c110c489274a0cca4b5c085d052`
  - `UpdateAgent.Agent-0.1.4-x64.msi` `eac6a19f7ed126fd253286a11fce08a3630a95acb3a9d64779f31e7a1268537e`
- 이미 공개된 `v0.1.3` 릴리스(에셋 3개, 해시 동일)는 변경하지 않음.

---

## 2026-11-18 — 학생/교사 설치 오류 수정 및 학생용 데스크톱 앱 추가 (v0.1.3)

### 원인

- 학생 PC: 기존 설치를 덮어쓰면 `[InstallDelete]`가 설치 폴더 전체를 지우고 `[Files]`가 기본 `appsettings.json`을 다시 만든다. 복원 단계가 `CopyFile(..., True)`(`FailIfExists`)를 사용해 이미 생성된 기본 파일 위에 기존 설정을 덮어쓰지 못하고 설치를 중단했다. Agent와 LabManager 인스톨러에 동일한 결함이 있었다.
- 교사 PC: 설치 마지막 단계에서 런처를 자동 실행하지만, `appsettings.json`에 기본 토큰(`change-me-*`)이 남아 있으면 헤드리스 서버가 보안상 즉시 종료된다. 런처는 원인과 무관하게 일반적인 `LabManager 서버에 연결할 수 없습니다` 메시지를 표시했다.

### 수정

| 영역 | 내용 |
|---|---|
| Agent 인스톨러 | 백업/복원 경로를 로그에 기록하고, 복원 시 기본 설정 파일을 덮어쓰도록 `FailIfExists`를 `False`로 변경 |
| LabManager 인스톨러 | 동일한 복원 결함 수정 |
| LabManager 자동 실행 | 기본 토큰이 남아 있으면 설치 후 런처 자동 실행을 건너뛰고 토큰 변경 안내 표시 |
| LabManager 런처 | 로컬 서버가 시작 직후 종료된 경우 기본 토큰/로그 경로를 포함한 구체적인 안내 표시 |
| 학생용 GUI | `UpdateAgent.Agent.Desktop`(WPF) 추가. 서비스 상태, 에이전트 ID, 컴퓨터명, LabManager URL, 진행 중/최근 작업을 읽기 전용으로 표시하고 새로고침 제공. 토큰은 읽지 않음 |
| 학생 바로가기 | Agent 인스톨러의 시작 메뉴/바탕화면/설치 완료 실행을 `cmd /k Cli.exe status`에서 `{app}\Desktop\UpdateAgent.Agent.Desktop.exe`로 교체 |
| 패키징 | `publish.ps1`, `build-inno.ps1`, `build-msi.ps1`, `Package.wxs`에 `agent\desktop` → `Desktop` 폴더 포함. MSI는 `DesktopFiles`를 `AgentFeature`(Level 1)에 포함 |
| 버전·문서 | 단일 소스 버전을 `0.1.3`으로 올리고 현재 명령 예시 동기화 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0, 오류 0.
- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release --no-build`: 89개 통과.
- `scripts\*.ps1` 11개 구문 분석 통과, `.iss` 두 파일 UTF-8 BOM 유지, 워크플로 YAML 파싱 통과.
- `scripts\publish.ps1` → `scripts\build-inno.ps1` → `scripts\build-msi.ps1`: Inno 2개와 MSI 1개가 `0.1.3`으로 빌드됨.
- 로컬 빌드 해시: Agent Setup `01125de46f1fa6491fa138402a9c8dd6ee9eec15466fe0d01a7b888afc29bb4f`, LabManager Setup `a828aaab1c51f2d6ad94732e90c90d494ddb351cd550672d4681fb3850843834`, MSI `54f3324f420bcfc4bd32686a132c1b65ef7d8088a4d565ed002e20dbb5c9af81`.
- Windows Installer COM으로 MSI에 `DesktopDir`과 `UpdateAgent.Agent.Desktop.exe`가 포함됐음을 확인.
- 실제 관리자 권한 PC/VM에서 업그레이드 보존/자동 실행 분기 재현은 환경 제약으로 미실시.

---

## 2026-11-18 — 2차 심층 자아성찰 감사 및 오류 수정 (v0.1.2)

### 목적

- `v0.1.1` 릴리스 기준으로 워크플로·인스톨러·런타임·문서를 독립 재감사하고 잔여 결함을 수정.
- 교사 대시보드가 학생용 설치 파일을 선택할 수 있던 문제와 무인 설치 경로의 경쟁 조건을 제거.

### 주요 수정

| 영역 | 내용 |
|---|---|
| 자동 업데이트 | `UpdateProduct`를 도입해 LabManager/Agent 자산을 제품 접두사로 구분 선택하고 제품별 현재 버전으로 비교 |
| 작업 상태 | `JobStateGuard`로 에이전트 ID·종료 상태·수신 시각·상세 요약 일치를 서버에서 검증하고 수신 시각으로 정규화 |
| 오버레이 | Ctrl+Shift+Esc 차단, 파이프 재연결 시 진행 상태 재동기화, 설정된 helper 실행 파일명으로 프로세스 감지 |
| 인스톨러 | Inno/MSI 제거 완료 대기, 무인 설정 실패 시 설치 중단, 서비스 복구 설정 임시 비활성화/복원, LabManager 기존 파일 정리 |
| 방화벽 | LabManager 규칙을 TCP 5080 포트 기반 Domain/Private로 통일, MSI 규칙에 그룹 지정 |
| 릴리스 | 기존 릴리스의 불필요한 자산 삭제 후 업로드, 릴리스 노트에 실제 파일명과 전체 무인 설치 명령 포함 |
| 버전·문서 | 단일 소스 버전을 `0.1.2`로 올리고 현재 명령 예시와 테스트 수(89개)를 동기화 |
| 최종 회검토 | 에이전트 시계 왜곡에도 상태 순서 검증이 동작하도록 `ReportedAt` 분리, LabManager 인스톨러가 설정된 포트로 방화벽 규칙 생성, 제품 자산 폴백 보강 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0, 오류 0.
- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release --no-build`: 89개 통과.
- `scripts\*.ps1` 11개 구문 분석 통과, `.iss` 두 파일 UTF-8 BOM 유지, 워크플로 YAML 파싱 통과.

---

## 2026-11-17 — 심층 자아성찰 감사 및 오류 수정 (v0.1.1)

### 목적

- `v0.1.0` 릴리스 이후 발견된 자동 업데이트 URL 오류와 릴리스/인스톨러/런타임 결함 수정.
- 재발 방지를 위한 회귀 테스트와 빌드 가드 추가.

### 주요 수정

| 영역 | 내용 |
|---|---|
| 자동 업데이트 | 저장소 상수를 `ahbiyout-all/WinAutoUpdate`로 고정하고 정확한 최신 릴리스 API URL을 테스트 |
| 버전 | `Directory.Build.props`를 `0.1.1`로 올려 잘못된 URL이 포함된 `v0.1.0`을 대체 |
| 인증 | 기본 토큰이면 LabManager 시작을 중단하고, `AllowDefaultTokens`는 로컬 개발 전용 옵션으로 추가 |
| 작업 상태 | LabManager가 에이전트 체크인의 소유자/종료 상태/시간 순서를 검증 |
| 오버레이 | 파이프 DACL 명시, 세션 helper 중복 실행 방지, 파이프 연결 끊김 시 오버레이 즉시 숨김 |
| 인스톨러 | `appsettings.json` 복원을 `InstallDelete`/`[Files]` 이후로 이동, MSI 재부팅 코드/서비스 실패 전파 처리 |
| 릴리스 | 수동 버전 입력 제거, `env` 기반 값 전달, 태그 커밋/자산 개수/ISCC 6/gh 검증, 단일 concurrency 적용 |
| 스크립트 | UTF-8 BOM 검증, 공유 게시 폴더 정리, 방화벽 기본/버전 규칙 통일, 닷넷 도구 매니페스트를 `.config/`로 이동 |
| 테스트 | 66개 xUnit 테스트로 URL, 토큰, 캐시, 브라우저 인자, Unicode STARTUPINFO 회귀 검증 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0, 오류 0.
- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release --no-build`: 66개 통과.
- `.iss` 두 파일 모두 UTF-8 BOM 유지.

---

## 2026-11-17 — 독립 Tauri 데스크톱 앱 스캐폴드 추가

### 목적

- 기존 UpdateAgent .NET 솔루션과 연결하지 않는 별도 Tauri 2 데스크톱 앱 기본 템플릿 제공.
- 교사 대시보드의 `--app=` 모드를 대체하지 않으며, WebView2 사용 결정도 변경하지 않음.

### 변경

| 파일 | 내용 |
|---|---|
| `desktop/tauri/` | Vite + Tauri 2 기본 템플릿(`index.html`, `src/`, `vite.config.js`, `package.json`) |
| `desktop/tauri/src-tauri/` | `Cargo.toml`, `build.rs`, `tauri.conf.json`, capabilities, placeholder 아이콘, Rust 진입점 |
| `.gitignore` | 허용 목록에 `desktop/tauri` 추가, `node_modules`/`dist`/`target`은 제외 |

### 검증

- `npm install`, `npm run build` 성공.
- `cargo check` 성공(Tauri 2.12, Windows WebView2).
- `UpdateAgent.sln`, 인스톨러, 릴리스 산출물에는 영향 없음.

---

## 2026-11-17 — SessionHelper 오버레이 실행 실패 수정 / 설치 시 선(先)제거

### 목적

- 학생 PC에서 서비스가 `SessionHelper 실행` 로그를 1분마다 남기지만 실제 오버레이 프로세스가 남지 않는 문제 수정.
- 설치 프로그램을 실행하면 기존 설치를 묻지 않고 먼저 제거한 뒤 설치하도록 변경.

### 오버레이 원인

- `SessionLauncher`는 `CreateProcessAsUserW`(`CharSet.Unicode`)를 호출하면서 `STARTUPINFO` 구조체에는
  `CharSet`을 지정하지 않아 `lpDesktop`이 ANSI로 마셜링됨.
- 그 결과 `winsta0\default`가 깨진 문자열로 전달되어 프로세스는 생성 직후 종료.
- 서비스는 성공 로그만 남기고 `IsHelperAlive()`가 프로세스를 찾지 못해 60초 간격으로 재시도.
- 같은 exe를 사용자 세션에서 수동 실행하면 정상적으로 유지되는 것으로 확인.

### 변경

| 파일 | 내용 |
|---|---|
| `src/UpdateAgent.Core/Sessions/SessionLauncher.cs` | `StartupInfo`에 `CharSet.Unicode` 지정, 불필요한 `CREATE_NEW_CONSOLE` 제거 |
| `installer/InnoSetup/UpdateAgent.Agent.iss` | Inno/MSI 기존 설치를 무인 제거, 실패 시 설치 중단, `appsettings.json` 백업/복원 |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | Inno 기존 설치를 무인 제거, 실패 시 설치 중단, `appsettings.json` 백업/복원 |
| `docs/INSTALLER.md`, `docs/AI-WORK-INSTRUCTIONS.md` | 선제거 흐름과 실패 정책 반영 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0개, 오류 0개.
- 동일 exe 수동 실행(`--session 1`): 프로세스 유지 확인.
- 설치된 서비스 재시작 및 로그 확인은 관리자 권한이 있는 PC/VM에서 재설치 후 수행 필요.

---

## 2026-11-17 — 브라우저 앱 모드 대시보드로 전환 (최종)

### 목적

- 네이티브 WPF 콘솔 대신 시스템에 설치된 Edge/Chrome의 `--app=` 단독 창을 교사용 대시보드로 사용.
- 주소창/툴바가 없는 별도 창을 제공하되 WebView2/내장 브라우저 프레임은 사용하지 않음.
- 앱 창을 닫아도 로컬 API 서버는 계속 실행해 학생 PC 체크인을 유지.

### 변경

| 파일 | 내용 |
|---|---|
| `src/UpdateAgent.LabManager/wwwroot/` | `3997a4d`의 웹 대시보드(`index.html`, `app.js`, `styles.css`) 복원 |
| `src/UpdateAgent.LabManager/Program.cs` | `UseDefaultFiles`/`UseStaticFiles` 복원, `--headless`/`--no-window` 필터 유지 |
| `src/UpdateAgent.LabManager.Desktop/` | WPF UI 제거, 창 없는 런처로 변경 |
| `src/UpdateAgent.LabManager.Desktop/Services/BrowserLauncher.cs` | Edge 우선 감지, Chrome 폴백, `--app=`/전용 프로필 실행 |
| `src/UpdateAgent.LabManager.Desktop/Services/DesktopSettings.cs` | DPAPI 토큰 로직 제거, 대시보드 URL/창 크기 설정만 유지 |
| `scripts/publish.ps1` | LabManager 게시에 `wwwroot` 포함 |
| `scripts/build-inno.ps1` | `.br`/`.gz` 차단, `wwwroot\index.html` 필수 확인 |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | 런처 바로가기 유지, `wwwroot` InstallDelete 제거 |
| `README.md`, `docs/INSTALLER.md`, `docs/RELEASE.md` | 앱 모드, localStorage 토큰, 서버 유지 정책 반영 |

### 주요 동작

- 런처는 창 없이 로컬 서버 health를 확인하고, 없으면 `UpdateAgent.LabManager.exe --headless`를 시작한다.
- Edge를 우선, 없으면 Chrome을 찾아 `--app=http://127.0.0.1:5080/` 창을 연다.
- 전용 프로필(`%LOCALAPPDATA%\UpdateAgent\LabManager\browser-profile`)을 사용하며 관리자 토큰은 localStorage에 저장한다.
- 외부 링크는 `target="_blank"`로 기본 브라우저에서 연다.
- 앱 모드 창을 닫아도 서버는 의도적으로 종료하지 않는다.

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0개, 오류 0개.
- `scripts\publish.ps1`: `publish\labmanager\wwwroot`에 `index.html`, `app.js`, `styles.css` 생성 확인.
- 스모크 테스트: 런처가 Edge 앱 모드 창을 열고 종료해도 서버가 계속 수신 중임을 확인.
- HTTP 확인: `/`(10406 bytes), `/app.js`(30045 bytes), `/styles.css`(12982 bytes), `/api/health` 모두 200.

---

## 2026-11-17 — 브라우저 대시보드 제거 및 네이티브 WPF 관리 콘솔 도입 (이후 철회)

### 목적

- 교사용 화면을 WebView2/브라우저 프레임 없이 네이티브 WPF 앱으로 제공.
- LabManager는 헤드리스 HTTP API만 담당하고, 상태는 서버가 계속 원본으로 유지.
- 창 닫기 = 앱 종료, 외부 링크는 OS 기본 브라우저로만 열기.

### 변경

| 파일 | 내용 |
|---|---|
| `src/UpdateAgent.LabManager.Desktop/` | 신규 WPF 프로젝트. 메인 창/다이얼로그/API 클라이언트/DPAPI 설정/서버 자동 시작 |
| `src/UpdateAgent.LabManager/Program.cs` | `--headless`/`--no-window` 플래그 필터, `UseDefaultFiles`/`UseStaticFiles` 제거 |
| `src/UpdateAgent.LabManager/wwwroot/` | 브라우저 대시보드(`index.html`, `app.js`, `styles.css`) 완전 삭제 |
| `scripts/publish.ps1` | Desktop 프로젝트를 `publish\labmanager`에 함께 게시, `-AgentOnly`에서 제외 |
| `scripts/build-inno.ps1` | Desktop exe 필수 파일 검사, 빈 정적 자산 매니페스트 차단 |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | 바로가기/[Run]/아이콘을 Desktop exe로 변경, 두 LabManager 프로세스 종료, 레거시 `wwwroot` 삭제 |
| `README.md`, `docs/INSTALLER.md`, `docs/RELEASE.md` | 네이티브 콘솔, DPAPI 토큰, 헤드리스 서버, 기본 브라우저 링크 반영 |

### 주요 동작

- 데스크톱 앱이 로컬 API 서버를 준비한다. 없으면 같은 폴더의 `UpdateAgent.LabManager.exe --headless`를 실행하고,
  종료 시 자신이 시작한 프로세스만 정리한다.
- 관리자 토큰은 `X-Admin-Token` 헤더로 전달하고, DPAPI로 암호화해
  `%LOCALAPPDATA%\UpdateAgent\LabManager\admin-token.bin`에 저장한다.
- 5초 폴링으로 요약 카드/에이전트/작업/감사 로그를 갱신하고, 30분 간격으로 GitHub Releases를 확인한다.
- 작업 시작/상세/Maestro 확인/토큰/업데이트 다이얼로그와 토스트를 네이티브 창으로 구현했다.

### 런타임 스모크 테스트에서 발견한 문제와 수정

| 증상 | 원인 | 수정 |
|---|---|---|
| 앱 시작 직후 "LabManager 서버에 연결할 수 없습니다" 표시 | 일부 환경에서 `localhost` DNS 해석이 약 2.1초 걸려 2초 헬스체크 타임아웃 초과 (`127.0.0.1`은 2ms) | 헬스체크 타임아웃을 6초로 늘리고 로컬 주소는 `127.0.0.1`로 확인. API 클라이언트도 HTTP `localhost`를 `127.0.0.1`로 정규화 |
| 메인 창이 뜨자마자 앱이 크래시 (`InvalidOperationException: TwoWay … 읽기 전용 속성 'JobProgress'`) | `ProgressBar.Value`가 기본 TwoWay 바인딩인데 `JobProgress`/`Progress`가 읽기 전용 | 두 ProgressBar 바인딩에 `Mode=OneWay` 명시 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0개, 오류 0개.
- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release --no-build`: 56개 통과.
- `scripts\publish.ps1`: LabManager 폴더에 두 exe 생성, `wwwroot` 없음 확인 (이후 앱 모드 전환으로 철회).
- `scripts\build-inno.ps1`: Agent/LabManager 설치 파일 2개 빌드 성공.
- `.iss` 두 파일 모두 UTF-8 BOM 유지 확인.
- 데스크톱 앱 실행: 제목 `UpdateAgent · 교사 관리 콘솔`, v0.1.0 배지, 통계 카드/에이전트/작업/감사 탭 렌더링, 연결 오류 없음.
- 서버가 없을 때 자동 시작, 창 닫기 시 앱과 자동 시작 서버 종료 확인.
- 서버가 이미 실행 중이면 재사용하고 창을 닫아도 서버를 유지함을 확인.
- 헤드리스 API 스모크 테스트: `/api/health`, `/api/admin/overview|agents|jobs|audit|update` 응답 확인.

---

## 2026-11-16 — build.bat 및 에이전트 전용 빌드 경로

### 목적

- Windows에서 더블클릭 한 번으로 에이전트를 빌드하고, 버전이 붙은 설치 파일을 버전별 폴더에 모아 바로 확인.
- 실행 파일 이름에 버전을 붙여 버전을 관리하되, .NET apphost를 직접 rename하지 않는 안전한 방식 사용.

### 변경

| 파일 | 내용 |
|---|---|
| `build.bat` | 신규. `Directory.Build.props` 버전 읽기 → `publish.ps1 -AgentOnly` → `build-inno.ps1 -AgentOnly` → `build\agent\<버전>\` 조립 → 탐색기 열기 |
| `scripts/publish.ps1` | `-AgentOnly` 추가. LabManager 게시를 생략하고 안내 문구도 에이전트 전용으로 변경 |
| `scripts/build-inno.ps1` | `-AgentOnly` 추가. LabManager 스테이징/검증/컴파일 생략, `build-info.json`에 `target` 필드 추가 |
| `.gitignore` | `build.bat` 허용, `build/` 무시 추가 |
| `README.md`, `docs/INSTALLER.md` | `build.bat` 사용법과 버전 표기 원칙 추가 |

### 검증

- `cmd /c build.bat` 실행: 게시 → Inno 컴파일 성공(76.9초), 완료 후 탐색기 자동 열림.
- 산출물: `build\agent\0.1.0\UpdateAgent.Agent-Setup-0.1.0-x64.exe`(96.5 MB), `.sha256`, `build-info.json`.
- `build-info.json`: `target=agent`, `productVersion=0.1.0`, `containsSecrets=false`.
- LabManager 산출물이 섞이지 않았고, 성공 후 `build\.staging`이 삭제된 것을 확인.
- `.NET` apphost 파일 이름을 직접 바꾸면 내부 DLL을 찾지 못해 실행 실패하는 것을 재확인.
  따라서 버전은 Inno Setup 설치 파일 이름으로만 관리.

### 버전 판정

- 빌드 스크립트와 출력 폴더 구조만 추가되었고 응용 프로그램 동작은 바뀌지 않음.
  SemVer **버전 변경 없음**(0.1.0).

### 남은 작업

- 관리자 권한이 있는 VM/실 PC에서 설치 → 업그레이드 → 제거 검증.

---

## 2026-11-16 — 범용 AI 작업 지시서 문서화

### 목적

- 누적된 요청 텍스트를 특정 프로젝트/기술에 묶이지 않은 **재사용 가능한 AI 작업 SOP**로 정리.
- "실현 가능성 판정 → 로드맵 → 구현 → 자아성찰 → 오류 수정 → 문서/버전 동기화" 흐름을 고정.

### 변경

| 파일 | 내용 |
|---|---|
| `docs/AI-WORK-INSTRUCTIONS.md` | 신규. 7개 절대 원칙, 10단계 표준 파이프라인, 정밀 오류 검증 체크리스트, SemVer 판정 규칙, 완료 정의, 요청 템플릿 수록 |
| `README.md` | 문서 색인에 AI 작업 지시서 링크 추가 |
| `docs/WORKLOG.md` | 이 작업 로그 추가 |

### 검증

- 원본 요청 목록의 항목 유형을 지시서의 7개 버킷(기능/UI/빌드/보안/문서/버전/품질)으로 분류해 누락 없이 매핑.
- 모순되는 요구(고정 창 크기 vs 반응형, 모바일 패키징 vs 제거 등)는 "실현 가능성 판정" 예시로 일반화.
- 작성 후 파일을 재독해하여 잘못 섞인 문자(한자 혼용) 1건을 발견·수정.

### 버전 판정

- 기능/동작/빌드 산출물 변경 없음(문서 추가만). SemVer **버전 변경 없음**으로 판정.

### 남은 작업

- 이 지시서를 실제 후속 작업에 적용할 때는 매 단계 자아성찰 게이트 결과를 작업 로그에 남긴다.

---

## 2026-11-16 — GitHub 자동 업데이트 기반 구성

### 목적

- GitHub 저장소(`ahbiyout-all/WinAutoUpdate`) 공개를 전제로 한 실시간 업데이트 확인.
- 대시보드에서 새 버전을 자동 감지해 알림 팝업 표시.
- 버전 정보를 코드/빌드/릴리스/UI에서 일관되게 유지.

### 변경

| 영역 | 내용 |
|---|---|
| 공통 상수 | 개발자 `AhBiYout`, 계정 `ahbiyout-all`, 저장소 `WinAutoUpdate`, 블로그 URL, Releases API, 확인 주기(30분) 추가 |
| 빌드 메타데이터 | `Directory.Build.props`의 `Authors/Company/Copyright/RepositoryUrl`을 AhBiYout 기준으로 변경 |
| 업데이트 모델 | `UpdateAgent.Shared/Updates/AppUpdateInfo.cs` 추가 |
| GitHub 조회 | `GitHubReleaseChecker` 추가: 최신 릴리스 조회, `v` 접두사 제거, 숫자 버전 비교, 설치 파일 자산 선택 |
| LabManager | `UpdateCheckService`(10분 캐시), `GET /api/admin/update?force=true` 추가 |
| 대시보드 | 버전 배지, 새 버전 점, 업데이트 팝업(버전/노트/자산/GitHub·블로그 링크/업데이트 버튼), 30분 재확인, 버전별 닫기 |
| 바닥글 | GitHub 저장소/릴리스/공식 블로그 링크 추가 |
| 테스트 | `GitHubReleaseCheckerTests` 추가(버전 비교, 정규화, MSI/Setup 자산 선택, 오류 처리) |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0, 오류 0.
- `dotnet test`: 56개 전부 통과.
- 업데이트 팝업은 첫 대시보드 로드 후 백그라운드에서 GitHub API를 호출합니다.
- 저장소가 비공개이면 API 조회가 실패하며, 대시보드는 "확인 실패"만 표시하고 설치는 막지 않습니다.

### 비고

- 버전의 단일 소스는 `Directory.Build.props`의 `<Version>`입니다.
- 릴리스 자산은 설치 파일만 올리는 원칙을 유지합니다.

---

## 2026-11-16 — WiX MSI 설치 프로그램 보강

### 변경

- 방화벽 확장(`WixToolset.Firewall.wixext` 5.0.2) 추가.
  - `UpdateAgent Agent`, `UpdateAgent Agent <버전>` 규칙을 설치 시 조용히 등록.
- 기존 설치 감지 대화상자(`Dialogs.wxs`) 추가.
  - `WIX_UPGRADE_DETECTED`일 때 라이선스 화면 다음에 표시.
  - "덮어쓰기 설치 / 취소" 제공, `/qn`에서는 자동 업그레이드.
- `install-agent-msi.ps1` 보강.
  - 기존 MSI 감지 시 `덮어쓰기 / 제거 후 설치 / 취소` 프롬프트.
  - `-Force`(무프롬프트 업그레이드), `-Quiet`(무인) 지원.
- 스테이징 정리 강화.
  - 문서/심볼/압축/포터블/바로가기 파일 제거.
  - 서비스 `appsettings.json`은 비밀값 없는 템플릿으로 교체.
- 한국어 방화벽 진행 문자열을 `ko-KR.wxl`에 추가.

### 문제와 해결

| 문제 | 원인 | 해결 |
|---|---|---|
| `Dialogs.wxs`가 MSI에 포함되지 않음 | 이름 없는 `UI`의 다이얼로그가 링크 단계에서 제외됨 | `UI Id` 부여 + `Package.wxs`에서 `UIRef` 참조 |
| 대화상자는 포함되지만 UI 순서에 없음 | `InstallUISequence`를 `Fragment` 바로 아래에 둠 | `InstallUISequence`를 `UI` 요소 내부로 이동 |
| 방화벽 지역화 변수 누락(WIX0102) | Firewall 확장 문자열 미정의 | `ko-KR.wxl`에 Firewall 문자열 8개 추가 |
| ICE03 형식 문자열 오류 | Control Text의 `[설치]`가 MSI 속성 참조로 해석됨 | `&quot;설치&quot;`로 변경 |

### 검증

- `dotnet wix msi decompile`로 MSI 테이블 확인:
  - `ExistingVersionDlg` 존재 및 `InstallUISequence`에 배치.
  - 방화벽 규칙 2개(기본/버전) 확인.
- `dotnet wix msi validate -sice ICE60`: 통과.
- MSI: `artifacts\msi\UpdateAgent.Agent-0.1.0-x64.msi`, 약 82.4 MB.

---

## 2026-11-16 — Inno Setup 설치 프로그램 추가

### 목적

- 개별 파일(Unpacked Multi-File) 설치를 기본으로 하는 Windows 설치 환경 제공.
- 기존 앱 강제 종료 → 삭제/덮어쓰기 확인 → 라이선스 → 설치 → 바로가기 선택 → 실행.
- 프로그램 추가/제거 정식 등록, 무인 설치, 방화벽 사전 등록.

### 추가 파일

| 파일 | 설명 |
|---|---|
| `installer/InnoSetup/UpdateAgent.Agent.iss` | 학생 PC용 설치 스크립트(AppID `{6F0C2E8A-...}`) |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | 교사 PC용 설치 스크립트(AppID `{A4D7F1C3-...}`) |
| `scripts/build-inno.ps1` | 스테이징/정리/검증/컴파일/해시 파이프라인 |
| `docs/INSTALLER.md` | 설치 프로그램 공식 문서 |
| `docs/WORKLOG.md` | 이 작업 로그 |

### 구현 내용

- 두 제품에 서로 다른 고유 AppID와 설치 폴더를 사용.
- Inno `[Files]`로 폴더 트리를 그대로 설치(개별 파일).
- 설치 전 `taskkill`/`sc stop`으로 기존 프로세스 강제 종료.
- Inno 설치 키와 MSI 등록 정보를 함께 감지해 삭제/덮어쓰기/취소를 질문.
- 무인 설치는 항상 제거 후 설치, `/LabManagerUrl` + `/AgentTokenFile`로 초기 설정 가능.
- `netsh advfirewall`로 기본 이름/버전 이름 규칙을 그룹 단위로 등록·정리.
- 설치/제거 시 서비스 등록·해제, 실패 시 자동 재시작 설정 적용.
- 바탕화면 바로가기 작업(기본 체크)과 설치 후 실행 옵션 제공.
- 프로그램 추가/제거에 `UpdateAgent Agent <버전>`, `UpdateAgent LabManager <버전>`으로 등록.

### 포함 파일 점검

- 스테이징에서 문서/심볼/압축/포터블/`web.config` 등 12개 제거.
- 검증 단계에서 금지 패턴이 남아 있으면 빌드 실패.
- 최종 포함: Agent 853개(304.9 MB), LabManager 361개(108.3 MB).

### 검증 결과

| 항목 | 결과 |
|---|---|
| Inno Setup 6 컴파일 | 성공 |
| Agent 설치 파일 | `artifacts\inno\UpdateAgent.Agent-Setup-0.1.0-x64.exe` (96.6 MB) |
| LabManager 설치 파일 | `artifacts\inno\UpdateAgent.LabManager-Setup-0.1.0-x64.exe` (약 35.6 MB) |
| SHA-256 | `4e6454f8d421d249b84d256bb3ec836468b0095208a6053b4915dd94d6a7ced0` (Agent) |
| SHA-256 | `b1ae1842871226e87721a66f1aaa85c7868b729c2b1b1d5eaa7b1dd189d41194` (LabManager) |
| 파일 버전 정보 | `AhBiYout`, `UpdateAgent Agent/LabManager Installer`, 0.1.0 확인 |
| 실제 설치/제거 | **미실행** — 현재 세션이 비관리자 권한 |

### 문제와 해결

| 문제 | 원인 | 해결 |
|---|---|---|
| Inno 컴파일 오류(390행) | `LoadStringFromFile` 미지원 | `LoadStringsFromFile`로 토큰 파일 첫 줄 읽기 |
| 한국어 문자열 깨짐 위험 | `.iss`가 UTF-8 BOM 없이 저장됨 | 두 `.iss`를 UTF-8 BOM으로 저장하고 빌드 전 검증 |
| 불필요한 `web.config` 포함 | ASP.NET Core 게시물에 IIS용 파일 포함 | 스테이징 금지 목록에 파일 이름 추가 |

### 남은 작업

- 관리자 권한이 있는 실제 PC/VM에서 설치·업그레이드·제거 테스트.
- 코드 서명 인증서 적용(파일럿 이후).
- GitHub Actions 릴리스 워크플로와 자동 푸시 스크립트 연결.

---

## 2026-11-16 — GitHub 릴리스/자동 업데이트 파이프라인

### 목적

- 버전을 코드/빌드/태그/릴리스/대시보드에서 한 곳(`Directory.Build.props`)으로 동기화.
- 태그 push만으로 설치 파일이 빌드되어 GitHub Release에 첨부되는 파이프라인 구성.
- 로컬 릴리스 스크립트가 비밀값/개인정보를 검사하고, 스크립트 자체는 저장소에 올리지 않음.

### 추가/변경 파일

| 파일 | 설명 |
|---|---|
| `.github/workflows/release.yml` | `v*` 태그 및 수동 실행 → 빌드/테스트 → Inno+MSI → 릴리스 |
| `scripts/github-push.ps1` | 버전 올림, 빌드/테스트, 비밀값 검사, 커밋/태그/push (**로컬 전용**) |
| `scripts/github-push.bat` | PowerShell 래퍼 (**로컬 전용**) |
| `docs/RELEASE.md` | 버전/릴리스/자동 업데이트/문제 해결 공식 문서 |
| `.gitignore` | 허용 목록(allowlist) 방식으로 전면 교체 |
| `README.md` | Inno 권장, 릴리스 섹션, 문서 링크, 테스트 56개 반영 |
| `docs/MSI.md` | Inno 권장 안내 및 상호 링크 |
| `docs/PILOT.md` | 4-A Inno 권장, 4-B MSI 대안, 4-C 레거시로 재구성 |
| `scripts/publish.ps1` | 다음 단계 안내에 Inno/릴리스 문서 반영 |

### 릴리스 동작

- 릴리스 자산은 Inno Setup 설치 파일 2개와 선택적 MSI만 업로드.
- `.sha256`, `build-info.json`, 로그는 자산이 아니라 릴리스 노트에만 기록.
- 자동 업데이트는 `*Setup*.exe` → `.msi` → 첫 자산 순서로 선택.
- Actions는 Chocolatey로 Inno Setup을 설치하고 `ISCC.exe` 경로를 동적으로 찾음.
- `workflow_dispatch`에서는 입력 버전(없으면 `Directory.Build.props`)으로 태그/릴리스 생성.
- 태그 push 시 태그와 `Directory.Build.props` 버전이 다르면 빌드 실패.

### `.gitignore` 허용 목록

- `*`로 모두 무시한 뒤 `!*/`로 디렉터리만 통과시키고 필요한 파일만 재포함.
- `bin/`, `obj/`, `publish/`, `artifacts/`, `.vs/`는 트리 안에서도 최종 차단.
- `scripts/github-push.ps1`, `scripts/github-push.bat`은 명시적으로 제외.
- 검증: `git check-ignore`로 두 스크립트가 무시됨을 확인, 허용 목록 외 빌드 산출물이 보이지 않음.

### 검증 결과

| 항목 | 결과 |
|---|---|
| `dotnet build` / `dotnet test` | 경고 0/오류 0, 56개 통과 |
| 워크플로 YAML | Python `yaml.safe_load` 파싱 성공 (12 steps) |
| `github-push.ps1 -DryRun` | 비밀값/개인정보 검사 통과 (105개 텍스트 파일) |
| Inno `.iss` BOM | 두 파일 모두 UTF-8 BOM 유지 |
| 실제 태그 push/Actions 실행 | **미실행** — GitHub 인증/원격 push 필요 |
| 실제 설치/제거 | **미실행** — 관리자 권한 VM/실 PC 필요 |

### 남은 작업

- `gh auth login` 후 첫 릴리스(`v0.1.0` 또는 다음 패치 버전) push.
- GitHub 저장소 Settings → Actions → Workflow permissions를 `Read and write`로 설정.
- 관리자 권한 VM/실 PC에서 Inno 설치·업그레이드·제거 체크리스트 실행.

---

## 템플릿

새 작업을 기록할 때 아래 형식을 사용합니다.

```markdown
## YYYY-MM-DD — 제목

### 목적
### 변경
### 검증
### 문제와 해결
### 남은 작업
```

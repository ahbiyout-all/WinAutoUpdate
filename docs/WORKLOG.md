# 작업 로그

이 문서는 UpdateAgent 프로젝트의 주요 작업 이력을 날짜별로 기록합니다.
빌드/테스트 결과와 산출물 해시는 작업 시점 기준이며, 이후 빌드에서 달라질 수 있습니다.

---

## 2026-11-24 — 교체 선택 시 릴리스 내장 토큰 사용 (v0.1.33)

v0.1.32 파일럿에서 **기존 토큰을 삭제하고 새 토큰 기록**을 선택하면 `/LabManagerTokenFile=` 또는
`/AgentTokenFile=`을 요구해 설치가 중단됐습니다. 질문 화면에서는 파일을 묻지 않으면서 실제로는
명령줄 인자가 필요한 UX 불일치가 원인입니다. 선택한 방식대로 교체 시 릴리스 내장 토큰을 사용하도록
바꿨습니다.

- LabManager: `LoadBundledLabManagerTokens`로 내장 두 줄을 공통 검증하고, 첫 설치와 교체 선택
  업데이트가 같은 함수를 사용합니다. `/LabManagerTokenFile=`을 주면 외부 파일이 우선합니다.
- Agent: 교체 선택 시 내장 `agent-token.txt`를 사용합니다. 새 URL/토큰 파일이 없으면 기존
  `agent.json`의 `LabManagerUrl`을 유지해 토큰만 교체합니다.
- 교체용 토큰이 비어 있거나 샘플/기본 값이면 여전히 설치 전에 fail-closed로 중단합니다.
- 재사용 선택과 무인 설치는 기존 동작(내장 토큰 미사용)을 유지합니다.
- 대시보드에서 토큰을 변경했다면 교체 선택 시 릴리스 빌드 시점 값으로 되돌아가므로 주의가 필요합니다.
- 버전은 4개 버전 파일을 `0.1.33`으로 동기화했습니다.

### 검증

- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release`: **195개 통과**.
- `.\scripts\publish.ps1`, `.\scripts\build-inno.ps1 -NoPublish`: Inno Setup 6 컴파일 성공.
- 로컬 산출물은 검증 후 삭제했습니다. 실제 선택 화면은 파일럿 환경에서 재확인 필요.

---

## 2026-11-24 — 업데이트 설치 시 기존 토큰 처리 선택 (v0.1.32)

기존 설치가 있는 PC에서 인스톨러를 다시 실행하면 **기존 토큰 처리** 화면이 표시됩니다.
기본값은 `기존 토큰을 백업해 재사용 (권장)`이고, `기존 토큰을 삭제하고 새 토큰 기록`을
선택하면 새 토큰 파일을 반드시 지정해야 합니다.

| 제품 | 화면 표시 조건 | 교체 시 필요한 입력 |
|---|---|---|
| Agent | 유효한 기존 `agent.json`이 있을 때 | `/AgentTokenFile=<파일>` 또는 `agent-install.ini`의 `AgentTokenFile` |
| LabManager | 이전 설치 흔적이 있을 때 | `/LabManagerTokenFile=<파일>`(1행 AdminToken, 2행 AgentToken) |

- 무인 설치(`/VERYSILENT`)는 화면을 건너뛰고 재사용 기본값을 유지합니다.
- 첫 설치도 화면을 건너뛰고 기존 규칙(첫 설치 전용 내장 토큰)을 사용합니다.
- 교체를 선택했지만 토큰 파일이 없거나 비었거나 샘플/기본 값이면, 기존 설치를 제거하거나
  파일을 교체하기 **전에** 설치를 중단합니다.
- 재사용 선택 시에는 기존 설정을 백업·복원한 뒤 URL/경로만 갱신합니다. 교체 선택 시에는
  백업에서 복원된 `appsettings.json`의 두 토큰만 새 값으로 바꾸고, 남아 있는 런타임
  관리자 토큰 재정의 파일을 제거해 새 토큰이 즉시 적용되도록 합니다.
- 버전은 `Directory.Build.props`, Inno `.iss` 2개, WiX `.wixproj`를 `0.1.32`로 동기화했습니다.

### 검증

- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release`: **195개 통과**.
- `.\scripts\publish.ps1`, `.\scripts\build-inno.ps1 -NoPublish`: Inno Setup 6 컴파일 성공.
- 비밀값이 없는 로컬 산출물은 검증 후 삭제했습니다. 실제 설치 화면은 파일럿 환경에서 확인 필요.

### 게시 결과

수동 실행 `45339435193`이 성공했고, 태그 `v0.1.32`는 커밋 `c2edea8`을 가리킵니다.
빌드 로그에서 첫 설치 토큰 내장과 `containsSecrets` 검증을 확인했습니다.

| 파일 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.32-x64.exe` | `51e43ce999187109f1d950f07b57a91bdbb6442daf51e13832a24d4a13830f8d` |
| `UpdateAgent.LabManager-Setup-0.1.32-x64.exe` | `424adfa0a3dd6958af92eb529d94c82e4e927f8e811cd0daa3b0433df67a993c` |
| `UpdateAgent.Agent-0.1.32-x64.msi` | `7cb3638b98947e4f18eb64d7fbef50ee73a74d89b7f6eaaccd8946f4c87d8c34` |

- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.32
- 성공 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45339435193

---

## 2026-11-23~24 — 자아성찰 20회 반복 심층 감사 및 결함 수정 (v0.1.31)

### 배경

v0.1.30 릴리스와 파일럿 1차 문제 대응 후 설치 관리자, 스크립트/CI, 에이전트 런타임,
LabManager 런타임, 문서/UI 다섯 영역을 **4회씩 순회해 총 20회** 자아성찰을 수행했습니다.
발견된 결함은 모두 코드·테스트·문서에 반영하고 `v0.1.31`로 게시했습니다.

### 수정 내역 (20회)

| 회차 | 영역 | 확인한 결함과 조치 |
|---:|---|---|
| 1 | 설치 관리자 | 기존 `agent.json`이 있어도 URL/토큰이 비어 있거나 샘플이면 업그레이드를 허용하던 문제 → 저장된 설정도 동일하게 검증 |
| 2 | 설치 관리자 | 토큰 파일 없이 URL만 바꾼 업그레이드에서 내장 토큰이 새 설정을 덮을 수 있던 문제 → `ReadExistingAgentToken`으로 기존 토큰 보존, 요청 URL만 반영 |
| 3 | 설치 관리자 | BOM이 있는 `agent-install.ini`가 명령줄 인자 때문에 조용히 무시되던 문제 → 명령줄 키가 있어도 BOM/형식 오류면 중단 |
| 4 | 설치 관리자 | 학생 PC에 인바운드 방화벽 규칙을 만들고 MSI uninstall `3010/1641`을 실패로 처리하던 문제 → 인바운드 규칙 제거, 재시작 요청은 성공으로 처리하고 설치 계속 |
| 5 | 스크립트/CI | 오래된 환경 변수 토큰이 명시적 `-AgentTokenFile`보다 우선하던 문제 → 명시적 파일 우선 |
| 6 | 스크립트/CI | 임시 설정 파일에 토큰을 쓴 뒤 ACL을 걸어 최종 경로에 노출 구간이 있던 문제 → 이동 전 ACL 적용, `icacls` 실패는 치명 오류 |
| 7 | 스크립트/CI | `install-service.ps1`이 서비스 중지·파일 삭제 후에야 인자를 검증하던 문제 → 모든 명시적 입력 검증을 파괴 작업 앞으로 이동, 설정 파일 원자적 교체 |
| 8 | 스크립트/CI | `rev-parse` 실패 코드가 남아 CI 단계가 뜻하지 않게 실패하고, 출력이 없을 때 `.Trim()`이 null 오류를 내던 문제, 고정 릴리스 노트가 다음 버전에서 거짓이 되는 문제, 수동 실행 태그 push 중복 → null-safe 조회, 상태 초기화, 커밋 제목 기반 노트, `--target` 사용 |
| 9 | 에이전트 런타임 | `SanitizeFor`만으로는 Windows 11에서 기능 업데이트가 남아 원격 StartJob이 우회되던 문제 → 실행 지점 공통 `ForExecution` 적용 |
| 10 | 에이전트 런타임 | OS 감지 실패/`unknown`에서 기능 업데이트·오버레이를 추정하던 문제 → `SupportsFeatureUpdates`/`SupportsOverlay` fail-closed |
| 11 | 에이전트 런타임 | 원격 관리가 켜졌는데 기본/샘플 토큰으로 서비스가 시작되던 문제 → `AgentOptionsValidator`로 시작 시 차단 |
| 12 | 에이전트 런타임 | 서버 정책 DB 오류 시 마지막 정상 정책에 기능 업데이트 값이 남을 수 있던 문제 → 폴백 경로에서도 기능 업데이트 제거 |
| 13 | LabManager 런타임 | 관리자 토큰 override 파일 ACL에서 UAC 승격 전 현재 사용자 SID가 빠져 재시작 후 읽지 못하던 문제 → 현재 SID 명시 부여 |
| 14 | LabManager 런타임 | 위조·중복·만료된 명령 결과가 감사 로그에 성공으로 남던 문제 → 소유자/상태/만료 검사 후 수락된 ID만 반영, 나머지는 거부 기록 |
| 15 | LabManager 런타임 | 공유 토큰으로 임의 에이전트 ID와 check-in gate를 무한 생성하던 문제 → ID 형식 길이 제한, gate 상한 200, 초과 시 429 |
| 16 | LabManager 런타임 | 실패한 GitHub 확인이 10분간 캐시되거나 다른 제품 설치 파일이 제안되던 문제, API 직접 호출로 Maestro 확인을 우회하던 문제 → 성공 결과만 캐시, 제품 접두사 자산만 사용, 서버가 Maestro 게이트 강제 |
| 17 | 문서/UI | 버전 단일 소스 설명이 4개 파일 갱신 규칙과 어긋나던 문제 → `Directory.Build.props` + Inno 2개 + WiX 1개 표로 정리 |
| 18 | 문서/UI | `0.1.27`, `165개` 등 오래된 예제/테스트 수 → `0.1.31`, `195개`로 갱신 |
| 19 | 문서/UI | 시크릿 3개 짝·동일 값 규칙과 `containsSecrets` 체크리스트가 부정확하던 문제 → 시크릿 유무별 기대값과 로컬 전용 릴리스 스크립트 설명 보강 |
| 20 | 문서/UI | v0.1.30 무설정 첫 설치 기본 URL, 업그레이드 토큰 우선순위, LabManager 자동 시작 표현이 실제 동작과 다르던 문제 → `INSTALLER.md`/`PILOT.md`/`MSI.md`/`RELEASE.md` 정정 |

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 경고 0 / 오류 0.
- `dotnet test tests/UpdateAgent.Tests/UpdateAgent.Tests.csproj -c Release`: **195개 통과**.
- PowerShell 파서: 저장소 내 모든 `.ps1` 구문 오류 없음.
- 로컬 Inno/MSI 사전 빌드 통과(검증 후 로컬 산출물 삭제), 게시 자산 해시는 아래 표 참고.
- `.iss` 2개는 UTF-8 BOM 유지, `agent-install.sample.ini`는 BOM 없음.
- `git diff --check`: 공백/충돌 표시 없음.
- 실제 PC 설치, DNS/방화벽 변경, 대시보드 UI 확인은 파일럿 환경에서 별도 검증 필요.

### 게시 결과

첫 수동 실행(`45313411142`)은 태그가 없을 때 `git rev-parse` 출력이 null이어서 `.Trim()`이 실패한 CI 결함으로 중단됐습니다.
null-safe 조회로 수정해 커밋 `a0aad4b`를 push한 뒤 다시 실행한 `45313687780`이 성공했습니다.
태그는 `a0aad4b`를 가리키고, 워크플로가 태그를 push하지 않아 태그 이벤트 중복 실행은 없습니다.
빌드 로그에서 첫 설치 토큰 내장 메시지와 `containsSecrets` 검증을 확인했습니다.

| 파일 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.31-x64.exe` | `6bfe27e8fdf223207798c963831a8fd790c1433bb34d1079646883a97cb64801` |
| `UpdateAgent.LabManager-Setup-0.1.31-x64.exe` | `b093a4d9357a36bfdc3c50a7e85c7b56819e72a41dd80d0a01ee972b05aeb58c` |
| `UpdateAgent.Agent-0.1.31-x64.msi` | `38bf3ecd1138a55f9c2a08515cbd3d1f5192631fcbf397ef0a9480b8b3e08c3f` |

- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.31
- 성공 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45313687780
- 실패 워크플로(원인 기록): https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45313411142

---

## 2026-11-23 — 파일럿 1차 설치 문제 대응 (v0.1.30)

### 1. 관리자 토큰 오류

교사 PC 첫 설치 후 대시보드에서 `관리자 토큰이 올바르지 않습니다.`가 표시됐습니다.
서버에 설치된 `appsettings.json`의 `LabManager:AdminToken`으로 `/api/admin/token`을 호출하면
`200`이 반환되고, `LabManager:AgentToken`으로 호출하면 `401`이 반환됐습니다.
즉 설치·토큰 반영은 정상이고 대시보드에 **에이전트 토큰**을 입력한 것이 원인입니다.
내장 토큰 파일은 1행이 AdminToken, 2행이 AgentToken이며 서버에도 같은 순서로 반영됩니다.
코드 변경 없이 `LabManager:AdminToken` 값을 입력하면 해결됩니다.

### 2. Agent 설치 시 LabManagerUrl 누락

학생 PC 설치 exe를 아무 인자 없이 실행하면 아래 오류로 중단됐습니다.

```text
LabManagerUrl이 없습니다. agent-install.ini 또는 /LabManagerUrl=http://labmanager:5080 을 지정하세요.
```

 v0.1.30부터 **첫 설치**에서 설정 파일·명령줄이 모두 없으면 `http://labmanager:5080`을 기본값으로
사용합니다. 외부 INI/명령줄 값은 항상 우선하고, BOM이 깨진 INI가 있으면 기본값으로 덮지 않고
기존 오류를 유지합니다. 업그레이드는 여전히 기존 `agent.json`을 유지합니다.
`docs/INSTALLER.md`, `docs/PILOT.md`에 기본값과 우선순위를 반영했습니다.

### 검증 및 산출물

- 로컬 Inno 컴파일 성공, 테스트 183개 통과, `.iss` UTF-8 BOM 유지.
- CI run `45164346126` 성공, 첫 설치 토큰 내장 메시지와 `containsSecrets` 검증 확인.

| 파일 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.30-x64.exe` | `41f3192fa4d4e00ffde9653c37ea41f0be5f9b2228f57274a5a8671393aa8bda` |
| `UpdateAgent.LabManager-Setup-0.1.30-x64.exe` | `19c6cec3aa84fb7f978d3d79b20243e6fd3d97326dd09c7d72eaca91cf14ecf9` |
| `UpdateAgent.Agent-0.1.30-x64.msi` | `7bc4b38f33cf14d0d7bfb49bad427dddb7d5fe3bda13da771acbe90637d25d0e` |

- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.30
- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45164346126

---

## 2026-11-23 — LabManager 설치 관리자 초기화 오류 수정 (v0.1.29)

### 증상

v0.1.28 LabManager 설치 파일을 관리자 권한으로 실행해도 시작 즉시 종료됩니다.

```text
Runtime error:
내부 오류: An attempt was made to expand the "app" constant before it was initialized.
```

### 원인과 조치

Inno Setup의 `InitializeSetup` 이벤트에서 `{app}`을 확장했습니다. 이 이벤트는 설치 경로가 정해지기 전이라 `{app}`이 초기화되지 않습니다.

| 항목 | 변경 |
|---|---|
| `InitializeSetup` | `OldInnoUninstall` 조회만 수행, `FirstInstall := True`로 둠 |
| `PrepareToInstall` | `{app}` 사용 가능 시점에 기존 `appsettings.json` 유무로 `FirstInstall` 재판정 |
| Pascal 주석 | 주석 안의 `{app}` 표기는 중괄호 주석을 닫으므로 `app 상수`로 변경 |

Agent 설치 관리자는 `{commonappdata}`를 사용하므로 같은 문제가 없습니다.

### 검증

- 로컬 Inno 컴파일 성공.
- CI run `45159320812` 전체 성공, 첫 설치 토큰 내장 메시지 확인.
- v0.1.28 설치 파일은 이 오류가 있으므로 사용하지 않습니다.

| 파일 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.29-x64.exe` | `c747b85a312b0ece77935a83141b0f2cd7d6ca55c8eea93606af0cbdacc7ee66` |
| `UpdateAgent.LabManager-Setup-0.1.29-x64.exe` | `8966e523f7a56c00bd3f85d6455af58ada7fc537412e977104802a1c22b0e049` |
| `UpdateAgent.Agent-0.1.29-x64.msi` | `475191a6be4d8645384d57f1d7bb6dca767cd654b9bc3439b7b6604d949c2f5e` |

- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.29
- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45159320812

---

## 2026-11-23 — 파일럿 토큰 secret 적용 릴리스 (v0.1.28)

### 배경

v0.1.27까지 공개 자산에는 실제 토큰이 내장되지 않아 첫 설치에서 외부 토큰 파일이 필요했습니다.
저장소 Actions secret 3개를 등록해 설치 exe에 **첫 설치 전용 토큰**을 내장했습니다.

| Secret | 설정 값 |
|---|---|
| `UPDATEAGENT_AGENT_TOKEN` | 학생 PC용 에이전트 토큰 |
| `UPDATEAGENT_LABMANAGER_AGENT_TOKEN` | 위와 동일한 값 |
| `UPDATEAGENT_LABMANAGER_ADMIN_TOKEN` | 교사용 관리자 토큰(별도 값) |

`gh secret set`으로 등록하고 `gh secret list`로 이름/갱신 시각을 확인했습니다.
빌드 로그에 `Embedded first-install ...`가 표시되고 `containsSecrets` 검증을 통과했습니다.

### CI 실패와 수정

| Run | 결과 | 원인/조치 |
|---|---|---|
| `45151411468` | 실패 | `.iss` 2개의 fallback `AppVersion`이 `0.1.27`로 남아 있음 → `0.1.28`로 수정(UTF-8 BOM 유지) |
| `45152050353` | 실패 | WiX `.wixproj` fallback `ProductVersion`이 `0.1.27`로 남아 있음 → `0.1.28`로 수정 |
| `45154021676` | 성공 | 로컬 Inno/MSI 사전 빌드 통과 후 전체 단계 성공 |

두 실패 모두 릴리스 자산 업로드 전에 발생해 v0.1.28 자산은 생성되지 않았습니다.
따라서 `v0.1.28` 태그를 수정 커밋 `d2302a2`로 이동(force push)한 뒤 워크플로를 다시 실행했습니다.

### 산출물

| 파일 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.28-x64.exe` | `bd8f93e4fb1fe584f735ae0c72a38aa683965a9599be5beeb7a231f195bdec1b` |
| `UpdateAgent.LabManager-Setup-0.1.28-x64.exe` | `b09694009ed6ceba0c2a62a5d13722fd622ae09fefc659854fd04fff2c291d5d` |
| `UpdateAgent.Agent-0.1.28-x64.msi` | `ee67483b80cceb8d9d9579e2d669ebbc405626a107008deec598af56e0f7e517` |

다운로드한 자산의 해시가 릴리스 노트와 일치함을 확인했습니다.

- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.28
- 성공 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45154021676

### 후속 과제

릴리스 스크립트가 `Directory.Build.props`만 갱신하므로 `.iss`/`.wixproj`의 fallback 버전은 수동으로 맞춰야 합니다.
`docs/RELEASE.md`에 해당 절차를 명시했습니다.

---

## 2026-11-22 — 자아성찰 10회 반복 심층 감사 및 결함 수정 (v0.1.27)

### 배경

v0.1.24~v0.1.26 배포·검증 후 설치 관리자, 배포 스크립트/CI, 에이전트 런타임, LabManager 런타임,
문서/UI의 다섯 영역을 순회하는 심층 감사를 2회 반복해 총 10회를 완료했습니다.
감사에서 확인된 치명(P0)/개선(P1) 결함을 v0.1.27에 일괄 반영했습니다.

| 회차 | 영역 | 주요 확인 내용 |
|---|---|---|
| 1 | 설치 관리자 | Inno Agent — 첫 설치 판단, 토큰 보존, 서비스/방화벽 순서 |
| 2 | 스크립트/CI | 배포·MSI·게시 스크립트, 토큰 secret 전달, 태그 재사용 가드 |
| 3 | 에이전트 런타임 | 서비스 이름, 정책 캐시, 기능 업데이트 승인, 수동 일시정지 |
| 4 | LabManager 런타임 | 명령 결과 소유자, 정책 발급, 체크인 동시성, 토큰 기록 |
| 5 | 문서/UI | 릴리스 노트 구성, 토큰 안내 모순, 방화벽/OS 그룹 설명 |
| 6 | 설치 관리자 재감사 | LabManager fail-closed, 업그레이드 샘플 토큰 치환 제거 |
| 7 | 스크립트/CI 재감사 | BOM/트림 순서, `sc.exe` 반환값, `containsSecrets` 메타데이터 |
| 8 | 에이전트 런타임 재감사 | 오버레이 정책 게이팅, WUA 결과 은폐, 만료 명령 재기록 |
| 9 | LabManager 런타임 재감사 | 로그온 사용자 이름 제거, 온라인 임계값 설정 반영, 고스트 PC 정리 |
| 10 | 문서/UI 재감사 | 비밀번호 필드 제거, 샘플 토큰 자동입력 제거, API 목록 보완 |

### 주요 변경

| 영역 | 변경 |
|---|---|
| 서비스/명령줄 | 서비스 이름을 `UpdateAgentService`로 통일, 명령줄을 구성 공급자에서 제거해 토큰 노출 차단 |
| 기능 업데이트 | 2차 승인 릴리스가 없으면 선택하지 않도록 fail-closed, 서버가 발급하는 정책에서 강제 제거 |
| 명령 보안 | 결과 제출 시 `AgentId` 소유자 검증, 실패 작업 정리도 소유자 확인 |
| 정책 | 교사 정책을 체크인에서 로컬 캐시에 저장, 읽기 실패 시 마지막 정상 정책 유지 |
| 체크인 | PC별 세마포어로 상태 갱신 직렬화, `LoggedOnUsers` 수집/저장 제거 |
| 토큰 | `IsPlaceholderToken`이 모든 `change-me-*`/`sample-*-replace-before-pilot` 거부, LabManager 첫 설치 fail-closed, UI 샘플 자동입력/비밀번호 필드 제거 |
| 설치 관리자 | 설정 파일 기반 첫 설치 판단, `SessionHelperPath` 갱신, 서비스 RUNNING 확인, 방화벽 전에 상태 검증 |
| 스크립트/CI | `-AllowSameVersion` 스테일 태그 가드, MSI 버전 우선 선택, `containsSecrets` 정확 기록, 빌드 정보 버전 검증 |
| 기타 | 수동 일시정지 재시작 유지, WUA 항목 소실을 `Skipped`로 표시, 고스트 PC 삭제 API 추가, `OnlineThresholdSeconds` 반영 |

### 회귀 테스트

`tests/UpdateAgent.Tests/AuditFixTests.cs`를 추가해 토큰 검증, 기능 업데이트 2차 승인,
정책 페일클루즈, 명령 결과 소유자, 로그온 사용자 이름 제거, 온라인 임계값, PC 삭제를 검증합니다.

### 검증

- `dotnet build UpdateAgent.sln -c Release`: 성공(경고 0, 오류 0)
- `dotnet test ... -c Release --no-build`: 성공(183개 통과, 실패 0)
- `scripts\publish.ps1`, `scripts\build-inno.ps1`, `scripts\build-msi.ps1`: 모두 성공(로컬 테스트 토큰 사용)
- PowerShell 스크립트 구문 검사: 전체 통과, `git diff --check`: 오류 없음
- BOM 스캔: `agent-install.sample.ini` 및 스크립트는 UTF-8 BOM 없음, 시크릿 스캔: 통과
- **Inno Setup 스크립트(.iss)는 UTF-8 BOM이 필수**입니다. BOM을 제거하면 `build-inno.ps1`의
  `Assert-InnoScriptBom`이 중단됩니다(첫 CI 실행 실패 원인, BOM 복구 후 재실행 성공).

### GitHub 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45024570303 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.27
- 공개 자산은 토큰 secret이 설정되지 않은 빌드이므로 `containsSecrets=false`이며,
  첫 설치에는 실제 토큰이 내장된 빌드(또는 에이전트는 외부 토큰 파일)가 필요합니다. 업데이트는 가능합니다.
- 다운로드한 자산의 SHA-256이 릴리스 노트의 값과 일치함을 확인했습니다.

| 자산 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.27-x64.exe` | `ba2ad5a1a6d5cc73d03bbb689e5b0c935e56daaca80578f4aa5f8ae1dc881011` |
| `UpdateAgent.LabManager-Setup-0.1.27-x64.exe` | `4833a3a2d48821b969b5f7c7ec2ccc3176f2ea9014c8060b3c23ca1e23433b91` |
| `UpdateAgent.Agent-0.1.27-x64.msi` | `37c0bb1f95f9accd4cf1889c70c9d84b6d7fe2e685638c7ee5242c73b086cc6b` |

---

## 2026-11-22 — 토큰 secret이 없는 빌드의 첫 설치 실패 수정 (v0.1.26)

### 배경

- v0.1.25에서는 GitHub 토큰 secret이 설정되지 않으면 내장 토큰 파일이 빈 파일로 컴파일되었고,
  LabManager 첫 설치가 "두 줄이 없습니다" 오류로 중단되었습니다. 공개 릴리스 자산에도 토큰이 없어
  교사 PC 설치가 불가능한 회귀(defect)였습니다.

### 변경

| 파일 | 변경 |
|---|---|
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | 빈 내장 토큰 파일을 "내장 토큰 없음"으로 처리해 기존 `appsettings.json` 샘플 토큰 동작 유지. 한 줄만 있거나 한쪽 값만 비면 계속 오류 |
| `installer/InnoSetup/UpdateAgent.Agent.iss` | 빈 내장 토큰 오류 메시지에 secret 설정 또는 `AgentTokenFile` 지정 안내 추가 |
| `Directory.Build.props`, `.iss`, `.wixproj`, 문서 예제 | 버전 0.1.26으로 갱신 |

### 검증

- Release 빌드 경고 0/오류 0, 전체 테스트 165/165 통과, Inno/MSI 빌드 성공.
- 토큰 secret이 없는 상태(내장 파일 빈 값)에서 LabManager 첫 설치가 중단되지 않는 경로를 코드로 보장.

### 릴리스 검증

- 릴리스 워크플로 [`45018448721`](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45018448721) 성공.
- release [`v0.1.26`](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.26) 생성 및 자산 3개 다운로드·해시 검증 완료:
  - `UpdateAgent.Agent-Setup-0.1.26-x64.exe` — `66b3cad0ef8e2effedd15c304c17c8d66346f8f56f73c5bfb6b33aba90f78324`
  - `UpdateAgent.LabManager-Setup-0.1.26-x64.exe` — `399b8357290a4f27432860bc025f7f897ed5551cffd61e354bd0c7bf8a63683d`
  - `UpdateAgent.Agent-0.1.26-x64.msi` — `27db0ee0dc687daae9f7560016ead6bcb54c660074fa5c90eaa5bd213ac14637`

---

## 2026-11-22 — 첫 설치 전용 내장 토큰 지원 (v0.1.25)

### 배경

- 첫 설치 때 관리자가 별도 토큰 파일을 준비하지 않아도 되도록, 지정한 토큰을 에이전트/관리자 설치 exe에 미리 넣어 둡니다.
- 업데이트에서는 내장 토큰을 절대 사용하지 않고 기존 설정을 유지해야 합니다.

### 변경

| 파일 | 변경 |
|---|---|
| `scripts/build-inno.ps1`, `.github/workflows/release.yml` | `UPDATEAGENT_AGENT_TOKEN`, `UPDATEAGENT_LABMANAGER_ADMIN_TOKEN`, `UPDATEAGENT_LABMANAGER_AGENT_TOKEN` secret을 스테이징 토큰 파일로 주입. 값이 없으면 빈 파일을 넣어 빌드 결과가 일정하게 유지되고 설치 시 "내장 토큰 없음"으로 처리. 플레이스홀더/한쪽만 설정된 경우 빌드 중단 |
| `installer/InnoSetup/UpdateAgent.Agent.iss` | `agent-token.txt`를 `dontcopy`로 내장. 이전 설치가 감지되면 내장 토큰 무시, 명시적 외부 토큰 파일은 업데이트에서도 우선 사용 |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | `labmanager-token.txt`(1행 AdminToken, 2행 AgentToken)를 `dontcopy`로 내장. 첫 설치에서만 `appsettings.json`에 반영하고, 이전 설치가 감지되면 기존 설정 유지 |
| `scripts/deploy-agents.ps1` | `-UseEmbeddedToken` 추가. 토큰 파일 없이 주소만 있는 INI와 래퍼를 생성해 내장 토큰 방식으로 배포 가능 |
| `README.md`, `docs/{INSTALLER,DEPLOYMENT,PILOT,RELEASE}.md`, `scripts/agent-install.sample.ini`, 릴리스 노트 | 내장 토큰 동작 표, secret 설정, 보안 주의 사항 반영 |
| `Directory.Build.props`, `.iss`, `.wixproj`, 문서 예제 | 버전 0.1.25로 갱신 |

### 검증

- Release 빌드 경고 0/오류 0, 전체 테스트 165/165 통과.
- 두 Inno 스크립트 BOM 유지, Inno Setup 6 컴파일 성공, MSI 빌드 성공.
- 토큰 주입 빌드 로그에서 `agent-token.txt`/`labmanager-token.txt` 컴파일 확인, 플레이스홀더 토큰 주입 시 빌드 중단 확인.
- `deploy-agents.ps1`의 외부 토큰/내장 토큰 두 모드 패키지 생성 확인.

### 릴리스 검증

- 릴리스 워크플로 [`45016968809`](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45016968809) 성공.
- release [`v0.1.25`](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.25) 생성 및 자산 3개 다운로드·해시 검증 완료:
  - `UpdateAgent.Agent-Setup-0.1.25-x64.exe` — `ac956e321a1b554e9f456a53d23c7c50e2d49663d6a0b962f9d814f3a4fe6086`
  - `UpdateAgent.LabManager-Setup-0.1.25-x64.exe` — `8eea1642607b2f36edb5479a8904d30bebbceb693c227cef81db875ccca81212`
  - `UpdateAgent.Agent-0.1.25-x64.msi` — `4c332aaac4e08cbcf18f1dce96dcf0658c65d5c69599aa26520917dded2c2b51`

---

## 2026-11-22 — 자아성찰 심층 감사와 설치/배포 오류 수정 (v0.1.24)

### 배경

- v0.1.23의 `agent-install.ini` 자동 읽기를 독립 감사 2회(인스톨러 로직, 배포 스크립트·문서 정합성)로 재검토했습니다.
- 설정 경로·업그레이드 판정·토큰 ACL·방화벽 규칙·문서 버전 등 실제 배포를 막을 수 있는 결함을 한 번의 패치로 수정했습니다.

### 변경

| 파일 | 변경 |
|---|---|
| `installer/InnoSetup/UpdateAgent.Agent.iss` | INI 경로를 `{src}` 기준으로 수정, 키별 우선순위/ 인라인 주석/BOM 오류 처리, URL·샘플 토큰 검증, 기존 agent.json 병합, 토큰 기록 전 ACL 제한, 검증을 제거 전으로 이동, PowerShell 기반 그룹 방화벽 규칙 등록 |
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | `netsh`가 지원하지 않는 `group=` 제거 후 PowerShell `New-NetFirewallRule`로 TCP 5080 Domain/Private 규칙 등록, 실패 시 중단 |
| `scripts/deploy-agents.ps1` | 토큰 첫 줄 검증·플레이스홀더 패턴 확대, 생성 토큰 ACL 실패 시 중단, 설치 래퍼의 ACL/삭제 실패 확인과 `/Upgrade` 옵션, 패키지 폴더 이름 충돌 검사, 버전순 인스톨러 선택 |
| `scripts/build-inno.ps1`, `scripts/build-msi.ps1` | `.iss`/`.wixproj` 폴백 버전이 props와 다르면 빌드 중단 |
| `scripts/{configure-agent,configure-labmanager,install-service,install-agent-msi}.ps1` | 토큰 값 명령줄 매개변수 제거, 파일/환경 변수 경로만 허용 |
| `README.md`, `docs/{INSTALLER,DEPLOYMENT,PILOT,MSI,RELEASE}.md`, `scripts/publish.ps1`, `.github/workflows/release.yml` | 같은 폴더 INI 권장 안내, 실패 모드·업그레이드 방법, 최신 버전 예제(0.1.24) 반영 |

### 검증

- Release 빌드 경고 0/오류 0, 전체 테스트 165/165 통과.
- 두 Inno 스크립트 모두 UTF-8 BOM(`EF BB BF`) 유지, Inno Setup 6 컴파일 성공.
- PowerShell 스크립트 구문 검사, `deploy-agents.ps1` 패키지 생성/토큰 ACL/충돌·플레이스홀더 거부 확인.
- 비밀값·개인정보 검사와 `-Bump patch -DryRun` 통과.

### 릴리스 검증

- 릴리스 워크플로 [`45012380690`](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45012380690) 성공.
- release [`v0.1.24`](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.24) 생성 및 자산 3개 다운로드·해시 검증 완료:
  - `UpdateAgent.Agent-Setup-0.1.24-x64.exe` — `005fd6cc65ea88c42a47b1118b279bb5c846cf314314968a742d3ac5d686cf13`
  - `UpdateAgent.LabManager-Setup-0.1.24-x64.exe` — `b70c918e5c00655af9c66a1f5d88e67621e0968ed76b778ebcfcf5f5a33eb6f0`
  - `UpdateAgent.Agent-0.1.24-x64.msi` — `db925c4adb6439883e13b56fdc2f24ce11e749974beb926308089083cdcda5a7`

---

## 2026-11-22 — 설치 파일과 같은 폴더의 설정 파일로 학생 에이전트 설치 (v0.1.23)

### 배경

- 학생 PC 50대에 배포할 때마다 `/LabManagerUrl`, `/AgentTokenFile` 명령줄 인자를 반복해서 관리해야 했습니다.
- 설치 exe와 같은 폴더에 설정 파일을 두고 인스톨러가 자동으로 읽도록 해 배포 패키지를 단순화했습니다.

### 변경

| 파일 | 변경 |
|---|---|
| `installer/InnoSetup/UpdateAgent.Agent.iss` | 설치 exe와 같은 폴더의 `agent-install.ini`(`[Agent]` 섹션) 자동 읽기. 상대 토큰 경로는 설치 폴더 기준, 명령줄 값 우선, 토큰 파일이 삭제된 업그레이드는 기존 `agent.json` 유지 |
| `scripts/deploy-agents.ps1` | PC별 패키지에 `agent-install.ini` 생성, `install-agent.cmd`는 명령줄 인자 없이 조용히 설치 |
| `docs/INSTALLER.md`, `docs/DEPLOYMENT.md` | 설정 파일 형식·우선순위·상대 경로 규칙과 패키지 구성 갱신 |

### 검증

- Inno Setup 6으로 에이전트 설치 프로그램 컴파일 성공.
- `deploy-agents.ps1`로 생성한 패키지에 `agent-install.ini`가 들어가고 설치 명령에 설정 인자가 없음을 확인.
- 설정 파일에는 토큰 값이 없이 파일 경로만 기록되며, 토큰 값은 별도 파일 첫 줄에서만 읽습니다.
- 릴리스 워크플로 `45007862553` 성공, release `v0.1.23` 생성 및 자산 3개 다운로드·해시 검증 완료:
  - `UpdateAgent.Agent-Setup-0.1.23-x64.exe` — `0146786e5f037e9fde6855210439c747a12b00743a4fcc9dfac7563ef2c51aa1`
  - `UpdateAgent.LabManager-Setup-0.1.23-x64.exe` — `1556ab22f4c7e7f5de38c2d72f4af640d28be88e5db3b39cef77d8e8e85a828a`
  - `UpdateAgent.Agent-0.1.23-x64.msi` — `c492ea0fa6bde2a9876e079533684c39122edd690b267fb373745458725ed3aa`

---

## 2026-11-22 — 자아성찰 3회 심층 감사와 오류 수정 (v0.1.22)

### 배경

- `v0.1.21`까지 배포된 학생용 수동 업데이트 옵션과 로컬 제어 파이프를 대상으로 자아성찰 3회를 수행했습니다.
- 회귀, 파이프 수명 주기, 요구사항 준수, 문서·버전 정합성 관점의 독립 감사를 통해 실제 사용 흐름을 망가뜨릴 수 있는 결함을 찾아 한 번의 패치로 수정했습니다.

### 발견 및 수정

| 라운드 | 심각도 | 발견 | 수정 |
|---|---|---|---|
| 1 | 높음 | 같은 버튼을 연속 클릭하면 옵션 창이 중복 표시되고 시작 요청이 겹침 | `_startRequestInFlight` 재진입 잠금을 옵션 창 표시 전에 설정 |
| 1 | 높음 | 교사 정책으로 모든 범위가 꺼져 있어도 파이프 요청으로 우회해 작업 시작 | `UpdatePolicy.HasAnyUpdateCategory`를 서비스 `LocalUpdateTrigger`에서 강제 검사 |
| 1 | 중간 | 클라이언트 시간 초과(95초)와 서버 핸들러 제한(90초) 메시지가 달라 원인이 불명확 | 공용 `ResponseTimeoutMessage` 사용 |
| 1 | 중간 | 상태 새로고침이 겹치면 오래된 응답이 최신 화면을 덮어씀 | 세대(generation) 기반 마지막 요청만 반영 |
| 1 | 낮음 | 프리셋 선택 후 이전 검증 오류가 남고, 성공 메시지가 실제 동작과 달랐음 | 안내 초기화 및 문구 정정, `AgentStatusReader`의 `agentId` 폴백 보완 |
| 2 | 높음 | 창을 닫을 때 최대 95초간 응답 없이 대기하고 취소 경로가 없음 | `OnClosing`에서 `_startCts` 취소, `_closing` 동안 결과 창/갱신 생략 |
| 2 | 높음 | 강제 새로고침이 기존 중복 제거를 무력화 | 세대 기반 마지막 기록자 우선으로 재설계 |
| 2 | 중간 | 서버가 취소를 존중해 파이프를 닫으면 시간 초과 메시지가 전달되지 않음 | 핸들러 시간 초과 시 서버가 시간 초과 응답을 먼저 전송 |
| 2 | 중간 | 응답 쓰기가 취소되면 `RequestSucceeded`가 누락돼 즉시 체크인이 발생하지 않음 | 이벤트를 응답 쓰기 전으로 이동 |
| 3 | 높음 | 시작 요청이 사전 점검/저장 중일 때 들어온 중단 요청의 플래그가 `StartRunLoop`에서 초기화돼 작업이 계속 실행 | 취소 플래그 설정과 `_runCts.Cancel()`을 시작과 같은 `_control` 임계구역으로 이동 |
| 3 | 중간 | 실제 시작 경로가 `OperationCanceledException`을 일반 실패로 바꿔 시간 초과 메시지가 도달하지 않음 | 핸들러 토큰 취소 시 rethrow해 파이프 시간 초과 응답 사용 |
| 3 | 중간 | 취소 직후 진행률이 늦게 도착해 숨겨진 오버레이가 다시 표시됨 | `HideAsync`가 작업 ID를 받아 `_gate` 하에서 억제 플래그 설정 |
| 3 | 중간 | 이미 취소된 토큰을 `Task.Run`에 넘기면 연결 핸들이 누수 | 취소 판단을 핸들러의 linked token으로 이관 |
| 3 | 낮음 | 체크인 즉시 요청 신호가 좁은 경쟁 구간에서 손실되고 CTS가 휘발성이 아님 | `Interlocked`/`Volatile` 사용 |
| 3 | 낮음 | 긴급 중단도 시작과 같은 95초를 대기하고, 새로고침 실패 시 시작 버튼이 잠김 | 중단 전용 10초 시간 초과, 실패 시 버튼 복원 |
| 문서 | 중간 | 소스 버전은 `0.1.21`인데 설치 스크립트/문서 예제가 `0.1.22`로 앞서고 잘못된 패치 예시가 남음 | `Directory.Build.props`를 단일 소스로 유지하며 예제·테스트 수(165개) 정리 |

### 검증

- Release 빌드 경고 0/오류 0, 테스트 **165개 통과**.
- `node --check` 2개 통과, `.iss` BOM `EF BB BF` 유지, 시크릿 스캔 정상.
- 회귀 테스트: 파이프 시간 초과 응답/순서 불변식, 성공 시에만 체크인 통지, 빈 정책 거부, 핸들러 취소 rethrow, 오버레이 숨김 시 작업 ID 전달 추가.

### 릴리스 검증

- 릴리스 커밋: `9fe27cd`
- GitHub Actions: [workflow 45004629842](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45004629842) 성공
- GitHub Releases: [v0.1.22](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.22)
- 공개 자산을 다시 내려받아 SHA-256을 확인했습니다.

| 자산 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.22-x64.exe` | `c0f8d1460b4e39ebe62b1227c981d01376e2ac163f743d483f388c3ce23eddb9` |
| `UpdateAgent.LabManager-Setup-0.1.22-x64.exe` | `f594764f13d9bc5a16b28488aefecf15d97a06097e620e5a833a5906a5d0c2cc` |
| `UpdateAgent.Agent-0.1.22-x64.msi` | `c0dfd1efa9031e2e43b284b2836d56851f08e5cfbff2f4a69de29228fa4bac11` |

---

## 2026-11-22 — 학생용 앱 메인 화면에 수동 업데이트 옵션 항목 추가 (v0.1.21)

### 배경

- `v0.1.20`까지 수동 업데이트 옵션은 **업데이트 시작** 버튼을 눌러야 열리는 창이었고, 메인 화면에 별도 항목이 없었습니다.
- 학생 PC에서 옵션 항목을 바로 찾을 수 있도록 메인 화면에 전용 버튼을 추가했습니다.

### 변경

| 파일 | 변경 |
|---|---|
| `src/UpdateAgent.Agent.Desktop/MainWindow.xaml` | **수동 업데이트 옵션** 버튼 추가 |
| `src/UpdateAgent.Agent.Desktop/MainWindow.xaml.cs` | 새 버튼과 기존 **업데이트 시작** 버튼이 같은 옵션 창을 열도록 연결. 작업 진행 중에는 두 버튼 모두 비활성화 |
| `README.md`, `docs/PILOT.md`, `docs/DEPLOYMENT.md` | 두 진입점과 정책 상한 규칙 반영 |

### 검증

- Release 빌드 경고 0/오류 0, 테스트 **157개 통과**, 시크릿 스캔 정상.
- 릴리스 커밋: `352b939`
- GitHub Actions: [workflow 45000721362](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/45000721362) 성공
- GitHub Releases: [v0.1.21](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.21)
- 공개 자산을 다시 내려받아 SHA-256을 확인했습니다.

| 자산 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.21-x64.exe` | `87d10b76d9fa4d2ad7220b8ca856ec7ed24576271689074216c36f12d2e99ac5` |
| `UpdateAgent.LabManager-Setup-0.1.21-x64.exe` | `25c4d711ebd0cdef0272ae97ea7770806d52617b3e7008b5e4b33833b0757062` |
| `UpdateAgent.Agent-0.1.21-x64.msi` | `7db988c4e20e1147b4df01ff6a8fca57242df3574703cb337c29625fbccc061d` |

---

## 2026-11-22 — 자아성찰과 심층 감사: 옵션 창/파이프 오류 수정 (v0.1.20)

### 배경

- `v0.1.19`로 학생용 수동 업데이트 옵션을 배포한 뒤, 구현 흐름과 문서·릴리스 일관성에 대해 두 차례의 독립 감사를 수행했습니다.
- 사용자 흐름을 망가뜨리는 결함, 정책 상한 누락, 방어적 오류 처리 누락, 문서 정합성 문제를 확인했습니다.

### 발견 및 수정

| 심각도 | 발견 | 수정 |
|---|---|---|
| 높음 | 프리셋을 선택하면 체크박스 값 변경 이벤트가 다시 `DetailChanged`를 호출해 선택이 즉시 “사용자 설정”으로 되돌아감 | `ManualUpdateOptionsWindow.PresetCombo_SelectionChanged`에서 값 적용 동안 `_applyingPreset` 잠금 |
| 높음 | 학생 옵션이 캐시된 교사 정책을 그대로 덮어써 교사의 `RebootPolicy.Never`·항목 제외·오버레이 비활성화를 다시 켤 수 있음 | `ManualUpdateOptions.LimitTo`/`ApplyTo`에 교사 정책 상한 적용. 학생은 줄이기만 가능 |
| 중간 | 업데이트 범위를 모두 해제한 뒤에도 작업을 시작할 수 있고, UI 검증만 있어 파이프 요청으로 우회 가능 | `HasAnyUpdateCategory`를 서비스 `LocalUpdateTrigger`에서도 검사 |
| 중간 | 옵션이 있는 요청에서 `MaestroConfirmed=false` 차단 경로에 회귀 테스트가 없음 | `LocalUpdateTrigger_RequiresMaestroConfirmationEvenWithOptions` 추가 |
| 중간 | `LocalUpdateTrigger`의 정책/OS 해석이 `try` 밖에 있어 예외 시 파이프 응답 없이 연결이 끊김 | 정책 해석 전체를 `try/catch`로 감싸 실패 응답 반환 |
| 낮음 | 중단할 작업이 없어도 오버레이를 숨기며 “중지했습니다” 안내를 보냄 | `summary is not null`일 때만 오버레이 숨김 |
| 낮음 | 옵션을 수정해도 이전 검증 오류 메시지가 남고, 시작 버튼이 최신 상태 갱신 전에 활성화됨 | `DetailChanged`에서 안내 초기화, `RefreshAsync`가 시작 버튼 상태를 결정 |
| 낮음 | `build-inno.ps1`이 `iscc /?`의 stderr를 버려 `build-info.json.innoVersion`이 비어질 수 있음 | `2>$null`을 `2>&1`로 변경 |
| 문서 | 현재 테스트 수(89개), 설치 예제의 `0.1.10`, RELEASE.md의 잘못된 패치 예시와 자산 2개 체크리스트가 최신 상태와 불일치 | 157개/`0.1.20`/3개 자산으로 갱신, `<버전>` placeholders 정리. 패치 예시는 v0.1.22 감사에서 `0.1.21 -> 0.1.22`로 최종 정정 |

### 검증

- Release 빌드 경고 0/오류 0, 테스트 **157개 통과**.
- `node --check` 2개 통과, 시크릿 스캔 정상, `.iss` BOM `EF BB BF` 유지.
- 회귀 테스트: 범위 검증, 교사 정책 상한, 옵션 있는 Maestro 차단, 정책 해석 실패, 빈 작업 중단 시 오버레이 유지 추가.
- `README.md`, `docs/PILOT.md`, `docs/DEPLOYMENT.md`, `docs/RELEASE.md`, `docs/MSI.md`, `docs/INSTALLER.md`와
  WiX/Inno/MSI 빌드 스크립트의 기본 버전 예시를 정리했습니다.

### 검토 후 유지

- `v0.1.18`의 남은 레지스트리 항목은 새 설치가 동일 AppId로 덮어써 정리되므로 이번 패치에서 추가 삭제하지 않습니다.
- `WORKLOG.md`의 오래된 릴리스 해시(prefix만 기록된 항목)는 과거 기록이며 기능 영향이 없어 변경하지 않습니다.

### 릴리스 검증

- 릴리스 커밋: `2094fa3`
- GitHub Actions: [workflow 44998615980](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44998615980) 성공
- GitHub Releases: [v0.1.20](https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.20)
- 공개 자산을 다시 내려받아 SHA-256을 확인했습니다.

| 자산 | SHA-256 |
|---|---|
| `UpdateAgent.Agent-Setup-0.1.20-x64.exe` | `710f7d62e86a55fc7862e906557b1b020cfa713527553ac0984677930f1087ed` |
| `UpdateAgent.LabManager-Setup-0.1.20-x64.exe` | `f178a61127de7909bb16024f1070c81181f2cdc7a7e223ea16663d735fa52923` |
| `UpdateAgent.Agent-0.1.20-x64.msi` | `cdb388189aab91224ac704c61fe7ad86dea6f8315a3c3cb955392efe8657a002` |

---

## 2026-11-22 — 학생용 앱에서 수동 업데이트 옵션 선택 (v0.1.19)

### 배경

- 학생 PC의 `UpdateAgent 상태 확인` 앱은 **업데이트 시작** 버튼만 제공하고, 누르면 곧바로 서비스에 요청을 보냈습니다.
- 학생이 해당 PC에서 설치할 업데이트 범위나 재부팅/오버레이 동작을 고를 수 없어 교사 대시보드를 열어야 했습니다.

### 변경

| 파일 | 변경 |
|---|---|
| `src/UpdateAgent.Shared/Models/ManualUpdateOptions.cs` | 학생이 고르는 프리셋/세부 옵션 모델 추가. 프리셋 변환, OS 안전 처리, 교사 정책 병합 로직 포함 |
| `src/UpdateAgent.Shared/Models/LocalControlModels.cs` | `start-update` 요청에 `options` 필드 추가 (없으면 기존 정책 사용) |
| `src/UpdateAgent.Service/Comms/LocalUpdateTrigger.cs` | 전달된 옵션을 캐시된 교사 정책에 병합. 기능 업데이트·Maestro 요구·로그온 일시중지 등 보호 항목은 유지 |
| `src/UpdateAgent.Agent.Desktop/ManualUpdateOptionsWindow.xaml(.cs)` | 프리셋 + 세부 옵션 선택 창 추가 |
| `src/UpdateAgent.Agent.Desktop/MainWindow.xaml.cs`, `Services/LocalUpdateClient.cs` | 시작 버튼이 옵션 창을 열고 선택 값을 파이프로 전달 |
| `tests/UpdateAgent.Tests/LocalControlTests.cs` | 옵션 직렬화, 프리셋/OS 안전 처리, 정책 병합, 트리거 반영 테스트 추가 |
| `README.md`, `docs/PILOT.md`, `docs/INSTALLER.md` | 학생용 옵션 선택 흐름 반영 |

### 설계 메모

- 학생이 바꿀 수 있는 항목: 보안/중요/권장/선택/드라이버/정의 업데이트, 오버레이, 자동 재부팅 여부, 최대 재부팅 횟수(0~10).
- 학생이 바꿀 수 없는 항목: 기능 업데이트(2차 승인 전용), Maestro 확인 요구, `PauseOnUserLogon`, 완료 후 종료 등 교사 정책의 나머지 값.
- 확인 주체는 기존처럼 서버에서 `student`로 고정되며, 토큰은 여전히 학생 앱에 저장하지 않습니다.

### 검증

- `node --check` 2개 통과, Release 빌드 경고 0/오류 0, 테스트 **151개 통과**, 시크릿 스캔 165개 파일 정상.

### 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44996535976 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.19
- 자산 3개 확인, 릴리스 노트 해시와 API `digest` 일치:
  - `UpdateAgent.Agent-Setup-0.1.19-x64.exe`: `6f072d2140f65f73a4f318f20423ee840061a2f1374e6bb91533df22ea70814e`
  - `UpdateAgent.LabManager-Setup-0.1.19-x64.exe`: `4672e5782ec350a3ecd2b7762fcf79f4ae598eb8dd0b58807168a36b928ada9d`
  - `UpdateAgent.Agent-0.1.19-x64.msi`: `8c85fb6bae50c10312ca9b2ee2f22228fe1269e8543b1d39b0ef4090fefebf0e`

---

## 2026-11-22 — 기존 설치를 찾을 수 없어도 설치 계속 (v0.1.18)

### 배경

- 교사 PC에서 LabManager Setup을 실행하면 레지스트리에 이전 설치 정보만 남고 실제 제거 프로그램 파일이 없는 경우,
  `기존 UpdateAgent LabManager 제거 프로그램을 찾을 수 없어 설치를 중단합니다.` 오류로 설치가 멈췄습니다.
- 이미 제거된 설치와 실제 제거 실패를 같은 오류로 처리한 것이 원인입니다.

### 변경

| 파일 | 변경 |
|---|---|
| `installer/InnoSetup/UpdateAgent.LabManager.iss` | 제거 프로그램 경로가 없거나 파일이 없으면 로그를 남기고 설치 계속. 실제 언인스톨 실행/완료 실패만 중단 |
| `installer/InnoSetup/UpdateAgent.Agent.iss` | 동일하게 처리하고, MSI `1605`(이미 설치되지 않은 제품)도 경고 후 계속 |
| `scripts/install-agent-msi.ps1` | Inno 제거 프로그램이 없으면 경고 후 건너뛰고 설치 계속 |
| `scripts/build-inno.ps1` | 로컬 Windows PowerShell 5.1에서 `ISCC /?`의 stderr 출력 때문에 빌드가 중단되던 문제 수정 |
| `docs/INSTALLER.md`, `docs/MSI.md` | 새 동작 반영 |

### 검증

- Inno Setup 6로 Agent/LabManager 설치 프로그램 컴파일 성공, `.iss` BOM `EF BB BF` 유지.
- `node --check` 2개 통과, Release 빌드 경고 0/오류 0, 테스트 **146개 통과**, 시크릿 스캔 162개 파일 정상.

### 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44995229665 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.18
- 자산 3개 확인, 릴리스 노트 해시와 API `digest` 일치:
  - `UpdateAgent.Agent-Setup-0.1.18-x64.exe`: `ea8e866d33412f176f1324c3edb694ae11ff33368fbeeede9b37032eb494e07f`
  - `UpdateAgent.LabManager-Setup-0.1.18-x64.exe`: `37f201216f5053dbcc1c849ad45665757c24a4c6fab59dd6291c594ec9f07def`
  - `UpdateAgent.Agent-0.1.18-x64.msi`: `64e2c1e844000cc88d6363679c0342d08cd3031a972b752a829164221c112288`

---

## 2026-11-21 — 5회 연속 자아성찰과 오류 수정 (v0.1.17)

### 배경

- `v0.1.16` 이후 OS 구분/프리셋/PC별 옵션 기능을 요구사항과 실제 코드 흐름에 대해
  **5회 연속 자아성찰**을 수행하고, 확인된 결함을 순차적으로 수정했습니다.

### 5회 자아성찰 결과

| 회차 | 관점 | 확인 결과 |
|---|---|---|
| 1회 | 요구사항 대조 | JS·C# 프리셋이 `pauseUser: true`여서 50대 랩의 "로그온 후 교사 재개 불필요" 요구와 충돌 → 프리셋/기본값 `false`로 변경 |
| 2회 | 데이터 흐름 | 시작 창이 항상 `policy`를 전송해 저장된 PC별 정책이 재사용되지 않음 → **저장된 개별 옵션 사용** 체크박스 추가(체크 시 `policy: null` 전송) |
| 3회 | UI·OS 안전 | PC별 옵션 창이 capability 적용 후 폼을 써서 비활성 오버레이가 체크될 수 있음 → write→apply 순서로 변경. 혼합 OS 안내 문구도 "일부"로 정확화 |
| 4회 | 프리셋 일관성 | C# `SecurityOnly`에 재부팅 상한 3회 누락 → 추가하고 대시보드 기본값과 일치하는 테스트 작성 |
| 5회 | 저장 해석·배포 | 손상된 override JSON이 "PC별 설정"으로 표시됨 → 파싱 실패 시 기본 정책으로 폴백하고 목록에도 개별 옵션으로 표시하지 않음. 런북/버전 동기화 |

### 변경

| 파일 | 변경 |
|---|---|
| `UpdatePolicy.cs` | `PauseOnUserLogon` 기본값 `false` (50대 랩 기준) |
| `UpdatePolicyPresets.cs` | `SecurityOnly`에 `MaxRebootCount = 3` 추가 |
| `AgentPolicyService.cs` | `Parse` → `TryParse`(실패 시 `null`). `GetEffectiveAsync`/`GetSummariesAsync`가 손상된 설정을 개별 정책으로 세지 않음 |
| `app.js` | 저장된 정책 재사용 토글, 프리셋 `pauseUser: false`, capability 순서 수정, 혼합 OS 안내 정확화 |
| `index.html` | 시작 창에 저장된 옵션 사용 체크박스/안내 추가, 로그온 일시중지 기본 해제 |
| 테스트 | 손상된 설정 폴백 2개 + 프리셋 기본값/재부팅 상한 1개 추가(143 → 146개) |
| `DEPLOYMENT.md` | 8-A/8-C 런북에 새 동작 반영 및 변경 이력 추가 |

### 검증

- `node --check` 통과, Release 빌드 경고 0/오류 0, 테스트 **146개 통과**.

### 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44900295626 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.17
- 자산 3개 확인, 릴리스 노트 해시와 API `digest` 일치:
  - `UpdateAgent.Agent-Setup-0.1.17-x64.exe`: `414f02fa8365530c0a323e040cf71be6caac80693bf7fa08b397308abd7bffa5`
  - `UpdateAgent.LabManager-Setup-0.1.17-x64.exe`: `2cfd9d4999c325e564efdced024419e7d8f76746e2aea109b64897661430fdf0`
  - `UpdateAgent.Agent-0.1.17-x64.msi`: `1048d92b3b965f635b714fcb3aa65a557b127a5960742b29989b13680dd9392d`
- 문서 정합성 후속 수정: `PILOT.md`의 "일시중지 기본 체크" 설명을 새 기본값(해제)에 맞게 정정.

---

## 2026-11-21 — 혼합 OS 선택 시 오버레이 옵션 유지 (v0.1.16)

### 배경

- v0.1.15 릴리스 검토 중, Windows 11과 Server Core를 함께 선택하면
  Server Core 때문에 오버레이 체크박스가 **전체 비활성화**되어 Windows 11 PC의 오버레이까지 꺼지는 문제를 발견.
- 서버·에이전트는 이미 PC별 OS로 정책을 정리하므로 UI도 같은 기준을 따라야 합니다.

### 변경

| 파일 | 변경 |
|---|---|
| `app.js` | `capabilitiesFor`가 `supportsOverlayAny`(한 대라도 지원)와 `supportsOverlayAll`(모두 지원)를 분리 반환 |
| `app.js` | 오버레이 체크박스는 `supportsOverlayAny`에서 선택 가능. OS mix/프리셋 안내는 `supportsOverlayAll` 기준으로 "OS별로 자동 조정" 표시 |
| `app.js` | 대상 선택이 없을 때 Server Core 안내가 잘못 표시되지 않도록 처리 |

### 검증

- `node --check` 통과, Release 빌드 경고 0/오류 0, 테스트 **143개 통과**, 비밀정보 스캔 162개 파일 정상.

### 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44898936852 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.16
- 자산 3개 확인, 릴리스 노트 해시와 API `digest` 일치:
  - `UpdateAgent.Agent-Setup-0.1.16-x64.exe`: `b4b3b3fdf21e396e3dd8222835489716fa96aab0f4d12cf77d097b3ba53031bf`
  - `UpdateAgent.LabManager-Setup-0.1.16-x64.exe`: `4cb6b4fcf5f8bbd9a623a3fc738e10f7f441c8715b2603c4c7bc0c3cd864adb3`
  - `UpdateAgent.Agent-0.1.16-x64.msi`: `1fce3d26ca6cb14ae83a09b19805e758022bf186318e9fd1f672dae23ea6faeb`

---

## 2026-11-21 — Windows 종류 구분과 업데이트 옵션 선택 (v0.1.15)

### 배경

- 50대 랩에 Windows 11, Windows 10 22H2 ESU, Windows Server 2016~2025(Server Core 포함)가 섞여 있어
  교사가 OS 종류를 한눈에 구분하고, OS에 맞는 업데이트 옵션을 선택해야 하는 요청.
- 기존에는 `OsInfo`에 구분 정보가 있었지만 대시보드에서는 이름/빌드만 표시했고,
  작업 시작 옵션은 모든 PC에 동일한 값만 전달했다.

### 구현

| 영역 | 변경 |
|---|---|
| 공통 모델 | `OsInfo.GroupId/GroupLabel/ReleaseLabel` 추가(서버는 빌드 기준 릴리스 표시), `UpdatePolicy.SanitizeFor/SanitizationNotes`, `UpdatePolicyPresets` 추가 |
| LabManager 저장 | `settings` 키 `agent-policy:{agentId}`에 PC별 정책 저장. `LabStore.DeleteSettingAsync/GetSettingsByPrefixAsync` 추가 |
| LabManager 서비스 | `AgentPolicyService`가 PC별 정책 > 기본 정책 순서로 해석하고 OS 안전 규칙을 적용. 체크인 응답도 해당 PC의 유효 정책 반환 |
| LabManager API | `GET /api/admin/agents/policies`, `GET/PUT/DELETE /api/admin/agents/{id}/policy` 추가. 작업 시작 시 PC별 정책 해석, OS 조정 사유를 `warnings`로 반환, `saveAsAgentPolicy` 지원 |
| 대시보드 | OS 필터 칩/개수, OS 그룹 칩, **개별 옵션** 표기, PC별 옵션 모달, 작업 시작 창의 OS별 프리셋·호환성 안내 추가 |
| 에이전트 | 작업 시작/검색/ApplyPolicy 직전에도 `SanitizeFor`를 호출해 서버 전달 값과 관계없이 OS 안전 규칙 강제 |

### OS 안전 규칙

- 기능 업데이트: Windows 10 / LTSC / Server에서는 옵션과 승인 릴리스를 자동 제거.
- 오버레이: Server Core에서는 자동 비활성화.
- 기능 업데이트는 2차 승인 워크플로 전용이라 모든 프리셋에서 기본 비활성화 유지.
- PC별 정책이 있어도 조회·작성·실행 시마다 OS로 재검증하므로 지원되지 않는 옵션이 남지 않습니다.

### 검증

- 빌드 경고 0/오류 0, 테스트 **143개 통과**(기존 126개 + 신규 17개).
- 신규 테스트: OS 그룹 분류/릴리스 표기, 정책 정리, 프리셋, PC별 정책 저장·삭제·요약.
- `docs/DEPLOYMENT.md`에 **8-C. Windows 종류 구분과 업데이트 옵션 선택** 추가.

### 릴리스 검증

- 워크플로: https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44898414773 (성공)
- 릴리스: https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v0.1.15
- 자산 3개(설치 파일 2개 + MSI 1개) 확인, 릴리스 노트 해시와 API `digest` 일치:
  - `UpdateAgent.Agent-Setup-0.1.15-x64.exe`: `dc5b8fb615975232a973dc92f753db05127e7687f348b355c529ad948f9e48ac`
  - `UpdateAgent.LabManager-Setup-0.1.15-x64.exe`: `68937a2d7bb35a1a4fc7fe2314bdb212c6c871bb836d00fdeae8947b4362510d`
  - `UpdateAgent.Agent-0.1.15-x64.msi`: `16ec1211defdd6dab1114e24042a4f525ff21304e1dd1061e48f3332537d2fe3`

---

## 2026-11-21 — 개별 수동 업데이트 강제 중단 (대시보드 + 오버레이 단축키)

### 배경

- 학생이 시작한 수동 업데이트를 교사가 개별적으로 즉시 중단할 수 있는 기능 요청.
- 중단 주체는 **(1) 교사 대시보드의 개별 작업 강제 중단**과 **(2) 오버레이 긴급 중단 단축키** 두 경로로 확정.

### 구현

| 경로 | 변경 |
|---|---|
| 공통 모델 | `LocalControlActions.CancelUpdate`, `CancelJobCommandPayload { Force }` 추가 |
| 작업 러너 | `CancelAsync(bool force, CancellationToken)` 오버로드 추가. 기존 `CancelAsync(ct)`는 force=false로 유지. `MarkCancelled`로 단계/메시지 통일(`강제 중단됨`), `_forceCancelled` 체크포인트 추가 |
| LabManager | `POST /api/admin/jobs/{jobId}/force-stop` 추가. `CancelJob` 명령에 `Force=true` 본문 전송, 감사 액션 `force-stop` |
| CommandExecutor | `CancelJob`이 payload의 `Force`를 읽어 러너에 전달하고, 강제 중단 시 오버레이에 안내 메시지 전송 |
| 대시보드 | 작업 행에 **강제 중단** 버튼(빨강) 추가. 경고 확인창(재부팅 취소·남은 업데이트 미설치·설치 중 항목 완료 가능) 후 전송 |
| 로컬 제어 | `LocalUpdateTrigger`에 취소 대리자/오버레이 숨김 콜백 추가(기존 생성자 호출 호환). `cancel-update` 요청은 항상 force로 처리 |
| 오버레이 | 저수준 키보드 훅에 `Ctrl+Shift+U` 2회 감지기(`EmergencyStopDetector`, 2초 창, 자동 반복 입력 제외) 추가. 첫 입력 시 안내, 두 번째 입력 시 로컬 파이프로 중단 요청 |
| 오버레이 UI | 안내 Notice 텍스트/표시 속성과 단축키 힌트 추가. 숨김 메시지에 안내가 있으면 4초간 표시 후 닫고, 입력 잠금은 즉시 해제 |

### 안전 경계

- 강제 중단도 **진행 중인 WUA 설치 자체**는 멈추지 않습니다. 설치가 끝나는 시점에 `취소됨`으로 마무리하고
  남은 업데이트는 설치하지 않습니다(시스템 손상 방지). 예약 재부팅은 즉시 취소됩니다.
- `LocalControlPipeServer`는 기존처럼 인증 사용자에게 읽기-쓰기를 허용하므로 학생 세션에서도 단축키 요청이 동작합니다.
- `OverlayJobProgressSink`에 숨김 후 suppression 추가: 강제 중단/입력 해제 뒤 러너가 마지막 `취소됨` 진행률을
  보내도 오버레이가 다시 나타나지 않습니다(새 작업 시작 시 자동 해제).
- 학생 암호 저장/자동 로그온/`runas`/`schtasks /RP`는 여전히 구현하지 않습니다.

### 테스트·문서

- 테스트 추가: 로컬 취소 트리거(force 전달·오버레이 숨김·작업 없음), `CancelJobCommandPayload` 직렬화,
  `IsCombo` 보조키 조합, 2회 입력 감지(창 내/창 초과/리셋).
- `docs/DEPLOYMENT.md`에 **8-B. 개별 수동 업데이트 강제 중단** 추가, `docs/PILOT.md` 체크리스트·제한 사항 갱신.
- 검증: 빌드 경고 0/오류 0, 테스트 **126개 통과**(기존 113개 + 신규 13개).
- 릴리스 `v0.1.14` / workflow [#44895859811](https://github.com/ahbiyout-all/WinAutoUpdate/actions/runs/44895859811)
  성공. 공개된 설치 파일 3개 해시가 릴리스 노트와 일치함을 GitHub REST API로 확인:
  Agent Setup `43966c2b…`, LabManager Setup `74e289f2…`, MSI `2044eab1…`.

---

## 2026-11-21 — 재부팅 후 로그온 암호 대비 안내 보강

### 배경

- 학생 PC가 업데이트 중 자동 재부팅된 뒤 로그온 암호가 요구되는 경우, 작업이 중단되거나
  오버레이가 표시되지 않는 것 아닌지 확인 요청이 있었다.

### 확인 내용

| 항목 | 실제 구현 |
|---|---|
| 재부팅 후 재개 | `UpdateAgentService`(LocalSystem, 자동 시작)가 부팅 직후 `ResumeIfNeededAsync` 실행. 로그온/암호 불필요 |
| 오버레이 | `SessionWatcherService`가 5초마다 WTS 세션을 조회해 로그온된 세션에만 `SessionHelper` 실행. 로그온 화면/secure desktop에는 표시되지 않음 |
| 일시중지 정책 | `PauseOnUserLogon` 기본값 `true`. 학생이 로그온하면 작업이 `사용자 로그온으로 일시중지` 상태가 되어 교사 **재개** 필요 |

### 변경

- `docs/DEPLOYMENT.md`에 **8-A. 재부팅 후 로그온 암호가 있는 경우** 추가: 동작 순서, 상황별 표,
  배포 체크리스트(**사용자 로그온 시 일시중지 해제** 포함), 의도적으로 구현하지 않는 항목.
- 기존 "재로그온 후 작업을 재개" 문구를 "부팅 직후 로그온과 무관하게 재개, 오버레이는 로그온 후"로 정정.

- 제품 코드 변경 없음(버전 `0.1.13` 유지). 암호 저장·자동 로그온·`runas`/`schtasks /RP` 암호 전달은
  기존 결정대로 구현하지 않음.

### 검증

- `Worker.cs`, `UpdateJobRunner.ResumeIfNeededAsync`, `SessionWatcherService`, `UserSessionProbe`,
  `UpdatePolicy.PauseOnUserLogon` 소스를 직접 읽어 동작 확인.
- 문서 변경만 있었으므로 빌드/테스트는 재실행하지 않음(직전 전체 검증 결과 동일).

---

## 2026-11-21 — 50대 랩 연결 배포 스크립트·런북

### 배경

- 교사 PC 1대를 허브로 두고 학생 PC 약 50대가 교사 PC만 바라보는 폴링 토폴로지는 설계되어 있었으나,
  실제 배포에 쓸 교사 PC 구성 스크립트와 학생 PC 일괄 배포 산출물, 운영 런북이 없었다.
- WORKGROUP 환경에서는 원격 실행 수단이 환경마다 다르므로, 특정 방식에 묶이지 않는 패키지 생성 방식을 택했다.

### 변경

| 항목 | 내용 |
|---|---|
| `scripts/setup-labmanager-lab.ps1` (신규) | 교사 PC에서 토큰/수신 URL 설정, TCP 5080 방화벽, 로그온 시 헤드리스 서버 자동 시작 예약 작업, `/api/health` 검증. `-DryRun` 및 단계별 `-Skip*` 지원 |
| `scripts/deploy-agents.ps1` (신규) | 인벤토리 CSV → PC별 자체 완결 패키지(인스톨러 + 토큰 파일 + `install-agent.cmd`) 생성. 토큰은 `/AgentTokenFile`로만 전달되고 성공 시 삭제. `-ExecuteLocal`로 1대 로컬 설치 가능 |
| `scripts/install-agent.cmd` (생성물) | 관리자 확인 → ACL 적용 → `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` 설치 → 토큰 파일 정리. CRLF/ASCII 유지 |
| `scripts/agents.sample.csv` (신규) | `ComputerName,MAC,IP` 인벤토리 양식 |
| `.gitignore` | `!scripts/*.csv` 추가. 생성되는 `deploy/`는 기본적으로 제외되어 토큰 파일이 커밋되지 않음 |
| `docs/DEPLOYMENT.md` (신규) | 50대 연결 토폴로지, IP/DNS, 토큰, 패키지 생성/실행(PsExec·WinRM·USB), 검증, 운영, 문제 해결, 보안 런북 |
| `README.md` | 새 문서/스크립트 링크와 표 행 추가 |

- 제품 코드와 인스톨러 소스는 변경하지 않았다. 문서·배포 도구 추가이므로
  `Directory.Build.props`의 버전은 `0.1.13`으로 유지하고 새 릴리스/태그를 만들지 않았다.
- 학생 계정 암호는 수집·저장하지 않는다. 헤드리스 서버는 교사 로그온 시 예약 작업으로 시작되며,
  앱 모드 창을 닫아도 서버는 유지된다.

### 검증

- 두 신규 `.ps1` 파일의 PowerShell 파서 구문 검사 통과.
- `deploy-agents.ps1`을 임시 인스톨러/토큰/샘플 CSV로 실제 실행해 3개 패키지 생성 확인.
  생성된 `install-agent.cmd`의 URL·PC명·인스톨러명 치환, CRLF 35개/LF 0개, `manifest.csv` 내용 확인.
- `setup-labmanager-lab.ps1`은 관리자 권한이 필요한 환경이라 실제 실행은 하지 못했다.
  관리자 검사만 우회한 임시 복사본으로 `-DryRun` 경로(설정/방화벽/예약 작업 계획 출력)와
  샘플 토큰 거부 동작을 확인했다.
- `scripts\github-push.ps1 -Bump none -AllowSameVersion -DryRun` 실행 결과:
  빌드 경고 0/오류 0, 테스트 113개 통과, 비밀값·개인정보 검사(158개 텍스트 파일) 이상 없음.
- 실제 50대 설치, 교사 PC 방화벽/예약 작업 변경, PsExec/WinRM 원격 실행은
  대상 PC와 자격 증명이 없어 로컬에서 검증하지 못했다.

---

## 2026-11-21 — 인스톨러 단계 안내 및 자동 진행 (v0.1.13)

### 배경

- 기존 인스톨러는 `종료 → 언인스톨 → 설치` 순서를 코드로 보장했지만, 화면에는 별도 안내 없이 곧바로 다음 단계로 넘어갔다.
- 덮어쓰기 설치 시 실행 중인 앱이 강제 종료되거나 기존 설치가 제거되는 순간을 사용자가 알기 어려웠다.

### 변경

| 항목 | 내용 |
|---|---|
| `installer/InnoSetup/*.iss` | `ShowStepNotice` 추가. 진행 페이지에 단계 안내와 카운트다운을 표시하고 5초 후 자동 진행 |
| 안내 단계 | `기존 앱 종료` → `기존 설치 제거` → `새 버전 설치` (Agent/LabManager 공통) |
| 무인 설치 | `WizardSilent`일 때 안내와 대기를 생략해 기존 자동 배포 지연 없음 |
| 조정 값 | `.iss` 상단 `NoticeDelaySeconds`(기본 5초)로 대기 시간 변경 가능 |

- 기존 종료 실패 시 중단, 설정 백업/복원, 서비스 복구 설정, 방화벽 규칙 로직은 변경하지 않았다.
- `InitializeSetup`에서 하던 프로세스 종료를 `PrepareToInstall`로 옮겨, 안내 화면이 종료 단계보다 먼저 표시되도록 했다.

### 검증

- Inno Setup 6 `ISCC.exe`로 Agent/LabManager 인스톨러 모두 컴파일 성공(경고/오류 없음).
- 두 `.iss` 파일의 UTF-8 BOM(`EF-BB-BF`) 유지 확인.
- 실제 권한 상승 덮어쓰기 설치에서의 안내 화면 표시/자동 전환은 로컬 환경에서 검증하지 못했다.
- 릴리스 워크플로 실행 #44886868753 성공. 업로드 자산은 설치 파일 3개뿐이다.
- 공개된 `v0.1.13` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.13-x64.exe` `8a8ac1be39061aa193fcc849633d6a1348dc92db0e9fe2844f3cf9dbf73244c4`
  - `UpdateAgent.LabManager-Setup-0.1.13-x64.exe` `b2ba60e9aa56c0bc55ca6d189faaf1b60f1a0219c8f80c52234f9b8121aaff17`
  - `UpdateAgent.Agent-0.1.13-x64.msi` `1ff6512ff31ae7448a3b5301d622540d8d7050f8594eb084b239d27d73c9df8a`

---

## 2026-11-21 — 별도 창에서 실시간 작업 현황 모니터링 (v0.1.12)

### 배경

- 기존 교사 대시보드는 단일 `--app=` 창에서 5초 폴링으로만 갱신되어, 작업 제어와 전체 현황 관찰을 동시에 하기 어려웠다.
- 여러 PC의 작업 진행을 별도 탭/창에서 실시간으로 보고 싶은 요구가 있었다.
- 실시간 전달은 새 인프라 없이 인메모리 이벤트 버스 + SSE로 구현하고, 기존 대시보드 폴링은 변경하지 않는다.

### 변경

| 항목 | 내용 |
|---|---|
| `LabManager/Live/LiveEventBus.cs` (신규) | 버전과 대기 작업을 공유하는 인메모리 신호 버스. 체크인/작업 변경 시 모든 구독자에 즉시 전달 |
| `LabManager/Live/LiveSnapshot.cs` (신규) | 에이전트/작업 요약과 전체 카운트를 담는 스냅샷 모델 및 빌더 |
| `LabManager/Program.cs` | `/api/admin/live` SSE 추가(초기+변경 시 즉시, 15초 하트비트). 체크인/작업 시작·제어/Maestro 확인에서 신호 발행 |
| `LabManager/wwwroot/monitor.html`, `monitor.js` (신규) | 전용 모니터 페이지. 토큰 배너, 연결 상태, 통계, 진행 중 작업/에이전트 표, 검색 필터, 재연결 |
| `LabManager/wwwroot/index.html`, `app.js`, `styles.css` | 대시보드에 **작업 현황 모니터** 버튼 추가, `/?job=<id>` 상세 딥링크 처리, 모니터 스타일 추가 |
| `LabManager.Desktop/DesktopSettings.cs`, `BrowserLauncher.cs`, `App.xaml.cs` | `MonitorUrl`(`/monitor.html`), `OpenMonitor`, `--monitor` 인자 지원(전용 바로가기) |
| 테스트 | 이벤트 버스·스냅샷 빌더·모니터 URL·앱 모드 인자 6개 추가(전체 113개 통과) |

- SSE는 `EventSource`가 사용자 지정 헤더를 보내지 못하므로 `fetch` 스트림으로 `X-Admin-Token`을 전달한다.
- `localhost`는 `127.0.0.1`로 정규화하고, SPA 폴백이 없으므로 모니터 경로는 `/monitor.html`을 사용한다.
- 두 창은 같은 브라우저 프로필을 공유하므로 토큰을 다시 입력하지 않아도 된다.
- 서버는 창을 닫아도 기존과 같이 백그라운드에 남는다.

### 검증

- 빌드 경고 0/오류 0, 테스트 113개 통과, 정적 검사 통과.
- 로컬 헤드리스 서버에서 `/monitor.html`, `/monitor.js`, `/styles.css` 200 응답과
  `/api/admin/live`의 `text/event-stream` 스냅샷 프레임을 확인.
- 체크인 직후 약 1ms 내 새 스냅샷이 재전송되고 신규 에이전트가 반영됨을 확인.
- `.iss` 파일 2개의 UTF-8 BOM(`EF-BB-BF`)이 릴리스 전후 모두 유지됨을 확인.
- `dotnet publish` 산출물의 `wwwroot`에 `monitor.html`/`monitor.js`가 포함됨을 확인.
- 릴리스 워크플로 실행 #44882964603 성공. 업로드 자산은 설치 파일 3개뿐이다.
- 공개된 `v0.1.12` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.12-x64.exe` `b6bcbb69dfc059e6b0d68afc00d3f470c2ad68132008148b303c46860cb37aed`
  - `UpdateAgent.LabManager-Setup-0.1.12-x64.exe` `0e686499ba37ca711717939e9b60126195cafcdac63330fbda6c3f8e173f9195`
  - `UpdateAgent.Agent-0.1.12-x64.msi` `01003c2ec39203cb5019b9ba29fbf765f80deb162306de973a5dfef37e0f60db`
- 실제 권한 상승 설치, 2창 동시 모니터링, 오버레이/재부팅 시나리오는 로컬 환경에서 검증하지 못했다.

---

## 2026-11-21 — 학생용 앱에서 수동 업데이트 시작 (v0.1.11)

### 배경

- 학생용 상태 앱(`UpdateAgent.Agent.Desktop`)은 상태 조회만 가능한 읽기 전용 앱이었다.
- 실제 작업은 관리자 PowerShell에서 `UpdateAgent.Cli run --confirm-maestro`로 시작할 수 있었지만,
  이 작업은 로컬 저장소에만 기록되어 교사 대시보드에 나타나지 않았다.
- 학생이 상태 앱에서 직접 작업을 시작하면서도 실행 중인 서비스를 통해 대시보드에 보고되도록 할 필요가 있었다.

### 변경

| 항목 | 내용 |
|---|---|
| `Shared/UpdateAgentConstants.cs` | 로컬 제어 파이프 이름 `UpdateAgent.LocalControl.v1` 추가 |
| `Shared/Models/LocalControlModels.cs` (신규) | `start-update` 요청/응답 DTO. 요청은 작업 종류와 Maestro 확인 여부만 포함 |
| `Core/Comms/LocalControlPipe.cs` (신규) | 요청 1줄/응답 1줄 파이프 서버·클라이언트. 최대 요청 크기 제한, 응답 시간 제한, 오버레이와 동일한 ACL(인증 사용자 읽기-쓰기) |
| `Service/Configuration/ServerPolicyProvider.cs` (신규) | 캐시된 서버 정책 로더. `CommandExecutor`도 함께 사용하도록 정리 |
| `Service/Comms/LocalUpdateTrigger.cs` (신규) | 알 수 없는 작업 거부, Maestro 미확인 차단, 확인 주체를 서버에서 `student`로 고정 |
| `Service/Worker.cs`, `Program.cs` | 파이프 서버 시작/종료, 성공 요청 시 즉시 체크인으로 대시보드 반영 |
| `LabManager/Program.cs` | 체크인으로 처음 발견된 Maestro 필요 작업에 대해 학생 확인 체크리스트를 자동 생성 |
| `Agent.Desktop/MainWindow.xaml(.cs)`, `Services/LocalUpdateClient.cs` | **업데이트 시작** 버튼, Maestro/재부팅 확인 창, 결과 표시 및 상태 새로고침 |
| 테스트 | 직렬화·정책 로더·트리거 검증·파이프 왕복 등 9개 추가(전체 107개 통과) |

- 토큰은 여전히 학생 앱에 저장하지 않는다.
- 진행 중인 작업이 있으면 시작 버튼을 비활성화하고, 재개/일시중지/취소는 대시보드에서 처리한다.
- CLI `run`은 로컬 전용 경로로 남으며, 대시보드 보고가 필요한 경우 학생 앱이나 대시보드를 사용한다.

### 검증

- 빌드 경고 0/오류 0, 테스트 107개 통과, 정적 검사 통과.
- 파이프 서버/클라이언트 왕복, Maestro 미확인 차단, 확인자 고정(`student`), 정책 로딩 테스트 통과.
- 릴리스 워크플로 실행 #44880708788 성공. 업로드 자산은 설치 파일 3개뿐이다.
- 공개된 `v0.1.11` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.11-x64.exe` `6884818922ae9b60479f595196a7aa9fb27670eaf2c975745b5e7f0502e9146e`
  - `UpdateAgent.LabManager-Setup-0.1.11-x64.exe` `a52e8b53f434a52cfd85cb789ee7f08a62bb840f0b18fee8d67b7d4a6a085d2d`
  - `UpdateAgent.Agent-0.1.11-x64.msi` `2bafa00505a12e7d716fa7bed0383f86258ea92e0875cbaafc7263ffdf0535f0`
- 실제 권한 상승 설치/오버레이/재부팅 시나리오는 로컬 환경에서 검증하지 못했다.

---

## 2026-11-21 — 대시보드에서 관리자 기본 토큰 표시 및 서버 토큰 변경 (v0.1.10)

### 배경

- LabManager 대시보드는 설치 시 기본값인 샘플 관리자 토큰(`sample-*-token-replace-before-pilot`)을 화면에 표시하지 않아 첫 실행 시 어떤 값을 입력해야 하는지 알기 어려웠다.
- 기존 **토큰** 창은 브라우저 localStorage 값만 바꿀 수 있어, 서버의 `AdminToken`을 변경하려면 쓰기 권한이 필요한 Program Files의 `appsettings.json`을 직접 수정해야 했다.

### 변경

| 항목 | 내용 |
|---|---|
| `wwwroot/index.html`, `app.js`, `styles.css` | 토큰 창에 샘플 관리자 토큰 표시 + **이 값 사용** 버튼, 서버 토큰 변경 입력/확인/버튼, 상태 배지 추가 |
| `Auth/AdminTokenStore.cs` (신규) | 변경된 토큰을 `%LOCALAPPDATA%\UpdateAgent\LabManager\runtime-admin-token.json`에 원자적으로 저장. 8자 이상, 기본/샘플 토큰 거부 |
| `Auth/TokenAuthMiddleware.cs` | `/api/admin` 인증을 `AdminTokenStore.CurrentToken`으로 수행 → 변경 즉시 반영 |
| `Program.cs` | `GET/POST /api/admin/token` 추가(인증 필수). GET은 상태만, POST는 변경 및 감사 로그 `token-change` 기록 |
| `UpdateAgent.Shared/UpdateAgentConstants.cs` | `SampleAdminToken`, `SampleAgentToken`, `IsPlaceholderToken` 추가 |
| 테스트 | 즉시 반영, override 저장/재시작/검증 등 9개 추가(전체 98개 통과) |

- 서버 토큰을 바꾸면 브라우저 localStorage도 새 값으로 갱신되어 다음 요청부터 새 토큰이 사용된다.
- `appsettings.json`의 `AdminToken`은 그대로 두고 런타임 override가 우선하므로 Program Files 쓰기 권한이 필요 없다.
- 에이전트 토큰(`AgentToken`)은 이번 범위 밖이며 기존 `configure-labmanager.ps1`로 별도 교체한다.
- 공개된 샘플 값은 화면에만 표시하고, 실제 현재 토큰 값은 API 응답에 포함하지 않는다.

### 검증

- 빌드 경고 0/오류 0, 테스트 98개 통과, 정적 검사 통과.
- 실제 LabManager 프로세스에서 `/api/admin/token` 흐름 확인: 미인증 401 → 샘플 토큰 200 → 변경 200 → 기존 토큰 401 → 새 토큰 200, override 파일 생성 확인.
- 로컬 Inno Setup 및 MSI 빌드 성공.
- 공개된 `v0.1.10` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.10-x64.exe` `740852cc228c23a509025acd5ed48a1bce3f03b165bd956cff4bda021c659aff`
  - `UpdateAgent.LabManager-Setup-0.1.10-x64.exe` `853db7a5b1a3768d327bcd832d1965861bf08abe8510fab9088c223e16ddb7d6`
  - `UpdateAgent.Agent-0.1.10-x64.msi` `06411ee9478a0d1fc4d79e219e957ca52d96c2ba36ed5f043d0d7b542003ae7d`
- 릴리스 워크플로 실행 #44877569180 성공. 업로드 자산은 설치 파일 3개뿐이다.
- 최초 실행 #44876909662은 `.iss` 파일의 UTF-8 BOM 누락으로 실패했고, BOM 복구 커밋(`a96fae2`)으로 태그를 갱신한 뒤 재실행해 성공했다.

---

## 2026-11-20 — 업그레이드 시 기본 토큰 자동 교체 (v0.1.9)

### 발견한 오류

- v0.1.8은 설치 파일에 샘플 토큰을 넣었지만, **기존 설치를 덮어쓰는 업그레이드 경로**에서는 `appsettings.json`을 백업→복원하므로 예전의 `change-me-*` 값이 그대로 돌아왔다.
- 결과적으로 v0.1.8로 업그레이드해도 서버가 시작되지 않고 대시보드 실행이 건너뛰어졌다.

### 수정

| 항목 | 내용 |
|---|---|
| `installer\InnoSetup\UpdateAgent.LabManager.iss` | 복원된 `appsettings.json`에 `change-me-*`가 남아 있으면 샘플 토큰(`sample-*-token-replace-before-pilot`)으로 자동 교체 |
| 교체 범위 | `change-me-admin-token`, `change-me-agent-token`만 치환 → 사용자가 이미 설정한 토큰은 유지 |
| 실패 처리 | 교체 실패 시 설치 중단(기존 백업/복원 실패 처리와 동일) |

- 신규 설치는 기존과 같이 페이로드의 샘플 토큰을 그대로 사용한다.
- `HasDefaultLabManagerToken`이 복원 후 파일을 검사하므로 대시보드 자동 실행도 정상 동작한다.

### 검증

- Inno Setup 컴파일 성공, 빌드 경고 0/오류 0, 테스트 89개 통과, 정적 검사 통과.
- `change-me-*` 설정을 샘플 토큰으로 치환하는 로직을 시뮬레이션해 유효한 JSON과 잔여 `change-me-` 0개를 확인.
- 로컬 빌드 SHA-256(CI 재빌드 시 달라질 수 있음):
  - `UpdateAgent.Agent-Setup-0.1.9-x64.exe` `3cff19be4a8a3ed5717c6b3e508b3d225f1a6d6e6362276c7c0a3fb1aedf80a4`
  - `UpdateAgent.LabManager-Setup-0.1.9-x64.exe` `83e7e96c6433bc998774c4c8d216664ebabc35179949a7aa703ea43854b48c18`
  - `UpdateAgent.Agent-0.1.9-x64.msi` `d7e1d59d492ede6a281f8bf8225ab08a8680e7888d9ba42b079f3f0e973c5f21`
- 공개된 `v0.1.9` 릴리스의 설치 파일 3개 해시가 릴리스 노트와 일치함을 확인:
  - `UpdateAgent.Agent-Setup-0.1.9-x64.exe` `16bc148ab8d870db4c04ef02a46c0dd138f4d0d77a575247c262c02859e35647`
  - `UpdateAgent.LabManager-Setup-0.1.9-x64.exe` `60b77623ccfefd8e882135aeaa43ca29f27ca7e1cfa4df4fb42e34ab710ed13b`
  - `UpdateAgent.Agent-0.1.9-x64.msi` `95805fd6bf10528132302e1c56349fc42d7439e7404c5c7e42b819cd754da457`
- 릴리스 워크플로 실행 #10 성공. GitHub 자동 생성 소스 코드 2개를 제외한 업로드 자산은 설치 파일 3개뿐이며 기존 릴리스와 동일한 형태다.
- 기존 `v0.1.8` 이하 릴리스는 변경하지 않음.

### 보안 주의

- 샘플 토큰은 공개된 값이므로 실제 배포/파일럿 전 반드시 `configure-labmanager.ps1`로 교체해야 한다.

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

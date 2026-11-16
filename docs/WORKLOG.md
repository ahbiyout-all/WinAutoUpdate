# 작업 로그

이 문서는 UpdateAgent 프로젝트의 주요 작업 이력을 날짜별로 기록합니다.
빌드/테스트 결과와 산출물 해시는 작업 시점 기준이며, 이후 빌드에서 달라질 수 있습니다.

---

## 2026-11-16 — GitHub 자동 업데이트 기반 구성

### 목적

- GitHub 저장소(`AhBiYout-all/UpdateAgent`) 공개를 전제로 한 실시간 업데이트 확인.
- 대시보드에서 새 버전을 자동 감지해 알림 팝업 표시.
- 버전 정보를 코드/빌드/릴리스/UI에서 일관되게 유지.

### 변경

| 영역 | 내용 |
|---|---|
| 공통 상수 | 개발자 `AhBiYout`, 계정 `AhBiYout-all`, 저장소 `UpdateAgent`, 블로그 URL, Releases API, 확인 주기(30분) 추가 |
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

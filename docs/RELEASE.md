# 릴리스와 자동 업데이트

이 문서는 UpdateAgent의 버전 관리, GitHub 릴리스, 자동 업데이트 파이프라인을 정리합니다.
설치 프로그램 자체의 설계는 [`INSTALLER.md`](INSTALLER.md), 작업 이력은 [`WORKLOG.md`](WORKLOG.md)를 참고하세요.

- 개발자: AhBiYout
- GitHub 계정: <https://github.com/ahbiyout-all>
- 저장소: <https://github.com/ahbiyout-all/WinAutoUpdate>
- 공식 블로그: <https://ahbiyoutvibe.blogspot.com/>

## 1. 버전 단일 소스

버전은 **`Directory.Build.props`의 `<Version>` 하나**만 사용합니다.

```xml
<Version>0.1.3</Version>
```

이 값이 다음 위치에 그대로 반영됩니다.

| 위치 | 반영 방법 |
|---|---|
| 어셈블리/파일 버전 | MSBuild가 빌드 시 자동 적용 |
| LabManager 대시보드 버전 배지 | `UpdateAgentConstants.AgentVersion` |
| GitHub API 현재 버전 비교 | `GitHubReleaseChecker` |
| Inno Setup `AppVersion`/파일 이름 | `scripts\build-inno.ps1` |
| MSI `ProductVersion`/파일 이름 | `scripts\build-msi.ps1` |
| Git 태그 | `scripts\github-push.ps1`의 `v<version>` |

버전은 `Major.Minor.Build` 형식의 숫자 3자리만 사용합니다(예: `0.1.3`).
`0.1.0-beta` 같은 접두/접미사는 릴리스 파일 이름과 Inno `VersionInfoVersion`을 깨뜨리므로 사용하지 않습니다.

## 2. 릴리스 흐름

```
로컬 개발 PC                         GitHub Actions                    GitHub Releases
─────────────                         ──────────────                    ───────────────
github-push.ps1
  ├─ 버전 올림
  ├─ 빌드/테스트
  ├─ 비밀값·개인정보 검사
  ├─ commit + tag v<version>
  └─ push ──────────────────────────▶ v* 태그 감지
                                       ├─ build/test
                                       ├─ publish
                                       ├─ Inno Setup + MSI 빌드
                                       └─ 설치 파일만 업로드 ─────────▶ Release v<version>
                                                                          ├─ *Setup*.exe
                                                                          └─ *.msi
```

LabManager는 30분마다(강제 확인 시 즉시) GitHub Releases API를 조회하고,
새 버전이 있으면 대시보드에 팝업을 띄웁니다. 대시보드 자체는 앱 모드 창이고, 팝업의 링크는 기본 브라우저에서 열립니다.

## 3. 로컬에서 릴리스하기

### 준비

- Git for Windows
- .NET SDK 10
- Inno Setup 6 (`ISCC.exe`)
- GitHub 인증: `gh auth login` 또는 Git Credential Manager
- 저장소 원격(`origin`)은 스크립트가 없으면 자동 추가합니다.
  `https://github.com/ahbiyout-all/WinAutoUpdate.git`

### 사용법

```powershell
# 패치 버전 올림: 0.1.2 -> 0.1.3
.\scripts\github-push.ps1 -Bump patch -Message "Fix firewall rule cleanup"

# 마이너/메이저
.\scripts\github-push.ps1 -Bump minor
.\scripts\github-push.ps1 -Bump major

# 버전 직접 지정
.\scripts\github-push.ps1 -Version 0.2.0 -Message "Add LabManager service mode"

# 빌드/테스트를 이미 끝낸 경우
.\scripts\github-push.ps1 -Version 0.2.1 -SkipBuild -SkipTests

# 실제 push 없이 검사만
.\scripts\github-push.ps1 -Bump patch -DryRun
```

배치 파일로도 실행할 수 있습니다.

```bat
scripts\github-push.bat -Bump patch
```

### 스크립트가 하는 일

1. `git config user.name/user.email`을 저장소 로컬에 설정
   (`AhBiYout`, GitHub noreply 이메일 주소).
2. `Directory.Build.props`의 버전을 올리고 파일을 갱신.
3. `dotnet build UpdateAgent.sln -c Release`와 xUnit 테스트 실행.
4. 커밋 대상 파일에서 비밀값/개인 이메일/로컬 절대 경로를 검사.
5. `git add -A` 후 커밋(`Release v<version>`).
6. `v<version>` 태그 생성.
7. `origin`이 없으면 추가하고 현재 브랜치와 태그를 push.

> `github-push.ps1`과 `github-push.bat`은 **저장소에 올라가지 않도록**
> `.gitignore`에서 제외되어 있습니다. 릴리스 스크립트는 로컬에만 둡니다.

### DryRun

`-DryRun`은 버전 파일을 실제로 쓰지 않고, 수행할 작업만 출력합니다.
비밀값 검사와 빌드/테스트는 그대로 실행됩니다.

## 4. GitHub Actions 릴리스

워크플로: [`.github/workflows/release.yml`](../.github/workflows/release.yml)

### 트리거

| 트리거 | 동작 |
|---|---|
| `v*` 태그 push | 태그 버전으로 릴리스 생성 |
| `workflow_dispatch` | `Directory.Build.props`의 현재 버전으로 릴리스 생성 |

수동 실행에서는 버전을 입력받지 않습니다. 버전을 바꾸려면 `Directory.Build.props`를 수정하고
커밋한 뒤 실행하세요. 태그 버전과 소스 버전이 다르면 워크플로가 중단됩니다.
이미 같은 태그의 릴리스가 있으면 설치 파일을 덮어쓰고 릴리스 노트를 갱신합니다.

### 러너 구성

- `windows-latest`
- `actions/checkout@v5`
- `actions/setup-dotnet@v5` + .NET `10.0.x`
- Chocolatey로 Inno Setup 6 설치
- `dotnet tool restore`로 WiX 5.0.2 검증 도구 복원

### 산출물

워크플로는 아래 파일만 Release 자산으로 업로드합니다.

```text
UpdateAgent.Agent-Setup-<버전>-x64.exe
UpdateAgent.LabManager-Setup-<버전>-x64.exe
UpdateAgent.Agent-<버전>-x64.msi        (대안)
```

- `.sha256` 사이드카 파일, `build-info.json`, 로그, 문서는 **업로드하지 않습니다.**
- SHA-256은 릴리스 노트 본문에 기록합니다.
- APK/iOS/모바일 산출물은 만들지 않습니다.

## 5. 자산 이름 규칙

| 제품 | Inno Setup (권장) | MSI (대안) |
|---|---|---|
| Agent | `UpdateAgent.Agent-Setup-<버전>-x64.exe` | `UpdateAgent.Agent-<버전>-x64.msi` |
| LabManager | `UpdateAgent.LabManager-Setup-<버전>-x64.exe` | 없음 (현재 Inno만) |

`GitHubReleaseChecker`의 자산 선택 순서:

1. 이름에 `Setup`이 포함된 `.exe` (Inno Setup)
2. `.msi`
3. 첫 번째 자산

따라서 릴리스에 여러 파일이 있어도 Inno Setup 설치 파일이 자동 선택됩니다.

## 6. 태그와 릴리스 규칙

- 태그 형식: `v<Major.Minor.Build>` (예: `v0.1.3`)
- 태그와 `Directory.Build.props` 버전은 **반드시 같아야** 합니다.
- 같은 버전으로 다시 릴리스하지 않습니다. 수정은 Build 번호를 올립니다.
- 릴리스 노트에는 최소한 아래를 포함합니다.
  - 버전
  - 설치 파일 이름과 크기
  - SHA-256
  - 주요 변경 사항
  - 알려진 제약(미서명, SmartScreen 경고 등)

`github-push.ps1`은 이미 존재하는 태그를 덮어쓰지 않습니다.
같은 버전을 다시 올려야 하면 `-AllowSameVersion`을 명시합니다.

## 7. 자동 업데이트 동작

1. LabManager가 `GET https://api.github.com/repos/ahbiyout-all/WinAutoUpdate/releases/latest`를 호출합니다.
2. 현재 버전과 태그를 숫자로 비교합니다.
3. 새 버전이면 대시보드에 팝업을 표시합니다.
   - 현재/최신 버전
   - 릴리스 노트
   - 자산 이름/크기
   - GitHub 저장소/릴리스 링크
   - `업데이트 내려받기` 버튼(자산 URL)
   - 링크는 `target="_blank"`로 열려 기본 브라우저 창/탭에서 표시됩니다.
4. 사용자가 `나중에`를 누르면 해당 버전은 세션 저장소에 기록되어 다시 표시하지 않습니다.
5. 30분마다 다시 확인하며, 상단 버전 배지를 누르면 즉시 강제 확인할 수 있습니다.

업데이트 확인은 **읽기 전용**입니다. 자동으로 설치 파일을 내려받아 실행하지 않으며,
교사가 다운로드 버튼을 눌러 설치 파일을 받은 뒤 직접 실행합니다.

## 8. 문제 해결

| 증상 | 원인 | 해결 |
|---|---|---|
| 대시보드에 업데이트가 안 뜸 | 저장소가 비공개 | 공개 저장소로 전환하거나 API 토큰 지원 추가(2단계) |
| "확인 실패"만 표시 | 네트워크/프록시/GitHub API 차단 | 교사 PC에서 `https://api.github.com` 접속 확인 |
| 최신 버전인데 팝업이 뜸 | `Directory.Build.props` 버전이 태그보다 낮음 | 버전을 올리고 새 태그로 릴리스 |
| `.msi`가 다운로드됨 | 릴리스에 `*Setup*.exe`가 없음 | Inno Setup 설치 파일을 릴리스에 첨부 |
| Actions에서 Inno 컴파일 실패 | 러너에 Inno Setup 없음 | 워크플로의 `Install Inno Setup 6` 단계 확인 |
| Actions에서 MSI 검증 실패 | WiX 도구 복원 실패 | `dotnet tool restore` 단계/네트워크 확인 |
| 태그 push 후 릴리스가 안 생김 | Actions 권한 부족 | 저장소 Settings → Actions → Workflow permissions를 `Read and write`로 설정 |
| 같은 버전 태그가 이미 있음 | 버전을 올리지 않음 | Build 번호를 올리거나 `-AllowSameVersion` 사용 |

## 9. 롤백

릴리스 자체를 되돌리는 것보다 **이전 설치 파일을 다시 배포**하는 것이 안전합니다.

1. GitHub Releases에서 이전 버전 설치 파일을 다운로드합니다.
2. 학생 PC/교사 PC에서 이전 설치 파일을 실행합니다.
   - Inno Setup은 AppID 기준으로 업그레이드/다운그레이드를 처리합니다.
   - 설정(`ProgramData\UpdateAgent`, LabManager `appsettings.json`)은 유지됩니다.
3. 필요하면 `v<이전 버전>` 태그의 릴리스 노트를 참고합니다.
4. 자동 업데이트 팝업을 막아야 하면 최신 릴리스보다 낮은 버전을 유지하거나,
   임시로 릴리스를 `pre-release`로 표시합니다.
   (`releases/latest`는 pre-release를 제외합니다.)

## 10. 체크리스트

릴리스 전:

- [ ] `Directory.Build.props` 버전이 의도한 값인가
- [ ] `dotnet build UpdateAgent.sln -c Release` 경고 0/오류 0
- [ ] `dotnet test` 전체 통과
- [ ] `scripts\build-inno.ps1`로 두 설치 파일 생성
- [ ] `scripts\build-msi.ps1`로 MSI 생성(필요 시)
- [ ] `artifacts\inno\build-info.json`의 `containsSecrets`가 `false`
- [ ] 릴리스 자산이 설치 파일만인가
- [ ] SHA-256을 릴리스 노트에 기록했는가
- [ ] 관리자 권한 VM/실 PC에서 설치·업그레이드·제거 확인

릴리스 후:

- [ ] GitHub Release에 Inno Setup 파일 2개가 있는가
- [ ] 태그(`v<버전>`)와 `Directory.Build.props` 버전이 같은가
- [ ] LabManager 대시보드(앱 모드 창)에서 새 버전 팝업이 뜨는가
- [ ] 다운로드 버튼이 올바른 자산을 가리키는가
- [ ] `docs/WORKLOG.md`에 릴리스 내용을 기록했는가

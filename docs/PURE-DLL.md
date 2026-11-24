# 순수 창작 공통 Guard DLL 설계와 동작 원리

작성일: 2026-11-24 · 적용 버전: `0.1.35`

이 문서는 프로젝트가 **직접 작성한** 공통 라이브러리 `UpdateAgent.Guards.dll`의 존재 이유,
동작 원리, 호출부 연결, 그리고 기존 DLL 산출물 점검 결과를 기록합니다.
외부 패키지 코드를 복사하거나 서드파티 바이너리를 감싼 것이 아니라,
보안·안전 규칙만을 순수 함수로 구현한 자체 제작 DLL입니다.

---

## 1. 왜 새 DLL로 분리했는가

| 판단 기준 | 분리 결과 |
|---|---|
| 여러 프로젝트가 같은 규칙을 쓰는가 | 토큰 규칙은 Shared·LabManager·서비스·런처가, URL 규칙은 릴리스 확인기·런처가, UTC 규칙은 LabManager 저장소 전반이 사용 |
| 규칙이 보안에 직접 영향을 주는가 | 기본 토큰 사용, 관리자/에이전트 토큰 동일, 임의 사이트 앱 모드 실행, SQLite 만료 시각 오프셋 |
| 외부 의존성 없이 검증 가능한가 | 파일·프로세스·네트워크·환경변수에 접근하지 않으면 단위 테스트로 완전히 재현 가능 |
| 플랫폼 독립적인가 | `net10.0` 단일 타깃이 `net10.0`과 `net10.0-windows` 소비자 모두에서 사용 가능 |

**제외한 기능**(DLL에 넣지 않음): `icacls` ACL 변경, 명명 파이프, WUA 호출, WPF 오버레이/키보드 훅,
방화벽 규칙 등록. 이들은 OS 자원을 직접 다루므로 기존 프로젝트에 남겨 두었습니다.

---

## 2. 산출물과 의존성

| 항목 | 값 |
|---|---|
| 프로젝트 | `src/UpdateAgent.Guards/UpdateAgent.Guards.csproj` |
| 어셈블리 | `UpdateAgent.Guards.dll` |
| 타깃 프레임워크 | `net10.0` |
| NuGet `PackageReference` | **없음** |
| OS 전용 API | 없음(`System.Security.Cryptography.SHA256`은 관리형 BCL) |
| 버전 | `Directory.Build.props`의 `<Version>` 단일 소스(현재 `0.1.35`) |
| 테스트 접근 | `InternalsVisibleTo UpdateAgent.Tests` |

프로젝트 참조를 통해 각 실행 파일의 게시 폴더로 DLL이 자동 복사됩니다.
WiX는 `Package.wxs`의 와일드카드 수집으로, Inno는 게시 폴더 스테이징으로 동일 파일을 포함합니다.
따라서 설치 스크립트에 개별 파일 항목을 추가할 필요가 없습니다.

---

## 3. 동작 원리

### 3.1 `TokenGuard` — 토큰 문자열 규칙

| 메서드 | 동작 | 실패 시 의미 |
|---|---|---|
| `Normalize` | BOM(`U+FEFF`) 제거 후 앞뒤 공백 제거 | `null`은 빈 문자열로 취급 |
| `IsPlaceholder` | 비어 있거나 `change-me-*`, 또는 `sample-*-replace-before-pilot`이면 참 | 공개된 예시 값이므로 배포 토큰으로 사용 금지 |
| `IsAcceptable` | 정규화 후 길이 8자 이상 + 필요 시 기본값 금지 | 파일럿 토큰은 대시보드에서 검증된 값만 허용 |
| `FixedTimeEquals` | 두 토큰을 UTF-8 → SHA-256 → `CryptographicOperations.FixedTimeEquals`로 비교 | 빈 값끼리도 일치하지 않음. 비교 시간이 입력 길이에 의존하지 않음 |
| `AreDistinct` | `FixedTimeEquals`의 부정 | 관리자 토큰과 에이전트 토큰이 같으면 학생이 관리자 API를 호출할 수 있음 |

**왜 해시를 비교하는가**: 원문 문자열을 직접 비교하면 비교 도중 일치 위치에서 조기 종료되어
타이밍 차이로 토큰을 추측할 수 있습니다. 고정 시간 비교는 해시 길이가 항상 같아 이 누출을 막습니다.

### 3.2 `UrlGuard` — 링크 안전 규칙

| 메서드 | 동작 |
|---|---|
| `IsHttpUrl` | 절대 URL, `http`/`https` 스킴, 비어 있지 않은 호스트, 사용자 정보(`user:pass@`) 없음 |
| `IsAllowedExternalUrl` | 외부 링크의 스킴 검증 + 선택적으로 GitHub 호스트 허용 목록 검증 |
| `IsLoopbackHost` | `localhost` 또는 루프백 IP(`127.0.0.0/8`, `::1`) 판별 |
| `IsSafeBrowserUrl` | 로컬 서버는 HTTP 허용, 원격 주소는 HTTPS만 허용 |
| `NormalizeBrowserUrl` | 안전한 주소를 정규화(HTTP `localhost` → `127.0.0.1`), 실패 시 `null` |

`file:`, `javascript:`, `ftp:` 같은 스킴과 사용자 정보가 포함된 URL은 앱 모드 창과
릴리스 링크 모두에서 거부됩니다. 교사 런처가 `desktop-settings.json`에 우연히 잘못된 주소를 가지거나
외부에서 이 파일을 덮어써도 브라우저 `--app=` 인자로 전달되지 않습니다.

### 3.3 `UtcClock` — 시각 규칙

- 현재 시각은 항상 `DateTimeOffset.UtcNow`를 사용합니다.
- 저장 문자열은 `ToString("O", InvariantCulture)`로 통일합니다.
- 오프셋이 없는 문자열은 `AssumeUniversal | AdjustToUniversal`로 UTC로 해석합니다.

SQLite의 `TEXT` 비교는 문자열의 오프셋을 계산하지 않으므로, 로컬 오프셋이 섞이면
명령 만료·작업 보존 기간이 실제보다 밀리거나 늦어집니다. 서버 저장·비교·응답 경로는
이 시계를 통해 UTC만 다루며, `UtcTimestamps`는 기존 호출부를 위한 얇은 어댑터만 남겼습니다.

---

## 4. 호출부 연결 지점

| 호출부 | 사용 메서드 | 효과 |
|---|---|---|
| `src/UpdateAgent.Shared/UpdateAgentConstants.cs` | `TokenGuard.IsPlaceholder` | 기본/샘플 토큰 판별의 단일 정의 |
| `src/UpdateAgent.Shared/Updates/GitHubReleaseChecker.cs` | `UrlGuard.IsAllowedExternalUrl` | 릴리스 링크 스킴/호스트 검증 위임 |
| `src/UpdateAgent.LabManager/Program.cs` | `Normalize`, `IsPlaceholder`, `AreDistinct` | 시작 시 기본 토큰·동일 토큰 fail-closed |
| `src/UpdateAgent.LabManager/Auth/TokenAuthMiddleware.cs` | `FixedTimeEquals` | 경로별 토큰 비교의 고정 시간화 |
| `src/UpdateAgent.LabManager/Auth/AdminTokenStore.cs` | `IsAcceptable`, `FixedTimeEquals` | 런타임 override 검증 및 관리자/에이전트 충돌 차단 |
| `src/UpdateAgent.LabManager/Services/UtcTimestamps.cs` | `UtcClock` | 기존 저장소 코드의 UTC 일관성 유지 |
| `src/UpdateAgent.LabManager.Desktop/Services/DesktopSettings.cs` | `IsSafeBrowserUrl`, `NormalizeBrowserUrl`, `IsLoopbackHost` | 설정 로드·URL 생성 시 안전성 검증 |
| `src/UpdateAgent.LabManager.Desktop/Services/BrowserLauncher.cs` | `IsSafeBrowserUrl` | `--app=` 전달 전 최종 차단 |
| `tests/UpdateAgent.Tests/GuardsLibraryTests.cs` | 전체 | 규칙 자체와 호출부 회귀 테스트 |

---

## 5. 기존 DLL/라이브러리 점검 결과

Git에는 `.dll` 파일이 추적되지 않으며(`build`, `bin`, `obj`, `publish`, `artifacts`는 `.gitignore`),
로컬 스테이징 폴더의 자체 제작 DLL 24개는 모두 당시 버전 `0.1.34`였습니다.
게시 폴더와 Inno 스테이징의 중복 사본은 동일 바이트로 정상이며, 오래된 `bin\Debug` 산출물
(`0.1.33`, `0.1.14`)은 로컬 빌드 잔해일 뿐 배포 대상이 아닙니다.

이번 작업에서 확인·수정한 항목:

| 심각도 | 항목 | 처리 |
|---|---|---|
| P1 | 대시보드에서 런타임 관리자 토큰을 에이전트 토큰과 같게 설정하면 학생 토큰으로 관리자 API 접근 가능 | `AdminTokenStore`가 저장/로드 양쪽에서 충돌을 거부(수정) |
| P2 | 외부 override 파일의 짧은 토큰이 길이 검증 없이 적용됨 | `IsAcceptable`(최소 8자)로 통일(수정) |
| P2 | 같은 크기·같은 수정 시각으로 덮어쓴 override를 감지하지 못함 | 수정 시각/크기 대신 내용 SHA-256 지문 사용(수정) |
| P2 | `desktop-settings.json`의 주소가 스킴 검증 없이 브라우저 `--app=`로 전달됨 | 로드/실행 이중 검증(수정) |
| P2 | 기본 토큰 목록이 C#과 PowerShell에 중복 정의됨 | C# 규칙은 DLL로 일원화(잔여: 설치 스크립트 쪽은 별도 유지) |

남은 개선 사항(이번 범위 밖):

- `GitHubReleaseChecker`의 기본 생성자는 호환을 위해 비엄격 모드를 유지합니다. 프로덕션 호출부
  (`UpdateCheckService`)는 명시적으로 엄격 모드를 사용합니다.
- 공유 DTO 일부와 로컬 작업 ID 경로에 로컬 시각 기본값이 남아 있습니다. 서버 경로는 UTC로 정규화됩니다.
- 설치/파일럿 스크립트의 기본 토큰 목록은 PowerShell에 별도로 존재하므로 향후 공통 생성기 검토가 필요합니다.
- 자체 제작 DLL에 Authenticode 서명은 아직 적용되지 않았습니다(`docs/WORKLOG.md`의 향후 개선 제안에 기록).

---

## 6. 검증과 한계

- `dotnet build UpdateAgent.sln -c Release --nologo`: 경고 0, 오류 0.
- `dotnet test tests\UpdateAgent.Tests\UpdateAgent.Tests.csproj -c Release --nologo`: **230개 통과**, 실패 0.
- 테스트에는 DLL 규칙뿐 아니라 토큰 충돌·짧은 override·동일 크기 재기록·안전하지 않은 브라우저 URL
  회귀가 포함되어 있습니다.
- 실 PC 설치/파일럿 검증(토큰 교체, 50대 동시 체크인)은 환경이 없어 미실행입니다.
- 이 DLL은 문자열 규칙만 담당합니다. ACL/프로세스/네트워크 작업을 이 DLL에 넣으면
  "순수 함수" 성질이 깨지므로 추가하지 않습니다.

# 순수 창작 공통 Guard DLL 설계와 동작 원리

작성일: 2026-11-24 · 적용 버전: `0.1.37`

이 문서는 프로젝트가 **직접 작성한** 공통 라이브러리 `UpdateAgent.Guards.dll`의 존재 이유,
동작 원리, 호출부 연결, 그리고 기존 DLL 산출물 점검 결과를 기록합니다.
외부 패키지 코드를 복사하거나 서드파티 바이너리를 감싼 것이 아니라,
보안·안전 규칙만을 순수 함수로 구현한 자체 제작 DLL입니다.

---

## 1. 왜 새 DLL로 분리했는가

| 판단 기준 | 분리 결과 |
|---|---|
| 여러 프로젝트가 같은 규칙을 쓰는가 | 토큰/URL/UTC 규칙은 Shared·Core·LabManager·런처·서비스가, 버전/ID/진행률/상태 규칙은 에이전트·서버·모니터가 함께 사용 |
| 규칙이 보안에 직접 영향을 주는가 | 기본 토큰 사용, 관리자/에이전트 토큰 동일, 임의 사이트 앱 모드 실행, SQLite 만료 시각 오프셋, 위조된 작업 상태 |
| 외부 의존성 없이 검증 가능한가 | 파일·프로세스·네트워크·환경변수에 접근하지 않으면 단위 테스트로 완전히 재현 가능 |
| 플랫폼 독립적인가 | `net10.0` 단일 타깃이 `net10.0`과 `net10.0-windows` 소비자 모두에서 사용 가능 |

**제외한 기능**(DLL에 넣지 않음): `icacls` ACL 변경, 명명 파이프, WUA 호출, WPF 오버레이/키보드 훅,
방화벽 규칙 등록, SQLite 접근. 이들은 OS 자원을 직접 다루므로 기존 프로젝트에 남겨 두었습니다.

---

## 2. 산출물과 의존성

| 항목 | 값 |
|---|---|
| 프로젝트 | `src/UpdateAgent.Guards/UpdateAgent.Guards.csproj` |
| 어셈블리 | `UpdateAgent.Guards.dll` |
| 타깃 프레임워크 | `net10.0` |
| NuGet `PackageReference` | **없음** |
| OS 전용 API | 없음(`System.Security.Cryptography.SHA256`은 관리형 BCL) |
| 버전 | `Directory.Build.props`의 `<Version>` 단일 소스(현재 `0.1.37`) |
| 테스트 접근 | `InternalsVisibleTo UpdateAgent.Tests` |

프로젝트 참조를 통해 각 실행 파일의 게시 폴더로 DLL이 자동 복사됩니다.
WiX는 `Package.wxs`의 와일드카드 수집으로, Inno는 게시 폴더 스테이징으로 동일 파일을 포함합니다.
`build-inno.ps1`의 필수 파일 검증과 `build-msi.ps1`의 게시 폴더 검증이 이 DLL의 존재를 확인합니다.

---

## 3. 동작 원리

### 3.1 `TokenGuard` — 토큰 문자열 규칙

| 메서드 | 동작 | 실패 시 의미 |
|---|---|---|
| `Normalize` | **앞의** BOM(`U+FEFF`)만 제거한 뒤 바깥 공백 제거. 내부 BOM은 의미 있는 문자로 유지 | `null`은 빈 문자열로 취급 |
| `IsPlaceholder` | 비어 있거나 `change-me-*`, 또는 `sample-*-replace-before-pilot`이면 참 | 공개된 예시 값이므로 배포 토큰으로 사용 금지 |
| `IsAcceptable` | 정규화 후 길이 8자 이상 + 필요 시 기본값 금지 | 파일럿 토큰은 대시보드에서 검증된 값만 허용 |
| `FixedTimeEquals` | 두 토큰을 UTF-8 → SHA-256 → `CryptographicOperations.FixedTimeEquals`로 비교. 빈 값은 항상 거부 | 해시 계산은 입력 길이에 비례하지만 **최종 비교는 항상 32바이트 고정** |
| `AreDistinct` | 어느 쪽이든 비어 있으면 `false`, 아니면 `FixedTimeEquals`의 부정 | 관리자 토큰과 에이전트 토큰이 같거나 비어 있으면 fail-closed |

내부 BOM을 제거하면 서로 다른 두 토큰이 같게 취급될 수 있으므로 앞의 BOM만 정규화합니다.
비교 자체는 대소문자를 구분합니다.

### 3.2 `UrlGuard` — 링크 안전 규칙

| 메서드 | 동작 |
|---|---|
| `IsHttpUrl` | 절대 URL, `http`/`https` 스킴, 비어 있지 않은 호스트, 사용자 정보(`user:pass@`) 없음 |
| `IsAllowedExternalUrl` | 외부 링크의 스킴 검증 + 선택적으로 GitHub 호스트 허용 목록 검증 |
| `IsLoopbackHost` | `localhost` 또는 루프백 IP(`127.0.0.0/8`, `::1`) 판별 |
| `IsSafeBrowserUrl` | 로컬 서버는 HTTP 허용, 원격 주소는 HTTPS만 허용 |
| `NormalizeBrowserUrl` | 안전한 주소를 정규화(HTTP `localhost` → `127.0.0.1`), 실패 시 `null` |
| `CombineBrowserUrl` | 정규화된 주소의 경로 끝에 상대 경로를 붙이며 포트·쿼리·조각 유지, 실패 시 `null` |

`file:`, `javascript:`, `ftp:` 같은 스킴과 사용자 정보가 포함된 URL은 앱 모드 창과
릴리스 링크 모두에서 거부됩니다. `IsSafeBrowserUrl`은 **원격 호스트 자체를 제한하지 않습니다.**
`desktop-settings.json`을 쓸 수 있는 사람은 원격 대상을 바꿀 수 있으므로, 이 파일의 ACL이 함께 중요합니다.

### 3.3 `UtcClock` — 시각 규칙

- 현재 시각은 항상 `DateTimeOffset.UtcNow`를 사용합니다.
- 저장 문자열은 `ToString("O", InvariantCulture)`로 통일합니다.
- 허용 형식은 ISO-8601 시각 문자열만입니다. 날짜만 있는 값(`2026-11-24`)은 조용히 보정하지 않고 거부합니다.
- 오프셋이 없는 문자열은 `AssumeUniversal | AdjustToUniversal`로 UTC로 해석하고,
  오프셋이 있으면 UTC로 변환합니다.

SQLite의 `TEXT` 비교는 문자열의 오프셋을 계산하지 않으므로, 로컬 오프셋이 섞이면
명령 만료·작업 보존 기간이 실제보다 밀리거나 늦어집니다. 서버 저장·비교·응답 경로는
이 시계를 통해 UTC만 다루며, `UtcTimestamps`는 기존 호출부를 위한 얇은 어댑터만 남겼습니다.

### 3.4 `VersionGuard` — 릴리스 버전 비교

`v` 접두와 공백을 제거하고 `prerelease(-)`/`build(+)` 뒤를 잘라 숫자 버전만 비교합니다.
해석할 수 없는 태그(`nightly` 등)는 업데이트로 취급하지 않습니다.
`GitHubReleaseChecker.NormalizeVersion`/`IsNewer`가 이 규칙으로 위임되어 에이전트와 서버가 같은 판정을 합니다.

### 3.5 `TextGuard` — ID/감사 텍스트 규칙

- `IsSafeIdentifier`: 비어 있지 않고 128자 이하이며 영문·숫자·`-`·`_`로만 구성된 ID만 허용합니다.
- `Truncate`: 클라이언트가 보낸 감사 메시지를 저장 전에 잘라냅니다(라벨 128자, 본문 512자).

체크인 ID와 작업 ID를 검증하면 공유 에이전트 토큰으로 임의 문자열을 무한 생성해 DB를 오염시키는 것을 막습니다.

### 3.6 `JobStateRules` — 작업 상태 규칙

`Terminal`(Completed/Failed/Cancelled)과 `Busy`(Busy/Rebooting/Paused) 집합을 한 곳에서 정의합니다.
공유 DTO, LabManager 서버 모니터, 개요 API가 각자 복제하던 상태 판정을 이 규칙으로 통일했습니다.

### 3.7 `JobIdGuard` / `ProgressGuard` — 작업 ID와 진행률

- `JobIdGuard.Create`: UTC 시각 + GUID로 32자 ID를 생성합니다. 로컬 오프셋을 쓰지 않고 GUID를 과도하게 자르지 않습니다.
- `ProgressGuard.Percent`: 완료는 100, 그 외는 0~99로 제한합니다. `Display`는 화면 표시용 0~100 clamp입니다.

### 3.8 `WindowSizeGuard` — 창 크기 규칙

`double` 창 크기를 NaN/Infinity/극단값으로부터 안전한 범위(640×480 ~ 3840×2160)로 정규화합니다.
정수 변환 전에 범위를 확인하므로 오버플로로 음수 크기가 되지 않습니다.

---

## 4. 호출부 연결 지점

| 호출부 | 사용 메서드 | 효과 |
|---|---|---|
| `src/UpdateAgent.Shared/UpdateAgentConstants.cs` | `TokenGuard.IsPlaceholder` | 기본/샘플 토큰 판별의 단일 정의 |
| `src/UpdateAgent.Shared/Updates/GitHubReleaseChecker.cs` | `VersionGuard`, `UtcClock` | 릴리스 버전 판정·확인 시각의 단일 정의 |
| `src/UpdateAgent.Shared/Models/*` | `UtcClock.Now`, `JobStateRules` | DTO 기본 시각 UTC화, 종료 상태 판정 일원화 |
| `src/UpdateAgent.Core/Orchestration/UpdateJobRunner.cs` | `UtcClock`, `JobIdGuard`, `ProgressGuard`, `JobStateRules` | 시각/ID/진행률/상태 규칙 통일, WU 대기에 정책 시간 적용 |
| `src/UpdateAgent.Core/Storage/SqliteJobStore.cs` | `UtcClock.Format` | `updated_at`/이벤트 시각 UTC 저장 |
| `src/UpdateAgent.LabManager/Program.cs` | `Normalize`, `IsPlaceholder`, `AreDistinct`, `JobIdGuard` | 시작 시 토큰 fail-closed, 작업 ID 형식 통일 |
| `src/UpdateAgent.LabManager/Auth/TokenAuthMiddleware.cs` | `FixedTimeEquals` | 경로별 토큰 비교의 고정 시간화 |
| `src/UpdateAgent.LabManager/Auth/AdminTokenStore.cs` | `IsAcceptable`, `FixedTimeEquals` | 런타임 override 검증 및 관리자/에이전트 충돌 차단 |
| `src/UpdateAgent.LabManager/Services/AgentCheckInHandler.cs` | `TextGuard` | ID 검증 전 상태 변경 방지, 감사 텍스트 절단 |
| `src/UpdateAgent.LabManager/Services/UtcTimestamps.cs` | `UtcClock` | 기존 저장소 코드의 UTC 일관성 유지 |
| `src/UpdateAgent.LabManager/Live/LiveSnapshot.cs` | `JobStateRules` | 활성 작업 수를 전체 작업에서 집계 |
| `src/UpdateAgent.LabManager.Desktop/Services/DesktopSettings.cs` | `CombineBrowserUrl`, `WindowSizeGuard` | 설정 로드·URL 생성·창 크기 검증 |
| `src/UpdateAgent.LabManager.Desktop/Services/BrowserLauncher.cs` | `IsSafeBrowserUrl`, `WindowSizeGuard` | `--app=` 전달 전 최종 차단, 창 크기 clamp |
| `src/UpdateAgent.LabManager.Desktop/Services/LabManagerServer.cs` | `CombineBrowserUrl`, `UtcClock` | health URL을 브라우저와 같은 규칙으로 생성 |
| `src/UpdateAgent.SessionHelper/*` | `ProgressGuard`, `KeyboardBlocker` 예외 처리 | 표시 진행률 clamp, 훅 설치 실패 보고 |
| `tests/UpdateAgent.Tests/GuardsLibraryTests.cs` | 전체 | 규칙 자체와 호출부 회귀 테스트 |

---

## 5. 기존 DLL/라이브러리 점검 결과

Git에는 `.dll` 파일이 추적되지 않으며(`build`, `bin`, `obj`, `publish`, `artifacts`는 `.gitignore`),
로컬 스테이징 폴더의 자체 제작 DLL은 모두 당시 버전이었습니다.
게시 폴더와 Inno 스테이징의 중복 사본은 동일 바이트로 정상이며, 오래된 `bin\Debug` 산출물은
로컬 빌드 잔해일 뿐 배포 대상이 아닙니다.

v0.1.35 작업(1차 점검)에서 확인·수정한 항목:

| 심각도 | 항목 | 처리 |
|---|---|---|
| P1 | 대시보드에서 런타임 관리자 토큰을 에이전트 토큰과 같게 설정하면 학생 토큰으로 관리자 API 접근 가능 | `AdminTokenStore`가 저장/로드 양쪽에서 충돌을 거부(수정) |
| P2 | 외부 override 파일의 짧은 토큰이 길이 검증 없이 적용됨 | `IsAcceptable`(최소 8자)로 통일(수정) |
| P2 | 같은 크기·같은 수정 시각으로 덮어쓴 override를 감지하지 못함 | 수정 시각/크기 대신 내용 SHA-256 지문 사용(수정) |
| P2 | `desktop-settings.json`의 주소가 스킴 검증 없이 브라우저 `--app=`로 전달됨 | 로드/실행 이중 검증(수정) |
| P2 | 기본 토큰 목록이 C#과 PowerShell에 중복 정의됨 | C# 규칙은 DLL로 일원화(잔여: 설치 스크립트 쪽은 별도 유지) |

v0.1.36 작업(5회 반복 감사)에서 확인·수정한 항목:

| 영역 | 심각도 | 항목 | 처리 |
|---|---|---|---|
| Guard | P2 | 내부 BOM 제거로 서로 다른 토큰이 같게 취급 | 앞의 BOM만 제거(수정) |
| Guard | P2 | 빈 토큰끼리 `AreDistinct`가 참 | 빈 값이면 false로 fail-closed(수정) |
| Guard | P2 | 문서가 "비교 시간이 입력 길이에 의존하지 않음"이라고 과장 | 해시 계산은 길이 비례, 비교만 고정으로 정정(수정) |
| Guard | P2 | `UtcClock.Parse`가 날짜만 있어도 허용 | 허용 형식 엄격화(수정) |
| Core | P1 | SQLite `TEXT` 정렬에 로컬 오프셋 시각 저장 | `UtcClock.Format` 사용(수정) |
| Core | P1 | `WuBusyWaitMinutes` 정책이 구현 경로에 미연결 | 재시도 대기를 정책 시간으로 제한(수정) |
| Core | P2 | 검색은 `SanitizeFor`, 실행은 `ForExecution` 적용 | 검색도 `ForExecution` 사용(수정) |
| Core | P2 | 로컬 시간 + 4자리 GUID로 ID 충분도 낮음 | `JobIdGuard.Create`(UTC, 32자) 사용(수정) |
| UI | P1 | 키보드 훅 설치 실패가 silent | 설치 실패 시 예외→안내 표시(수정) |
| UI | P2 | health URL이 `UrlGuard` 우회 | `CombineBrowserUrl`로 통일(수정) |
| UI | P2 | 서버 시작 실패 시 process 잔류, 창 크기 미검증 | `Stop()`, `WindowSizeGuard` 적용(수정) |
| LabManager | P1 | 체크인 명령 결과 배치 상한 없음 | 100건 상한 + 초과분 전부 ack(수정) |
| LabManager | P1 | 미검증 `JobId`로 임의 상태 저장 | ID 검증을 상태 변경보다 앞으로 이동(수정) |
| LabManager | P1 | 미래 `ReportedAt`이 이후 모든 보고 거부 | 수신 시각 상한으로 clamp(수정) |
| LabManager | P1 | 가짜 에이전트가 DB에 누적 | ID 형식/등록 수 상한 검증(수정) |
| LabManager | P1 | 외부 결과 400 응답으로 재시도 무한 | 200 + 거부 ack(수정) |
| LabManager | P2 | `_cached` 비-volatile, force가 gate 우회, gate 대기 무제한 | volatile화, force도 gate 통과, 10초 제한(수정) |
| 패키징/문서 | P2 | `PURE-DLL.md` cross-link 누락, 스테이징 검증기 미명시 | 문서 링크와 필수 DLL 검증 추가(수정) |

---

## 6. 검증과 한계

- `dotnet build UpdateAgent.sln -c Release --nologo`: 경고 0, 오류 0.
- `dotnet test tests\UpdateAgent.Tests\UpdateAgent.Tests.csproj -c Release --nologo`: **244개 통과**, 실패 0.
- 테스트에는 DLL 규칙뿐 아니라 토큰 충돌/내부 BOM/짧은 override/동일 크기 재기록/안전하지 않은 브라우저 URL,
  체크인 ack·감사 절단, 미래 시각 clamp, 대량 배치 ack 회귀가 포함되어 있습니다.
- `scripts\publish.ps1`, `scripts\build-inno.ps1 -NoPublish`, `scripts\build-msi.ps1`로 게시/패키징을 확인합니다.
- 실 PC 설치/파일럿 검증(토큰 교체, 50대 동시 체크인·감사·SSE 부하)은 환경이 없어 미실행입니다.
- 이 DLL은 문자열 규칙만 담당합니다. ACL/프로세스/네트워크 작업을 이 DLL에 넣으면
  "순수 함수" 성질이 깨지므로 추가하지 않습니다.

### 향후 개선 제안

- 설치/파일럿 스크립트의 기본 토큰 목록은 PowerShell에 별도로 존재하므로 공통 생성기 검토가 필요합니다.
- 자체 제작 DLL에 Authenticode 서명은 아직 적용되지 않았습니다(`docs/WORKLOG.md` 참고).
- 원격 앱 모드 URL은 호스트 허용 목록이 없습니다. 설정 파일 ACL을 유지하고, 필요하면
  `UrlGuard.IsAllowedExternalUrl`의 허용 목록을 대시보드에도 적용하는 방안을 검토합니다.

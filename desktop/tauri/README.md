# UpdateAgent Tauri 스캐폴드

기존 UpdateAgent .NET 솔루션과 **연결되지 않은 독립 Tauri 2 데스크톱 앱 기본 템플릿**입니다.
LabManager 대시보드나 에이전트와 통합하지 않으며, 빌드/릴리스/인스톨러에도 포함되지 않습니다.

## 요구 사항

- Node.js 22 이상 (현재 환경: Node 24)
- Rust 1.77 이상 (현재 환경: Rust 1.95)
- Windows에서 Tauri는 WebView2를 사용합니다. 향후 LabManager와 통합할 경우
  기존의 "WebView2 미사용, Edge/Chrome `--app=` 모드" 결정이 변경됩니다.

## 실행

```powershell
cd desktop\tauri
npm install
npm run tauri dev
```

배포용 빌드:

```powershell
npm run tauri build
```

> `src-tauri/icons/icon.ico`는 빌드 확인용 최소 placeholder입니다. 실제 배포용 설치 파일을
> 만들려면 `.ico`/`.png`/`.icns` 아이콘으로 교체하고 `tauri.conf.json`의 `bundle.icon`을 갱신하세요.

## 구조

```text
desktop\tauri\
  index.html              Vite 진입점
  package.json            Node 스크립트/의존성
  vite.config.js          프런트엔드 개발 서버 설정
  src\main.js             예제 UI 동작
  src\styles.css          예제 스타일
  src-tauri\
    Cargo.toml            Rust 크레이트 정의
    build.rs              tauri-build 진입점
    tauri.conf.json       Tauri 2 앱/번들 설정
    capabilities\default.json  기본 창 권한
    icons\icon.ico        빌드 확인용 placeholder 아이콘
    src\lib.rs            Tauri Builder 진입점
    src\main.rs           Windows 콘솔 창 숨김 + run()
```

## 범위

- 네트워크 요청, 토큰, 설정 파일을 사용하지 않습니다.
- 기존 `UpdateAgent.sln`, `scripts\publish.ps1`, `scripts\build-inno.ps1`은 변경하지 않습니다.
- 릴리스 산출물은 여전히 `artifacts\inno\*Setup*.exe` 두 개뿐입니다.

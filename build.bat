@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem ============================================================================
rem  UpdateAgent Agent build (Windows)
rem
rem  What this does
rem    1) Reads the version from Directory.Build.props (single version source).
rem    2) Publishes only the student-PC apps: Service, SessionHelper, Cli.
rem    3) Builds the Agent Inno Setup installer, whose file name carries the
rem       version: UpdateAgent.Agent-Setup-<version>-x64.exe
rem    4) Collects the result in a per-version folder and opens it in Explorer.
rem
rem  Output
rem    build\agent\<version>\UpdateAgent.Agent-Setup-<version>-x64.exe
rem    build\agent\<version>\UpdateAgent.Agent-Setup-<version>-x64.exe.sha256
rem    build\agent\<version>\build-info.json
rem
rem  Requirements
rem    .NET 10 SDK on PATH and Inno Setup 6 installed (ISCC.exe).
rem
rem  Usage
rem    Double-click build.bat, or run "build.bat" from a terminal.
rem ============================================================================

set "REPO_ROOT=%~dp0"
set "STAGING_DIR=%REPO_ROOT%build\.staging"
set "PUBLISH_ROOT=%STAGING_DIR%\publish"
set "INNO_OUT=%STAGING_DIR%\inno"
set "OUTPUT_ROOT=%REPO_ROOT%build\agent"

echo ============================================================
echo  UpdateAgent Agent build
echo ============================================================
echo.

rem --- 1) prerequisites -------------------------------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] The .NET SDK was not found on PATH.
    echo         Install the .NET 10 SDK, open a new terminal, then retry.
    goto :fail
)

rem --- 2) version -------------------------------------------------------------
set "VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Select-Xml -LiteralPath 'Directory.Build.props' -XPath '//Version').Node.InnerText"`) do set "VERSION=%%V"

if not defined VERSION (
    echo [ERROR] Could not read the version from Directory.Build.props.
    goto :fail
)

echo Version       : %VERSION%
echo Output folder : %OUTPUT_ROOT%\%VERSION%
echo.

if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"

rem --- 3) publish the agent ---------------------------------------------------
echo [1/3] Publishing the agent (Service, SessionHelper, Cli)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%scripts\publish.ps1" -AgentOnly -OutputRoot "%PUBLISH_ROOT%"
if errorlevel 1 (
    echo.
    echo [ERROR] Publishing the agent failed.
    goto :fail
)

rem --- 4) build the versioned installer ---------------------------------------
echo.
echo [2/3] Building the agent installer...
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%scripts\build-inno.ps1" -AgentOnly -NoPublish -ProductVersion "%VERSION%" -PublishRoot "%PUBLISH_ROOT%" -OutputRoot "%INNO_OUT%" -StagingRoot "%STAGING_DIR%\inno-stage"
if errorlevel 1 (
    echo.
    echo [ERROR] Building the agent installer failed.
    goto :fail
)

rem --- 5) assemble the per-version output folder ------------------------------
echo.
echo [3/3] Assembling the versioned output folder...

set "VERSION_DIR=%OUTPUT_ROOT%\%VERSION%"
set "SETUP_NAME=UpdateAgent.Agent-Setup-%VERSION%-x64.exe"

if exist "%VERSION_DIR%" rmdir /s /q "%VERSION_DIR%"
mkdir "%VERSION_DIR%" >nul 2>nul
if not exist "%VERSION_DIR%" (
    echo [ERROR] Could not create the output folder: %VERSION_DIR%
    goto :fail
)

copy /y "%INNO_OUT%\%SETUP_NAME%" "%VERSION_DIR%\" >nul
if errorlevel 1 (
    echo [ERROR] Installer was not produced: %INNO_OUT%\%SETUP_NAME%
    goto :fail
)

if exist "%INNO_OUT%\%SETUP_NAME%.sha256" (
    copy /y "%INNO_OUT%\%SETUP_NAME%.sha256" "%VERSION_DIR%\" >nul
)
if exist "%INNO_OUT%\build-info.json" (
    copy /y "%INNO_OUT%\build-info.json" "%VERSION_DIR%\" >nul
)

rem --- 6) clean up and open the result ----------------------------------------
rmdir /s /q "%STAGING_DIR%" >nul 2>nul

echo.
echo Build complete.
echo   Version   : %VERSION%
echo   Installer : %SETUP_NAME%
echo   Folder    : %VERSION_DIR%
echo.
start "" "%VERSION_DIR%"
exit /b 0

:fail
echo.
echo Build FAILED.
if exist "%STAGING_DIR%" (
    echo Intermediate output kept for troubleshooting:
    echo   %STAGING_DIR%
)
echo.
pause
exit /b 1

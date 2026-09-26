@echo off
rem YukiDesk one-click packaging. Builds 4 setup installers:
rem   YukiDesk-Setup-<arch>-full-<ver>.exe  self-contained (bundles .NET 8 runtime)
rem   YukiDesk-Setup-<arch>-slim-<ver>.exe  framework-dependent (needs .NET 8 Desktop Runtime)
rem for arch = x64 / arm64. Output goes to installers\.
rem Requirements: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
setlocal
cd /d "%~dp0"

rem Locate ISCC.exe: winget per-user install first, then system-wide paths.
set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo [ERROR] Inno Setup 6 not found. Install it with:
    echo     winget install JRSoftware.InnoSetup
    exit /b 1
)

for %%A in (x64 arm64) do (
    echo.
    echo ========== [%%A full] clean and publish ==========
    if exist "publish\win-%%A" rmdir /s /q "publish\win-%%A"
    dotnet publish -c Release -r win-%%A --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A full
        exit /b 1
    )
    >"publish\win-%%A\yukidesk.runtime" echo full

    echo ========== [%%A full] Inno Setup ==========
    "%ISCC%" /DARCH=%%A /DFLAVOR=full installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A full
        exit /b 1
    )

    echo ========== [%%A slim] clean and publish ==========
    if exist "publish\win-%%A-slim" rmdir /s /q "publish\win-%%A-slim"
    dotnet publish -c Release -r win-%%A --self-contained false -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A-slim"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A slim
        exit /b 1
    )
    >"publish\win-%%A-slim\yukidesk.runtime" echo slim

    echo ========== [%%A slim] Inno Setup ==========
    "%ISCC%" /DARCH=%%A /DFLAVOR=slim installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A slim
        exit /b 1
    )
)

echo.
echo Done. Installers:
dir /b "installers\YukiDesk-Setup-*.exe"
endlocal

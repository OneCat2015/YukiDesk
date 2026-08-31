@echo off
rem YukiDesk one-click packaging: publish win-x64 / win-arm64 (self-contained)
rem and build setup installers with Inno Setup. Output goes to installers\.
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
    echo ========== [%%A] dotnet publish ==========
    dotnet publish -c Release -r win-%%A --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "publish\win-%%A"
    if errorlevel 1 (
        echo [ERROR] publish failed for win-%%A
        exit /b 1
    )

    echo ========== [%%A] Inno Setup ==========
    "%ISCC%" /DARCH=%%A installer.iss
    if errorlevel 1 (
        echo [ERROR] installer failed for win-%%A
        exit /b 1
    )
)

echo.
echo Done. Installers:
dir /b "installers\YukiDesk-Setup-*.exe"
endlocal

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

rem 只保留当前版本资产:installers\ 里历史安装包堆积会让人工上传时拿错旧版本。
for /f "delims=" %%V in ('powershell -NoProfile -Command "(Get-Item 'publish\win-x64\YukiDesk.exe').VersionInfo.FileVersion"') do set "VER=%%V"
if not defined VER (
    echo [ERROR] 无法读取版本号,跳过 installers 清理与校验和生成
    exit /b 1
)
echo ========== Clean stale installers (keep %VER%) ==========
for %%F in ("installers\YukiDesk-Setup-*.exe") do (
    echo "%%~nxF" | findstr /C:"-%VER%.exe" >nul || del /f /q "%%F"
)

rem 生成 SHA256SUMS 清单:更新器会下载同版本 SHA256SUMS-<ver>.txt 校验安装包完整性。
set "SUMS=installers\SHA256SUMS-%VER%.txt"
if exist "%SUMS%" del /f /q "%SUMS%"
for %%F in ("installers\YukiDesk-Setup-%VER%.exe" "installers\YukiDesk-Setup-%VER%.0.exe") do (
    if exist "%%~fF" for /f "tokens=1" %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%%~fF').Hash.ToLower()"') do >>"%SUMS%" echo %%H  %%~nxF
)

echo.
echo Done. Assets for release:
dir /b "installers\YukiDesk-Setup-*.exe"
if exist "%SUMS%" (
    type "%SUMS%"
    echo Upload the SHA256SUMS-%VER%.txt above together with the 4 installers.
)
endlocal

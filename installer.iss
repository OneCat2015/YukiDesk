; YukiDesk 安装包脚本（Inno Setup 6）
; 不要直接运行本文件，请使用 packaging.cmd；它会对每个架构执行：
;   iscc /DARCH=x64 /DFLAVOR=full installer.iss   ← 自带 .NET 8 运行时（完整版）
;   iscc /DARCH=x64 /DFLAVOR=slim installer.iss   ← 依赖系统 .NET 8 桌面运行时（精简版）
; 发布目录 publish\win-<arch>(full) / publish\win-<arch>-slim(slim) 由 packaging.cmd 先生成。

#ifndef ARCH
  #error 请用 /DARCH 指定架构：x64 或 arm64
#endif
#if (ARCH != "x64") && (ARCH != "arm64")
  #error ARCH 只能是 x64 或 arm64
#endif

#ifndef FLAVOR
  #error 请用 /DFLAVOR 指定打包类型：full（自带 .NET 运行时）或 slim（依赖系统 .NET 8）
#endif
#if (FLAVOR != "full") && (FLAVOR != "slim")
  #error FLAVOR 只能是 full 或 slim
#endif

#if FLAVOR == "full"
  #define PublishDir "publish\win-" + ARCH
#else
  #define PublishDir "publish\win-" + ARCH + "-slim"
#endif

#define MyAppName "YukiDesk"
#define MyAppExe "YukiDesk.exe"
#define MyAppVersion GetVersionNumbersString(AddBackslash(SourcePath) + PublishDir + "\YukiDesk.exe")

[Setup]
AppId={{7C4B6E5A-2D93-4B18-9F61-0A8C3E52D7F01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=YukiDesk
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName} {#ARCH} {#FLAVOR}
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=installers
OutputBaseFileName=YukiDesk-Setup-{#ARCH}-{#FLAVOR}-{#MyAppVersion}
; 本程序数据全部在 %AppData%\YukiDesk，按每用户安装，无需管理员权限
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible or arm64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Assets\YukiDesk.ico
; 覆盖安装时若程序正在运行，由 Windows 重启管理器提示关闭
CloseApplications=yes

[Languages]
; 简体中文翻译文件随仓库分发（Inno Setup 官方安装器不一定自带）；缺失时回退英文
#if FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

#if FLAVOR == "slim"
[InstallDelete]
; full -> slim 覆盖升级时只清理 self-contained 的 native .NET runtime 文件。
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\clrjit.dll"
Type: files; Name: "{app}\ijwhost.dll"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\mscordaccore*.dll"
Type: files; Name: "{app}\mscordbi.dll"
Type: files; Name: "{app}\vcruntime140_cor3.dll"
Type: files; Name: "{app}\System.IO.Compression.Native.dll"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// 扫描 .NET 桌面运行时安装目录，寻找 8.x 版本子目录。
function DirHasNet8DesktopRuntime(root: string): Boolean;
var
  fr: TFindRec;
begin
  Result := False;
  if not DirExists(root) then Exit;
  if FindFirst(AddBackslash(root) + '*', fr) then
  try
    repeat
      if (fr.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        if Copy(fr.Name, 1, 2) = '8.' then
        begin
          Result := True;
          Exit;
        end;
    until not FindNext(fr);
  finally
    FindClose(fr);
  end;
end;

function HasNet8DesktopRuntime(): Boolean;
begin
  Result :=
    DirHasNet8DesktopRuntime(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    DirHasNet8DesktopRuntime(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
#if ARCH == "arm64"
  // arm64 安装包只在 ARM64 设备上原生运行；x64 安装包兼容 ARM64 模拟运行，故不做限制
  if not IsArm64() then
  begin
    MsgBox('此安装包为 ARM64 版本，请在 Windows on ARM 设备上运行。' + #13#10 +
           'x64 设备请使用 YukiDesk-Setup-x64 安装包。', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
#endif
#if FLAVOR == "slim"
  // slim 版不自带 .NET 运行时，检测不到 8.x 桌面运行时时提示但允许继续。
  if not HasNet8DesktopRuntime() then
  begin
    if MsgBox('此安装包为精简版（不自带 .NET 8 运行时），而系统中未检测到 .NET 8 桌面运行时。' + #13#10 +
              '没有它 YukiDesk 将无法启动。建议改用文件名带 full 的完整版安装包，' + #13#10 +
              '或先到 dotnet.microsoft.com 安装 ".NET Desktop Runtime 8"。' + #13#10 + #13#10 +
              '仍要继续安装吗？', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
#endif
end;

; YukiDesk 安装包脚本（Inno Setup 6）
; 不要直接运行本文件，请使用 packaging.cmd；它会对每个架构执行：
;   iscc /DARCH=x64 installer.iss    或    iscc /DARCH=arm64 installer.iss
; 发布目录 publish\win-<arch> 由 packaging.cmd 先用 dotnet publish 生成（自带 .NET 运行时）。

#ifndef ARCH
  #error 请用 /DARCH 指定架构：x64 或 arm64
#endif
#if (ARCH != "x64") && (ARCH != "arm64")
  #error ARCH 只能是 x64 或 arm64
#endif

#define MyAppName "YukiDesk"
#define MyAppExe "publish\win-" + ARCH + "\YukiDesk.exe"
#define MyAppVersion GetVersionNumbersString(AddBackslash(SourcePath) + MyAppExe)

[Setup]
AppId={{7C4B6E5A-2D93-4B18-9F61-0A8C3E52D7F01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=YukiDesk
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName} {#ARCH}
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=installers
OutputBaseFileName=YukiDesk-Setup-{#ARCH}-{#MyAppVersion}
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

[Files]
Source: "publish\win-{#ARCH}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
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
  end;
#endif
end;

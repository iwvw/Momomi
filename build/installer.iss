; Momomi 一键安装脚本（Inno Setup 6）
; 由 CI 调用：ISCC /DAppVersion=x.y.z /DSourceDir=... /DOutputDir=... installer.iss

#define AppName "Momomi"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef NameSuffix
  #define NameSuffix ""
#endif

[Setup]
AppId={{8F3A9C21-7B4E-4D5A-9C1F-2E6B8D4A7F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DSUK
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 允许免管理员的 per-user 安装到 %LocalAppData%
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=Momomi-{#AppVersion}-x64{#NameSuffix}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Momomi.exe

[Languages]
; 简体中文语言包不在 Inno 默认发行版内，由 CI 下载到 Languages 目录；
; 若缺失则自动跳过，仅用英文。
#ifdef ChineseISL
Name: "chinesesimplified"; MessagesFile: "{#ChineseISL}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "开机自动启动"; GroupDescription: "启动项"

[Files]
; 打包整个发布目录
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Momomi.exe"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Momomi.exe"; Tasks: desktopicon

[Registry]
; 开机自启（写当前用户 Run 键，与本程序内置的自启逻辑一致）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Momomi"; ValueData: """{app}\Momomi.exe"" --minimized"; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\Momomi.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时仅清理程序目录（用户数据在 %LocalAppData%\Momomi，保留以免误删订阅与配置）
Type: filesandordirs; Name: "{app}"

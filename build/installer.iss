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
; 分离版（框架依赖）置 1：安装前检测系统运行时，缺失则提示（不自动安装）。
#ifndef RequireRuntime
  #define RequireRuntime "0"
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
; 主程序 manifest 是 requireAdministrator（TUN 必需），安装器同样以管理员运行：
; 这样才能写入 Program Files、创建"登录时以最高权限运行"的计划任务，
; 并在安装完成后直接启动主程序而不触发错误 740（请求的操作需要提升）。
PrivilegesRequired=admin
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
; 打包整个发布目录（排除运行时生成的 data 数据目录）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Momomi.exe"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Momomi.exe"; Tasks: desktopicon

[Run]
; 主程序需管理员权限：自启改用计划任务（登录时以最高权限静默运行，不弹 UAC）。
Filename: "schtasks.exe"; Parameters: "/Create /TN ""Momomi"" /TR ""\""{app}\Momomi.exe\"" --minimized"" /SC ONLOGON /RL HIGHEST /F"; \
  Flags: runhidden; Tasks: startupicon; StatusMsg: "正在创建开机自启计划任务…"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""Momomi"" /F"; Flags: runhidden; Tasks: not startupicon
; 安装器已以管理员运行（PrivilegesRequired=admin），子进程继承管理员令牌，
; 直接启动即可；用 shellexec 反而会多弹一次 UAC。
Filename: "{app}\Momomi.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""Momomi"" /F"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

; 卸载时由安装器自动移除其安装的文件；运行期生成的 data（用户数据/内核/地理数据）
; 不在安装记录内，会被保留，避免误删订阅与配置。

[Code]
#if RequireRuntime == "1"
// 分离版：启动前检测 .NET 运行时 10.0 与 Windows App Runtime 2.6.4（均为 x64），缺失则提示（不自动安装）。
const
  DotnetUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  WinAppRuntimeUrl = 'https://aka.ms/windowsappsdk/2.6/2.6.4/windowsappruntimeinstall-x64.exe';

// 运行一段 PowerShell 探测命令，把结果写入临时文件并读取。
function RunProbe(const Script: String): String;
var
  ResultCode: Integer;
  Report: String;
  Output: AnsiString;
begin
  Report := ExpandConstant('{tmp}\momomi-rt.txt');
  if FileExists(Report) then DeleteFile(Report);
  if not Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "' + Script + ' | Out-File -Encoding ascii -FilePath ''' + Report + '''"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := '';
    exit;
  end;
  if FileExists(Report) and LoadStringFromFile(Report, Output) then
    Result := Trim(String(Output))
  else
    Result := '';
end;

function HasDotnet10(): Boolean;
begin
  // 检测已安装的 Microsoft.NETCore.App 10.x 运行时（.NET 运行时，非桌面运行时）。
  Result := Pos('10.', RunProbe('(dotnet --list-runtimes | Select-String ''Microsoft.NETCore.App 10\.'' | Select-Object -First 1)')) > 0;
end;

function HasWindowsAppRuntime(): Boolean;
begin
  // Windows App Runtime 2.6.4 以 Appx 包安装，用 Get-AppxPackage 检测最可靠。
  // 名称 Microsoft.WindowsAppRuntime.2 对应 2.x 系列。门槛与 MSIX 依赖声明的
  // MinVersion 保持一致（2.6.4.0），否则低版本 2.6.x 会通过检测却启动失败。
  Result := RunProbe('$(if (Get-AppxPackage Microsoft.WindowsAppRuntime.2 | Where-Object { $_.Version -ge [Version]''2.6.4'' }) { ''FOUND'' } else { ''MISSING'' })') = 'FOUND';
end;

function InitializeSetup(): Boolean;
var
  Missing: String;
begin
  Missing := '';
  if not HasDotnet10() then
    Missing := Missing + '  - .NET 运行时 10.0（x64）：' + DotnetUrl + #13#10;
  if not HasWindowsAppRuntime() then
    Missing := Missing + '  - Windows App Runtime 2.6.4（x64）：' + WinAppRuntimeUrl + #13#10;

  if Missing <> '' then
  begin
    if MsgBox('检测到缺少以下运行环境，Momomi 分离版需要它们才能启动（均须为 x64）：' + #13#10#13#10 +
              Missing + #13#10 +
              '是否仍要继续安装？（可稍后自行安装上述运行环境）',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      exit;
    end;
  end;
  Result := True;
end;
#endif

// 从旧版（PrivilegesRequired=lowest，卸载项写在 HKCU）升级上来时，
// 新版以管理员运行、卸载项改写到 HKLM。不清理 HKCU 的旧项会在
// 「应用和功能」里留下一个指向已失效路径的重复条目。
procedure CleanupLegacyUserUninstallKey();
var
  OldKey: String;
begin
  OldKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A9C21-7B4E-4D5A-9C1F-2E6B8D4A7F31}_is1';
  if RegKeyExists(HKEY_CURRENT_USER, OldKey) then
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, OldKey);
end;

// 读取旧版（HKCU）记录的安装目录，让升级沿用原位置。
// 不沿用的话，老用户升级后程序会跑到 Program Files，旧目录的 data
// （订阅/配置/内核）与旧 exe 都留在原地，出现两份程序。
function GetLegacyInstallDir(): String;
var
  OldKey: String;
  Dir: String;
begin
  OldKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A9C21-7B4E-4D5A-9C1F-2E6B8D4A7F31}_is1';
  Dir := '';
  if RegQueryStringValue(HKEY_CURRENT_USER, OldKey, 'InstallLocation', Dir) and (Dir <> '') then
    Result := Dir
  else
    Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanupLegacyUserUninstallKey();
end;

// 在目录页首次显示前把默认路径改成旧版位置。
// 不用 InitializeSetup 是为了避免与分离版的同名函数冲突（Inno 不允许重复定义）。
procedure CurPageChanged(CurPageID: Integer);
var
  Legacy: String;
  Default: String;
begin
  if CurPageID <> wpSelectDir then exit;

  // 只在用户还没改过目录时接管，避免覆盖他的手动选择。
  Default := RemoveBackslashUnlessRoot(ExpandConstant('{autopf}\{#AppName}'));
  if CompareText(RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text), Default) <> 0 then exit;

  Legacy := GetLegacyInstallDir();
  if (Legacy <> '') and FileExists(AddBackslash(Legacy) + 'Momomi.exe') then
    WizardForm.DirEdit.Text := RemoveBackslashUnlessRoot(Legacy);
end;



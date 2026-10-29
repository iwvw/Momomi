{{CHANGES}}

---

## 产物下载

点击文件名即可下载。

### 合并版（自包含，解压即用，无需预装运行时）

| 文件 | 类型 | 大小 | 说明 |
|---|---|---:|---|
| [`Momomi-{{VERSION}}-x64-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-setup.exe) | x64 安装包 | {{SIZE_X64_SETUP}} | 推荐。一键安装，可选开机自启与桌面快捷方式 |
| [`Momomi-{{VERSION}}-x64-portable.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-portable.zip) | x64 便携版 | {{SIZE_X64_PORTABLE}} | 解压即用，无需安装 |
| [`Momomi-{{VERSION}}-x64-full-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-setup.exe) | x64 完整版 安装包 | {{SIZE_X64_FULL_SETUP}} | 内置内核 + 地理数据库，开箱即用 |
| [`Momomi-{{VERSION}}-x64-full.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full.zip) | x64 完整版 便携包 | {{SIZE_X64_FULL}} | 内置内核 + 地理数据库，解压即用 |
| [`Momomi-{{VERSION}}-arm64-portable.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-arm64-portable.zip) | ARM64 便携版 | {{SIZE_ARM64_PORTABLE}} | ARM64 设备（如骁龙本） |
| [`Momomi-{{VERSION}}-arm64-full.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-arm64-full.zip) | ARM64 完整版 便携包 | {{SIZE_ARM64_FULL}} | ARM64 设备，内置内核 + 地理数据库 |

### MSIX（x64）

| 文件 | 类型 | 大小 | 说明 |
|---|---|---:|---|
| [`Momomi-{{VERSION}}.0-x64.msix`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}.0-x64.msix) | x64 MSIX 包 | {{SIZE_X64_MSIX}} | MSIX 打包形态，功能与安装版一致；安装前需先信任证书 |
| [`momomi-signing.cer`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/momomi-signing.cer) | 签名证书（公钥） | {{SIZE_MSIX_CER}} | 自签名证书，安装 MSIX 前需导入「受信任人」存储 |

安装前先信任证书（会弹一次 UAC），再安装：

```powershell
certutil -addstore -f TrustedPeople momomi-signing.cer
Add-AppxPackage .\Momomi-{{VERSION}}.0-x64.msix
```

> MSIX 版数据目录固定为 `%LOCALAPPDATA%\Momomi`，不支持应用内覆盖式自更新，升级请安装新的 MSIX 包。

### 分离版（框架依赖，体积最小，需预装运行时）

分离版仅提供 x64，须先安装运行环境方可运行，安装包在运行环境缺失时会给出下载提示。

| 文件 | 类型 | 大小 | 说明 |
|---|---|---:|---|
| [`Momomi-{{VERSION}}-x64-sep-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-sep-setup.exe) | x64 安装包 | {{SIZE_X64_SEP_SETUP}} | 一键安装；安装时检测运行环境并提示 |
| [`Momomi-{{VERSION}}-x64-sep.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-sep.zip) | x64 便携版 | {{SIZE_X64_SEP}} | 解压即用，需自备运行环境 |
| [`Momomi-{{VERSION}}-x64-full-sep-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-sep-setup.exe) | x64 完整版 安装包 | {{SIZE_X64_FULL_SEP_SETUP}} | 内置内核 + 地理数据库，需自备运行环境 |
| [`Momomi-{{VERSION}}-x64-full-sep.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-sep.zip) | x64 完整版 便携包 | {{SIZE_X64_FULL_SEP}} | 内置内核 + 地理数据库，需自备运行环境 |

#### 运行环境要求

须预先安装下列两个运行时，均为 **x64** 架构，缺一不可：

| 运行时 | 所需版本 | 下载地址 |
|---|---|---|
| Windows App Runtime | **2.6 或更高**（2.x） | [WindowsAppRuntimeInstall-x64.exe](https://aka.ms/windowsappsdk/2.6/2.6.4/windowsappruntimeinstall-x64.exe) |
| .NET 运行时 | **10.0** | [.NET 10 下载](https://dotnet.microsoft.com/download/dotnet/10.0) |

- 本应用基于 **Windows App SDK 2.6** 构建，Windows App Runtime 需 **2.6 或更高**的 2.x 版本，推荐直接安装上表的 2.6。
- .NET 请选择 **「.NET 运行时」**（组件名 `Microsoft.NETCore.App`），而非「.NET 桌面运行时」或「ASP.NET Core 运行时」，版本 10.0.x。
- 安装完成后可执行下列命令核对，两条均应有输出：

```powershell
dotnet --list-runtimes | Select-String 'Microsoft.NETCore.App 10\.'
Get-AppxPackage Microsoft.WindowsAppRuntime.2 | Select-Object Name, Version
```

若运行环境已按上表装齐仍无法启动，建议改用**合并版**（自带全部运行时，无需预装）。

### 版本选择建议

| 使用场景 | 推荐版本 |
|---|---|
| 一般用户 | **合并版 x64 安装包**（自带运行时，安装即用） |
| 追求最小体积，且已装 .NET 10 与 Windows App Runtime 2.6 | **分离版**（较合并版小约 100 MB） |
| 网络可正常访问 GitHub | 精简版（内核按需下载，体积更小） |
| 访问 GitHub 不便 / 需开箱即用 | **完整版**（内置 mihomo 内核与全部地理数据库） |
| ARM64 设备（如骁龙本） | 合并版 ARM64 便携包 |

## 注意事项

- **管理员权限**：主程序以管理员身份运行以支持 TUN。UAC 为默认级别时，每次启动会弹出一次授权提示。
- **分离版运行环境**：须预先安装 Windows App Runtime 2.6（x64）与 .NET 运行时 10.0（x64），安装器会检测并在缺失时给出下载地址，详见上方「分离版」章节。
- **端口 7890 冲突**：若同时运行 clash-party 等占用 7890 端口的程序，Momomi 的混合端口将绑定失败，请在设置中改用其他端口（如 7897）。
- **代码签名**：安装包未做代码签名，Windows SmartScreen 可能提示「未知发布者」，选择「仍要运行」即可。

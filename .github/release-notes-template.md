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

### 分离版（框架依赖，体积最小，需预装运行时）

> 需系统已安装 **.NET 10 桌面运行时** 与 **Windows App Runtime 2.x**，否则无法启动。仅提供 x64。

| 文件 | 类型 | 大小 | 说明 |
|---|---|---:|---|
| [`Momomi-{{VERSION}}-x64-sep-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-sep-setup.exe) | x64 安装包 | {{SIZE_X64_SEP_SETUP}} | 一键安装；安装时会检测运行时并提示 |
| [`Momomi-{{VERSION}}-x64-sep.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-sep.zip) | x64 便携版 | {{SIZE_X64_SEP}} | 解压即用，需自备运行时 |
| [`Momomi-{{VERSION}}-x64-full-sep-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-sep-setup.exe) | x64 完整版 安装包 | {{SIZE_X64_FULL_SEP_SETUP}} | 内置内核 + 地理数据库，需自备运行时 |
| [`Momomi-{{VERSION}}-x64-full-sep.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-sep.zip) | x64 完整版 便携包 | {{SIZE_X64_FULL_SEP}} | 内置内核 + 地理数据库，需自备运行时 |

### 选哪个？

| 场景 | 建议 |
|---|---|
| 大多数用户 | **合并版 x64 安装包**（自带运行时，装完即用） |
| 想体积最小、已装 .NET 10 与 Windows App Runtime | **分离版**（比合并版小约 100 MB） |
| 网络能正常访问 GitHub | 精简版（内核按需下载，体积更小） |
| 访问 GitHub 困难 / 想开箱即用 | **完整版**（内置 mihomo 内核与全部地理数据库） |
| ARM64 设备（如骁龙本） | 合并版 ARM64 便携包 |

## 注意事项

- **需要管理员权限**：主程序以管理员运行以支持 TUN。你的 UAC 若为默认级别，每次启动会弹一次授权提示。
- **分离版运行时**：需自行安装 .NET 10 桌面运行时与 Windows App Runtime 2.x，安装器会检测并给出下载地址。
- **端口 7890 冲突**：若同时运行 clash-party 等占用 7890 的程序，Momomi 的混合端口会绑定失败。请在设置中改用其他端口（如 7897）。
- 安装包未做代码签名，Windows SmartScreen 可能提示「未知发布者」，选择「仍要运行」即可。

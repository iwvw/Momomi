{{CHANGES}}

---

## 产物下载

点击文件名即可下载。

| 文件 | 类型 | 大小 | 说明 |
|---|---|---:|---|
| [`Momomi-{{VERSION}}-x64-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-setup.exe) | x64 安装包 | {{SIZE_X64_SETUP}} | 推荐。一键安装，可选开机自启与桌面快捷方式 |
| [`Momomi-{{VERSION}}-x64-portable.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-portable.zip) | x64 便携版 | {{SIZE_X64_PORTABLE}} | 解压即用，无需安装 |
| [`Momomi-{{VERSION}}-x64-full-setup.exe`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full-setup.exe) | x64 完整版 安装包 | {{SIZE_X64_FULL_SETUP}} | 内置内核 + 地理数据库，开箱即用 |
| [`Momomi-{{VERSION}}-x64-full.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-x64-full.zip) | x64 完整版 便携包 | {{SIZE_X64_FULL}} | 内置内核 + 地理数据库，解压即用 |
| [`Momomi-{{VERSION}}-arm64-portable.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-arm64-portable.zip) | ARM64 便携版 | {{SIZE_ARM64_PORTABLE}} | ARM64 设备（如骁龙本） |
| [`Momomi-{{VERSION}}-arm64-full.zip`](https://github.com/iwvw/Momomi/releases/download/v{{VERSION}}/Momomi-{{VERSION}}-arm64-full.zip) | ARM64 完整版 便携包 | {{SIZE_ARM64_FULL}} | ARM64 设备，内置内核 + 地理数据库 |

### 选哪个？

| 场景 | 建议 |
|---|---|
| 网络能正常访问 GitHub | 精简版（体积更小，内核按需下载） |
| 访问 GitHub 困难 / 想开箱即用 | **完整版**（内置 mihomo 内核与全部地理数据库） |
| 不确定架构 | 绝大多数 Windows 电脑选 **x64** |

## 注意事项

- **需要管理员权限**：主程序以管理员运行以支持 TUN。你的 UAC 若为默认级别，每次启动会弹一次授权提示。
- **端口 7890 冲突**：若同时运行 clash-party 等占用 7890 的程序，Momomi 的混合端口会绑定失败。请在设置中改用其他端口（如 7897）。
- 安装包未做代码签名，Windows SmartScreen 可能提示「未知发布者」，选择「仍要运行」即可。

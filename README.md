# ScrcpyX

ScrcpyX 是一个 Windows 下的安卓投屏工具。它把手机画面投到电脑上，并在同一个窗口里管理手机文件。

- 投屏由 adb 启动 scrcpy-server，程序自己解码 H.264，画面画在同一个窗口里。
- 投屏前选设备、改参数；投屏后这个窗口只做投屏和文件管理。
- 文件管理支持浏览、复制、剪切、粘贴、导入、导出、新建、重命名和删除。
- 不包含音频、录屏和场景方案。

> 本项目不是 Genymobile 的官方产品，基于官方 scrcpy 4.1 的 server 和 adb 构建。

## 系统要求

- Windows 10 1809 及以上，或 Windows 11，x64。
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。
- [Windows App Runtime 1.8](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)。
- 安卓手机，开启 USB 调试；无线连接需要先执行一次 `adb connect <手机地址>`。

程序启动时会检查上面两项运行时。缺少任何一项，会打开对应的下载页并给出提示。

## 安装

**安装包**：运行 `ScrcpyX-Setup-<版本>.exe`，按向导安装。安装到 `%LOCALAPPDATA%\Programs\ScrcpyX`，不需要管理员权限。卸载时程序文件会被删除，`data` 目录中的个人设置会保留。

**免安装**：解压 zip 包后直接运行 `ScrcpyX.exe`。程序使用同目录下的 `DLL\adb.exe` 和 `DLL\scrcpy-server`。

设置和窗口位置保存在程序目录下的 `data` 中。`data` 不可写时程序会拒绝启动。

## 使用

1. 用数据线连上手机，或用无线调试连接，在手机上允许 USB 调试。
2. 打开 ScrcpyX，在首页点「开始投屏」。
3. 投屏后，左侧按钮为「停止投屏」「返回」「主页」「任务」「文件」。中间是手机画面，可以直接点按。黑边区域不会发送到手机。
4. 点左侧「文件」展开文件面板。竖屏时面板在右侧，横屏时在下方。

投屏期间不能切换设备，也不能修改参数；停止投屏后再开始，新参数才生效。关闭窗口会停止投屏。

### 高级选项

首页的「高级选项」目前只有 5 个参数：

- 最大画面尺寸
- 最大帧率
- 视频比特率
- 保持设备唤醒
- 启动后关闭设备屏幕

留空时使用默认值。其余 scrcpy 参数（参数清单共 99 项）暂未接入界面。

### 延迟显示

左栏底部显示两个数字：

- **渲染**：从收到帧到画面绘制完成的时间，滚动平均。
- **ADB 往返**：定时执行 `adb shell` 的耗时。

这两个数字都不是端到端延迟。端到端延迟需要外部设备测量。

## 已知限制

- 不支持音频、录屏、场景方案，也没有第二个投屏窗口。
- 导入、导出使用系统的文件夹选择框。该对话框由 Windows 提供，偶尔会崩溃，这是系统组件的问题。
- Windows 10 没有 Segoe Fluent Icons 字体，界面图标会回退到 Segoe MDL2 Assets。已经做过回退，但个别图标在两个系统上的样式可能不同。

## 开发

需要 .NET 10 SDK 和 Windows 10/11 x64。界面使用 WinUI 3（Windows App SDK）。

```powershell
dotnet build .\src\ScrcpyX\ScrcpyX.csproj -c Release
dotnet run --project .\tests\ScrcpyX.SelfTest\ScrcpyX.SelfTest.csproj -c Release
.\Publish.ps1
```

- `Publish.ps1` 生成框架依赖的发布目录和 zip 包。
- `installer\ScrcpyX.iss` 是 Inno Setup 6 安装脚本，需要先安装 Inno Setup 6。
- `tools\Generate-ScrcpyManifest.ps1` 从 `DLL\scrcpy.exe --help` 生成参数清单 `DLL\scrcpy-options.json`。

## 目录结构

```
src/ScrcpyX/          应用源码（WinUI 3）
tests/ScrcpyX.SelfTest/  自测程序
tools/                参数清单生成脚本
installer/            安装包脚本
DLL/                  adb、scrcpy-server 及随包的 scrcpy 运行文件与许可证
Publish.ps1           发布脚本
```

## 第三方组件

- **scrcpy 4.1**（Apache License 2.0）：`DLL\scrcpy-server`、`DLL\scrcpy.exe` 等。版权与许可证见 `DLL\licenses\`。
- **adb、SDL、FFmpeg、libusb**：随官方 scrcpy 包分发，各自的声明和源码链接见 `DLL\licenses\THIRD_PARTY_NOTICES.txt`。
- **Windows App SDK、.NET**：由各自的运行时安装包提供，不随本项目分发。
- **程序图标**：铸图标 by CSS GG on [Icon-Icons.com](https://icon-icons.com/zh/authors/1235-css-gg)。

## 许可证

ScrcpyX 使用 [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0) 发布，**仅限非商业用途**。全文见 [LICENSE](LICENSE)。

第三方组件（如 scrcpy）仍遵循各自的许可证，见上面的第三方组件部分。

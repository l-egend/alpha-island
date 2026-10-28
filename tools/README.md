# 项目脚本

日常开发使用 Visual Studio 或 `dotnet build`，无需运行安装、证书或发布脚本。所有命令从仓库根目录执行。

## 构建与检查

| 入口 | 用途 |
| --- | --- |
| `Test-Maintenance.ps1` | 按功能运行离线回归；`-Configuration Debug -Only '--multi-display-only'` 只检查多显示器定位。 |
| `RegressionTests/`、`PluginFixture/` | WPF、服务与插件的回归程序和隔离测试插件。 |
| `AgentBridgeTests/` | 外部助手桥接与安装脚本的 Node.js 检查；部分用例需要对应工具或测试包。 |
| `build-traffic-monitor-bridge.ps1` | 构建可选 TrafficMonitor 原生桥接，需要 x64 MinGW g++；MSVC 可使用对应 vcxproj。 |
| `Test-PackageCompatibility*.ps1`、`Test-ShortcutIcons.ps1` | 检查已有发布目录的运行库、资源和快捷方式。 |

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Maintenance.ps1 -Configuration Debug -Only '--multi-display-only'
```

检查日志写入 `bin/maintenance-tests-*`；测试中的示例数据与实际设备测量会分别标明。完整检查的前置条件见 [开发与构建](../docs/开发与构建.md)。

## 资源维护

| 入口 | 用途 |
| --- | --- |
| `make-icon.ps1` | 重新生成程序图标。 |
| `make-notification-sound.ps1` | 生成内置默认通知音效。 |
| `Export-GeminiAnimations.py` | 从指定视频和参考图生成助手状态动画，支持各内置助手类型。需 Pillow、NumPy、imageio-ffmpeg。 |
| `IconReviewExporter/` | 导出项目图标供视觉审阅，见其目录说明。 |

资源脚本只在修改对应资源时执行；可能覆盖指定的输出文件。第三方素材来源见各 `Assets/*/SOURCES.md`。

## 诊断与接入

| 入口 | 用途 |
| --- | --- |
| `Collect-CodexIslandDiagnostics.ps1` | 收集外部助手接入诊断，避免输出密钥。 |
| `measure-background-cpu.ps1` | 测量已运行进程的后台 CPU。 |
| `Test-WindowsNotification.ps1` / `.cmd` | 发送真实测试通知，会在当前桌面产生提示。 |
| `Test-InstalledNotificationTakeover.ps1` | 检查已安装程序的通知接管。 |
| `agent-bridge-smoke.ps1` | 检查已运行的本地桥接，会发送测试状态和审批请求。 |
| `OneClickCodex/` | 生成及使用可搬移的助手接入包，需显式提供或准备经校验的 Node.js 运行库。 |

## 发布与安装维护

安装包默认保留最近两个版本。`Clean-BuildArtifacts.ps1` 默认只显示计划，`-Apply` 执行清理；边界与保留规则由 `Test-BuildArtifactRetention.ps1` 验证。

| 入口 | 用途 |
| --- | --- |
| `build-installer.ps1` | 完成更新后生成 EXE 安装包；成功后自动只保留最近两个版本并清理旧展开目录。 |
| `build-msix.ps1 -CreatePackage` | 可选的开发 MSIX 打包，需要 Windows SDK 与开发签名；省略开关不会打包。 |
| `Install-NotificationBuild.ps1`、`Trust-NotificationPublisher.ps1` | 可选 MSIX 安装与开发证书信任，须审核后明确选择。 |
| `package-earbuds-plugin.ps1`、`package-vivo-plugin.ps1` | 打包独立设备插件。 |
| `Update-TestInstallation.ps1`、`Verify-TestInstallation.ps1` | 完成构建后同步升级已授权的检阅安装、重启并核验配置保留；须显式传入实际安装目录。安装包生成后仍需执行部署步骤。 |
| `Verify-SettingsMigration.ps1`、`Verify-ClipboardHistoryRuntimeUpdate.ps1` | 核验迁移或升级审计，不能替代通用单元测试。 |
| `test-installer.ps1` | 安装器专用测试；执行前阅读参数和前置条件。 |

`packaging/` 内的脚本是安装器载荷，`examples/*/install.ps1` 是对应集成的安装入口。普通应用启动不会执行这些开发脚本。

# 开发工具

本目录保留可复用的构建、离线回归、资源维护与脱敏诊断入口。运行日志和生成文件写入 `bin/`，不提交到仓库。

| 入口 | 用途 |
| --- | --- |
| `Test-Maintenance.ps1`、`RegressionTests/` | 按功能运行回归；包含可选插件的扩展回归需准备独立插件源码。 |
| `PluginFixture/`、`PluginCrashFixture/` | 隔离测试插件，只用于回归，不随应用安装。 |
| `AgentBridgeTests/` | 外部助手接入与协议检查。 |
| `LocalizationMaintenance/` | 校验中英文资源、文案引用和格式参数。 |
| `build-installer.ps1` | 构建自包含 EXE 安装包；从项目读取版本，成功后保留最近两个版本。 |
| `Clean-BuildArtifacts.ps1`、`Test-BuildArtifactRetention.ps1` | 在项目边界内预览、执行并核验构建产物清理。 |
| `Test-PackageCompatibility*.ps1`、`PackageImageMetadata.cs.txt` | 核验发布目录中的运行库、架构、依赖和个人文件排除。 |
| `Test-ShortcutIcons.ps1` | 检查快捷方式图标资源。 |
| `Collect-CodexIslandDiagnostics.ps1` | 导出脱敏的助手接入诊断。 |
| `Test-RepositoryPrivacy.py`、`privacy-reviewed.json` | 只读隐私检查，以及逐行核对过的测试样例放行记录。 |
| `OneClickCodex/` | 外部助手接入所需工具，使用前核对参数和运行环境。 |
| `make-icon.ps1`、`make-notification-sound.ps1`、`Export-GeminiAnimations.py`、`IconReviewExporter/` | 图标、内置音效和动画资源维护。 |

本机检阅安装、旧版迁移核验、实机探针、开发签名及批量插件打包命令保存在源码目录之外，不进入仓库。可选的实机通知回归需另行提供本机工具；默认离线回归不依赖它们。

`packaging/` 中的维护脚本、`examples/` 中的助手适配器属于产品构建或运行所需文件，继续保留。可选插件源码位于同级独立目录 `alpha-island-plugins/`，发布产物和本地工具均不属于源码。

# Alpha Island 快速开始

[简体中文](QUICKSTART.zh-CN.md) · [English](QUICKSTART.en.md) · [项目介绍](../README.md)

## 安装与更新

支持 Windows 10 2004（19041）及以上或 Windows 11 x64。下载主程序 Release 中的 `AlphaIsland-0.2.32-win-x64-Setup.exe` 并运行；安装包自带 .NET 运行库，通常按当前 Windows 用户安装。

更新前从托盘退出 Alpha Island，再运行安装包。保留原安装目录可以覆盖升级；用户配置与插件不随程序文件替换。卸载时请留意是否选择删除用户数据。

## 第一次使用

1. 从托盘打开设置，选择语言、外观和需要的组件。
2. 播放媒体后在主面板查看状态；单击或悬停可展开面板，右键可置顶支持的内容。
3. 使用右侧组件切换控件切换组件，长按打开组件菜单。卡片内的滚轮用于滚动内容。
4. 在“屏幕工具”使用 OCR、颜色吸管或扫码。语音识别与区域翻译是不同模式；区域翻译需先框选画面。
5. 通知、定位和语音可能需要 Windows 授权。设备电量依赖设备、驱动和协议，未知读数不会自动变成 0%。

## 可选插件

插件不在主安装包内。打开 [插件 Release](https://github.com/l-egend/alpha-island/releases/tag/plugins-v0.2.32) 下载所需的安装 ZIP，或下载全部插件合并包。

在“设置 → 插件 → 打开插件目录”中，放入解压后的整个插件目录（应含 `plugin.json` 和 DLL），再刷新并启用。不要放入源码 ZIP，也不要把 DLL 单独复制进去。更新前退出程序，再替换相同 ID 的完整目录。

API 对话需要地址、模型和密钥；本机 Agent 需要对应工具已经可以正常对话。仅“找到程序”不表示已完成登录或状态接入。以插件各自的中英文说明为准。TrafficMonitor 另需安装其原生桥接。

## 配置与数据

| 内容 | 默认位置 |
| --- | --- |
| 设置、本地记录 | `%LOCALAPPDATA%\alpha-island\config` |
| 用户插件 | `%LOCALAPPDATA%\alpha-island\plugins` |

优先使用设置中的打开目录按钮确认实际位置。迁移前退出程序并备份上述目录；DPAPI 密钥不能直接供另一 Windows 用户使用，应在新机器重新填写。主程序安装包不包含开发者的个人配置。

## 排查与反馈

先确认版本与组件开关，再检查 Windows 权限或已配置服务的连接测试。游戏模式会收起界面并降低后台工作量；退出后再检查被暂停的组件。

通过设置导出诊断包，检查脱敏结果后附上复现步骤。请勿提交原始配置、密钥、对话内容或私人截图。报告设备问题时注明型号、连接方式和 Windows 版本；界面问题请注明显示器分辨率和缩放。

[主程序下载](https://github.com/l-egend/alpha-island/releases/tag/v0.2.32) · [插件目录](https://github.com/l-egend/alpha-island/tree/plugins) · [提交问题](https://github.com/l-egend/alpha-island/issues)

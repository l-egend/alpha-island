# 本机 Agent 插件

Codex 使用 App Server，DeepSeek Harness 使用 SDK 协议，Antigravity 使用官方 CLI 的 stream-json。复用各应用已有登录，不读取或复制凭据。桌面版 Antigravity 的 agentapi 依赖桌面内部服务地址，不能替代 CLI；只有安装并登录 CLI 后才启用该提供方。

选择任务目录后发送；不会接管各应用已经打开的对话。新建清除本插件当前上下文。取消、切换目录、禁用、卸载都会结束本插件拥有的进程，原应用继续运行。需要确认的 Codex 操作在岛内允许一次或拒绝；Harness 沿用岛的现有原生审批桥。Antigravity 保留自己的权限配置，无人回答的 Ask 操作会拒绝，不开启跳过权限选项。

本机 Agent 仍使用对应账号的模型额度。配置在 config/options.json，任务记录在 config/plugins/ai.local-agents/last-conversation.json；不复制第三方应用的历史。

程序发现支持 PATH、npm、桌面版、MSIX 和 VS Code 扩展的常见 Codex 布局。插件设置提供重新检测、手动选择 codex.exe 和重置路径；发现 CLI 不等于已验证账号。外部应用状态接入还需要 Hooks 和原生信任，与本插件的 CLI 检测分别排查。

文件中转站中的“发送至本机 Agent”会附加最多 16 个文件或文件夹并打开本卡片，输入指令后发送。Codex 图片使用原生 localImage，文档及文件夹使用本地路径引用；保持原有访问审批，不移动原文件，不清空中转条目。取消和失败保留附件，游戏暂停期间拒绝唤醒。

诊断导出会过滤常见凭据与个人路径，保留插件运行状态和程序发现结果。完整操作与验证边界见[诊断脱敏与本机 Agent 文件接入](../../docs/诊断脱敏与本机Agent文件接入.md)。

接口资料：[Codex](https://developers.openai.com/codex/app-server)、[Harness](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/sdk)、[Antigravity](https://www.antigravity.google/docs/cli/headless/)。

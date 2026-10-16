# 本机 Agent 插件

Codex 使用 App Server，DeepSeek Harness 使用 SDK 协议，Antigravity 使用官方 CLI 的 stream-json。复用各应用已有登录，不读取或复制凭据。桌面版 Antigravity 的 agentapi 依赖桌面内部服务地址，不能替代 CLI；只有安装并登录 CLI 后才启用该提供方。

选择任务目录后发送；不会接管各应用已经打开的对话。新建清除本插件当前上下文。取消、切换目录、禁用、卸载都会结束本插件拥有的进程，原应用继续运行。需要确认的 Codex 操作在岛内允许一次或拒绝；Harness 沿用岛的现有原生审批桥。Antigravity 保留自己的权限配置，无人回答的 Ask 操作会拒绝，不开启跳过权限选项。

本机 Agent 仍使用对应账号的模型额度。配置在 config/options.json，任务记录在 config/plugins/ai.local-agents/last-conversation.json；不复制第三方应用的历史。

接口资料：[Codex](https://developers.openai.com/codex/app-server)、[Harness](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/sdk)、[Antigravity](https://www.antigravity.google/docs/cli/headless/)。

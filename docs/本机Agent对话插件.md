# 本机 Agent 对话插件

设置 → 插件 → 本机 Agent 中可以重新检测、禁用或卸载插件。检测到可调用的程序后，岛和托盘显示“本机 Agent”。首次发送需选择任务目录。

- Codex：使用本机 Codex 的 App Server，在独立线程中发送并接收回复，使用原有登录和模型设置。
- DeepSeek Harness：使用安装好的 Harness SDK 协议和原应用登录。默认 `deepseek-flash`；模型覆盖项在集中配置的 `plugin/ai.local-agents/harness-model` 中。审批继续通过已有 Harness 审批桥。
- Antigravity：适配官方 CLI `agy` 的流式协议。仅有桌面应用不足以启用此提供方，需要安装并登录官方 CLI。本机目前未检测到 CLI。

本机 Agent 仍使用对应账号的额度；不使用岛的 API 密钥。插件不会接管原应用中已打开的对话。插件的“新建”重置自己的会话；停止任务、禁用或卸载会结束本插件启动的进程，其他原应用进程不受影响。空闲时不保留 Agent 子进程。

Codex 操作审批只允许本次或拒绝，界面显示命令或文件差异；缺少可审阅内容、额外权限扩展和不支持的交互请求会拒绝。Antigravity 沿用自身权限配置，无人回答的 Ask 操作不会自动放行。

目录和提供方选择保存在 `config/options.json`。最近任务记录和原生会话标识保存在 `config/plugins/ai.local-agents/last-conversation.json`，重启后可继续本插件的会话；不复制第三方应用历史。记录以本地 JSON 保存，可在停用插件后备份或删除。禁用、卸载不会删除记录。

接口资料：[Codex App Server](https://developers.openai.com/codex/app-server)、[Harness SDK](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/sdk)、[Antigravity CLI](https://www.antigravity.google/docs/cli/headless/)。

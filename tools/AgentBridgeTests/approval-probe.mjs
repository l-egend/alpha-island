// SDK smoke test only: asks for a decision but never executes an operation.
export const name = 'alpha-island-approval-probe';
export const inject = ['approval'];
export function apply(ctx) {
  const tested = new Set();
  ctx.on('agent/pre-step', async ({ agent, signal }, next) => {
    if (!tested.has(agent.id)) {
      tested.add(agent.id);
      await ctx.approval.request({ agent, signal, toolName: 'connection_probe',
        reason: 'Alpha Island 连接自检；不执行文件或命令操作，可等待 5 秒自动拒绝。' });
    }
    return next();
  });
}

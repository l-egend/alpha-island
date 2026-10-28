using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using alpha_island.Core;

namespace alpha_island
{
    public partial class MainWindow
    {
        /// <summary>
        /// 调试钩子：<c>ALPHA_ISLAND_PIN=&lt;层名&gt;</c> —— 开机若干秒后在 UI 线程直接调 ③A 的置顶生产路径
        /// <see cref="SetLayerPinned"/>（右键走的就是它），可选参数：
        /// <list type="bullet">
        ///   <item><c>ALPHA_ISLAND_PIN_DELAY=&lt;秒&gt;</c>：多久之后动手（默认 8）。</item>
        ///   <item><c>ALPHA_ISLAND_PIN_OFF=&lt;秒&gt;</c>：再过多少秒自动取消置顶（默认不取消）。</item>
        ///   <item><c>ALPHA_ISLAND_PIN_SELFTEST=1</c>：顺带跑一遍语义自检，逐步打 <c>[pincheck] step=… ok=…</c>。</item>
        /// </list>
        /// <para>
        /// 为什么要有它：本机前台经常被全屏游戏占住（`Client-Win64-Shipping` / 类 `UnrealWindow`），
        /// 注入的鼠标消息到不了岛窗（`IslandBody_PreviewMouseRightButtonUp` 一次都不会触发），
        /// 真点击没法做确定性验收；右键路由本身已用一次真点击人工验证过，这里只驱动同一条生产路径。
        /// </para>
        /// </summary>
        private void StartPinDebugHook()
        {
            bool selfTest = Environment.GetEnvironmentVariable("ALPHA_ISLAND_PIN_SELFTEST") == "1";
            string? raw = Environment.GetEnvironmentVariable("ALPHA_ISLAND_PIN");
            if (string.IsNullOrWhiteSpace(raw) && !selfTest) return;

            string key = string.IsNullOrWhiteSpace(raw) ? "download" : raw!.Trim();
            double delay = ParseSeconds("ALPHA_ISLAND_PIN_DELAY", 8.0);
            double offAfter = ParseSeconds("ALPHA_ISLAND_PIN_OFF", 0.0);

            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(delay),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                IslandLog.Debug("Pin", $"调试钩子：置顶 key={key} selftest={selfTest} delay={delay:0.#}s off={offAfter:0.#}s");

                if (selfTest)
                {
                    RunPinSelfTest(key);
                    return;
                }

                SetLayerPinned(key, true);
                if (offAfter <= 0) return;

                var off = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(offAfter),
                };
                off.Tick += (_, _) =>
                {
                    off.Stop();
                    SetLayerPinned(key, false);
                };
                off.Start();
            };
            timer.Start();
        }

        /// <summary>
        /// ③A 置顶语义自检（只在 <c>ALPHA_ISLAND_PIN_SELFTEST=1</c> 时跑）。每一步都打一行 ASCII，
        /// 验收脚本只认 <c>ok=True</c> 与最后的 <c>result=ALL-OK</c>。
        /// 断言的是用户能感到的四件事：钉住后①模块自己撤不掉、②超时也收不回、③一段时间内还占着格子、
        /// ④解除后立刻恢复正常（撤下/超时重新生效）。
        /// </summary>
        private async void RunPinSelfTest(string key)
        {
            const string tag = "[pincheck]";

            bool visible = false;
            for (int i = 0; i < 60 && !visible; i++)
            {
                visible = _layers.IsVisible(key);
                if (!visible) await Task.Delay(200);
            }
            IslandLog.Debug("Pin", $"{tag} step=visible key={key} ok={visible}");
            if (!visible)
            {
                IslandLog.Debug("Pin", $"{tag} result=FAILED reason=layer-never-showed");
                return;
            }

            bool pinOk = SetLayerPinned(key, true) && _layers.IsPinned(key);
            IslandLog.Debug("Pin", $"{tag} step=pin ok={pinOk}");

            _layers.Hide(key);
            bool hideBlocked = _layers.IsPinned(key) && _layers.IsShowing(key);
            IslandLog.Debug("Pin", $"{tag} step=hide-blocked ok={hideBlocked}");

            _layers.ExpireIfStale(key);
            bool expireBlocked = _layers.IsShowing(key);
            IslandLog.Debug("Pin", $"{tag} step=expire-blocked ok={expireBlocked}");

            await Task.Delay(4000);
            bool stay = _layers.IsPinned(key) && _layers.IsActive(key);
            IslandLog.Debug("Pin", $"{tag} step=stay ok={stay} winner={_layers.LastWinnerKey ?? "-"}");

            // 少一条"自动收回"的出口（置顶时不该收成细条）：直接叫一次生产的收回方法，
            // 守卫必须拦住它（[pin=hold]），形态留在胶囊。
            CollapseToBar();
            await Task.Delay(400);
            bool held = _state == IslandState.Pill && _layers.AnyPinned;
            IslandLog.Debug("Pin", $"{tag} step=hold-guard ok={held} state={_state}");

            // 更高的优先级仍然可以临时打断（系统告警 68 > 下载 66），它自己超时后钉子层必须回来。
            ShowSystemNotify("\uE950", "置顶自检", "临时打断", "-", 2.0);
            await Task.Delay(700);
            bool preempted = _layers.LastWinnerKey == "system";
            IslandLog.Debug("Pin", $"{tag} step=preempt ok={preempted} winner={_layers.LastWinnerKey ?? "-"}");
            await Task.Delay(2600);
            bool cameBack = _layers.LastWinnerKey == key && _layers.IsActive(key);
            IslandLog.Debug("Pin", $"{tag} step=return ok={cameBack} winner={_layers.LastWinnerKey ?? "-"}");

            bool unpinOk = !SetLayerPinned(key, false) && !_layers.IsPinned(key);
            IslandLog.Debug("Pin", $"{tag} step=unpin ok={unpinOk}");

            _layers.Hide(key);
            await Task.Delay(400);
            bool gone = !_layers.IsShowing(key);
            IslandLog.Debug("Pin", $"{tag} step=hide-after ok={gone}");

            bool all = pinOk && hideBlocked && expireBlocked && stay && held && preempted && cameBack && unpinOk && gone;
            IslandLog.Debug("Pin", $"{tag} result={(all ? "ALL-OK" : "FAILED")}");
        }

        /// <summary>读一个正数秒数的环境变量；没写/写坏/非正数一律用 <paramref name="fallback"/>。</summary>
        private static double ParseSeconds(string name, double fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0)
            {
                return value;
            }
            return fallback;
        }
    }
}

using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void OutsideCardClickUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        try
        {
            layers.HideAll(); SetAgentField(window, "_clipboardLocked", false);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            layers.ShowSeconds("volume", 20);
            SetAgentField(window, "_componentSelectedTick", Environment.TickCount64 - 1000);
            SetAgentField(window, "_componentCardAwaitingPointer", true);
            PrivateCall(window, "OnGlobalButtonDown", null, new MouseButtonDownEventArgs(1000000, 1000000, NativeMethods.WM_LBUTTONDOWN));
            PumpAnimations(40); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsActive("volume"),
                "岛外点击结束选页停留并把卡片缩为小岛，音量提示仍正常保留");
            Check(!AgentField<bool>(window, "_componentCardAwaitingPointer"), "岛外点击不再被选页等待标志持续阻止收回");
        }
        finally { layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true); }
    }
}

namespace alpha_island.Core
{
    /// <summary>
    /// 灵动岛的形态定义。数值大小同时用于判断"变大"还是"变小"，不要随意调整顺序。
    /// Bar = 贴顶待机细条；Pill = 悬停第一级（长椭圆）；Tray = 中间托盘（文件中转站）；Card = 完整卡片。
    /// </summary>
    public enum IslandState
    {
        Bar = 0,
        Pill = 1,
        Tray = 2,
        Card = 3
    }
}

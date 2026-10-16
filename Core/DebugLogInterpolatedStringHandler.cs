using System.Runtime.CompilerServices;

namespace alpha_island.Core;

/// <summary>过滤 Debug 日志时连插值表达式也不求值，避免热路径先构造无用字符串。</summary>
[InterpolatedStringHandler]
public ref struct DebugLogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;
    public bool Enabled { get; }
    public DebugLogInterpolatedStringHandler(int literalLength, int formattedCount, out bool enabled)
    {
        Enabled = enabled = IslandLog.IsEnabled(IslandLogLevel.Debug);
        _builder = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);
    internal string GetText() => _builder.ToStringAndClear();
}
